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
	private WebView2? _webView;
	private TerminalSessionService? _sessionService;
	private bool _webViewReady;
	private bool _sessionStartedByResize;
	private readonly OutputLogger? _logger;
	private bool _disposed;
	private bool _initializing;

	public TerminalToolWindowControl()
	{
		_logger = VsServices.Instance.Logger;

		// Lightweight placeholder — WebView2 is created lazily in DeferredInitialize
		// to avoid blocking VS during tool window restoration at startup.
		Content = new TextBlock
		{
			Text = "Loading Copilot CLI…",
			Foreground = System.Windows.Media.Brushes.Gray,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 14
		};

		Background = new System.Windows.Media.SolidColorBrush(
			System.Windows.Media.Color.FromRgb(30, 30, 30));

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		PreviewMouseDown += OnPreviewMouseDown;
		IsVisibleChanged += OnVisibleChanged;
	}

	private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		// Clicking the tool window explicitly focuses WebView2 + xterm.js.
		// This handles focus recovery after F5 debug cycles where Chromium's
		// internal focus desyncs from the WPF focus state.
		// PreviewMouseDown only fires on real user clicks — no infinite loops.
		if (_webViewReady && _webView?.CoreWebView2 != null)
		{
			_webView.Focus();
			try { _ = _webView.CoreWebView2.ExecuteScriptAsync("if(window.term)term.focus()"); }
			catch { /* Ignore */ }
		}
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (!_webViewReady && !_initializing && !_disposed)
		{
			// First load — defer WebView2 init to avoid blocking VS during tool window restore.
#pragma warning disable VSTHRD001, VSTHRD110 // BeginInvoke is intentional — need ApplicationIdle priority for safe deferred startup
			Dispatcher.BeginInvoke(new Action(DeferredInitialize), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
#pragma warning restore VSTHRD001, VSTHRD110
		}
		else if (_webViewReady && _sessionService == null && !_disposed)
		{
			// Reloaded after being unloaded (e.g., VS debug layout switch) — re-attach
			AttachToSession();
		}
	}

	private void DeferredInitialize()
	{
		if (_webViewReady || _initializing || _disposed)
			return;
		_initializing = true;

#pragma warning disable VSSDK007 // Fire-and-forget is intentional for UI event handler
		_ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
#pragma warning restore VSSDK007
		{
			try
			{
				await InitializeWebViewAsync();
				if (_disposed)
					return;
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				AttachToSession();
			}
			catch (Exception ex)
			{
				_logger?.Log($"Terminal control: load failed: {ex}");
			}
			finally
			{
				_initializing = false;
			}
		});
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		DetachFromSession();
	}

	private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is true && _webViewReady && _webView != null)
			_webView.Focus();
	}

	private async System.Threading.Tasks.Task InitializeWebViewAsync()
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

		if (_disposed)
			return;

		var cachePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"CopilotCliIde", "webview2");
		Directory.CreateDirectory(cachePath);

		var env = await CoreWebView2Environment.CreateAsync(null, cachePath);

		if (_disposed)
			return;

		// Create WebView2 lazily here (not in constructor) to keep startup lightweight
		_webView = new WebView2
		{
			DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 30)
		};
		Content = _webView;

		await _webView.EnsureCoreWebView2Async(env);

		if (_disposed)
			return;

		// Map the Terminal resources folder to a virtual hostname
		var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
		var terminalDir = Path.Combine(extensionDir, "Resources", "Terminal");
		_logger?.Log($"Terminal control: mapping resources from {terminalDir}");

		_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
			"copilot-cli.local", terminalDir, CoreWebView2HostResourceAccessKind.Allow);

		_webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

		_webView.CoreWebView2.NavigationCompleted += (_, args) =>
		{
			if (args.IsSuccess)
			{
				_logger?.Log("Terminal control: navigation succeeded");
			}
			else
			{
				_logger?.Log($"Terminal control: navigation failed — status {args.WebErrorStatus}");
			}
		};

		_webView.CoreWebView2.DOMContentLoaded += (_, _) =>
		{
			_webViewReady = true;
			_logger?.Log("Terminal control: DOM ready, terminal active");
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
		if (!_webViewReady || _disposed || _webView == null)
			return;

		// Serialize on the calling thread to keep UI work minimal
		var message = JsonSerializer.Serialize(new { type = "output", data });

#pragma warning disable VSTHRD001 // BeginInvoke is intentional — lighter than JTF for fire-and-forget UI dispatch
		_webView.Dispatcher.BeginInvoke(new Action(() =>
#pragma warning restore VSTHRD001
		{
			try
			{
				_webView?.CoreWebView2?.PostWebMessageAsJson(message);
			}
			catch (Exception)
			{
				// WebView may be disposed
			}
		}));
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
		_disposed = true;
		DetachFromSession();
		if (_webView != null)
		{
			_webView.CoreWebView2?.WebMessageReceived -= OnWebMessageReceived;
			_webView.Dispose();
			_webView = null;
		}
	}
}
