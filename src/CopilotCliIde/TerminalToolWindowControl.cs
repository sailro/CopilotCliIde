using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CopilotCliIde;

// WPF control hosting a WebView2 instance with xterm.js for terminal rendering.
// Attaches to TerminalSessionService for process I/O.
internal sealed class TerminalToolWindowControl : UserControl, IDisposable
{
	private readonly WebView2 _webView;
	private TerminalSessionService? _sessionService;
	private bool _webViewReady;
	private bool _sessionStartedByResize;
	private readonly OutputLogger? _logger;

	public TerminalToolWindowControl()
	{
		_logger = VsServices.Instance.Logger;

		_webView = new WebView2
		{
			DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 30)
		};

		Content = _webView;

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
#pragma warning disable VSSDK007 // Fire-and-forget is intentional for UI event handler
		_ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
#pragma warning restore VSSDK007
		{
			try
			{
				await InitializeWebViewAsync();
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				AttachToSession();
			}
			catch (Exception ex)
			{
				VsServices.Instance.Logger?.Log($"Terminal control: load failed: {ex}");
			}
		});
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		DetachFromSession();
	}

	private async System.Threading.Tasks.Task InitializeWebViewAsync()
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

		var logger = VsServices.Instance.Logger;
		logger?.Log("Terminal control: initializing WebView2...");

		var cachePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"CopilotCliIde", "webview2");
		Directory.CreateDirectory(cachePath);

		var env = await CoreWebView2Environment.CreateAsync(null, cachePath);
		await _webView.EnsureCoreWebView2Async(env);

		logger?.Log("Terminal control: WebView2 core initialized");

		// Map the Terminal resources folder to a virtual hostname
		var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
		var terminalDir = Path.Combine(extensionDir, "Resources", "Terminal");
		logger?.Log($"Terminal control: mapping resources from {terminalDir}");

		_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
			"copilot-cli.local", terminalDir, CoreWebView2HostResourceAccessKind.Allow);

		_webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

		_webView.CoreWebView2.NavigationCompleted += (_, args) =>
		{
			if (args.IsSuccess)
			{
				logger?.Log("Terminal control: navigation succeeded");
			}
			else
			{
				logger?.Log($"Terminal control: navigation failed — status {args.WebErrorStatus}");
			}
		};

		_webView.CoreWebView2.DOMContentLoaded += (_, _) =>
		{
			_webViewReady = true;
			logger?.Log("Terminal control: DOM ready, terminal active");
		};

		_webView.CoreWebView2.Navigate("https://copilot-cli.local/terminal.html");
	}

	private void AttachToSession()
	{
		_sessionService = VsServices.Instance.TerminalSession;
		if (_sessionService == null)
			return;

		_sessionService.OutputReceived += OnOutputReceived;
		_sessionService.ProcessExited += OnProcessExited;

		// Don't start the session here — wait for the first resize message
		// from xterm.js so ConPTY is created with the correct dimensions.
		_sessionStartedByResize = false;
	}

	private void DetachFromSession()
	{
		if (_sessionService == null)
			return;

		_sessionService.OutputReceived -= OnOutputReceived;
		_sessionService.ProcessExited -= OnProcessExited;
		_sessionService = null;
	}

	private void OnOutputReceived(string data)
	{
		if (!_webViewReady)
			return;

#pragma warning disable VSSDK007 // Fire-and-forget is intentional for event handler
		_ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
#pragma warning restore VSSDK007
		{
			try
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				var message = JsonSerializer.Serialize(new { type = "output", data });
				_webView.CoreWebView2?.PostWebMessageAsJson(message);
			}
			catch (Exception)
			{
				// WebView may be disposed
			}
		});
	}

	private void OnProcessExited()
	{
		var exitMessage = "\r\n\x1b[90m[Process exited. Press Enter to restart.]\x1b[0m\r\n";
		OnOutputReceived(exitMessage);
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		try
		{
			var json = e.TryGetWebMessageAsString();
			if (json == null)
				return;

			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			var type = root.GetProperty("type").GetString();

			ThreadHelper.ThrowIfNotOnUIThread();

			switch (type)
			{
				case "input":
					var inputData = root.GetProperty("data").GetString();
					if (inputData != null)
					{
						if (_sessionService?.IsRunning == true)
						{
							_sessionService.WriteInput(inputData);
						}
						else
						{
							// Process exited — restart on Enter
							if (inputData == "\r" || inputData == "\n")
								_sessionService?.RestartSession();
						}
					}
					break;

				case "resize":
					var cols = (short)root.GetProperty("cols").GetInt32();
					var rows = (short)root.GetProperty("rows").GetInt32();
					if (!_sessionStartedByResize && _sessionService != null && !_sessionService.IsRunning)
					{
						// First resize from xterm.js — start process with correct dimensions
						_sessionStartedByResize = true;
						var workspaceFolder = GetWorkspaceFolder();
						if (workspaceFolder != null)
							_sessionService.StartSession(workspaceFolder, cols, rows);
					}
					else
					{
						_sessionService?.Resize(cols, rows);
					}
					break;
			}
		}
		catch (Exception ex)
		{
			_logger?.Log($"Terminal control: message error: {ex.Message}");
		}
	}

	private static string? GetWorkspaceFolder()
	{
		try
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
			{
				var dir = Path.GetDirectoryName(dte.Solution.FullName);
				if (!string.IsNullOrEmpty(dir))
					return dir;
			}
		}
		catch { /* Ignore */ }

		return Directory.GetCurrentDirectory();
	}

	public void Dispose()
	{
		DetachFromSession();
		_webView.CoreWebView2?.WebMessageReceived -= OnWebMessageReceived;
		_webView.Dispose();
	}
}
