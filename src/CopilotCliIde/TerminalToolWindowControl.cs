using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Terminal.Wpf;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

// WPF control hosting a native Microsoft.Terminal.Wpf.TerminalControl for terminal rendering.
// Implements ITerminalConnection as the bridge between the native control and TerminalSessionService.
internal sealed class TerminalToolWindowControl : UserControl, ITerminalConnection, IDisposable
{
	private TerminalControl? _termControl;
	private TerminalSessionService? _sessionService;
	private bool _sessionStartedByResize;
	private readonly OutputLogger? _logger;
	private bool _disposed;

	public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

	public TerminalToolWindowControl()
	{
		_logger = VsServices.Instance.Logger;

		_termControl = new TerminalControl { Focusable = true, Connection = this, AutoResize = true };
		Content = _termControl;

		// Attach to session early — Resize may fire before OnLoaded
		AttachToSession();

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		GotFocus += OnGotFocus;
		IsVisibleChanged += OnVisibleChanged;

		VSColorTheme.ThemeChanged += OnThemeChanged;
	}

	void ITerminalConnection.Start()
	{
	}

	void ITerminalConnection.WriteInput(string data)
	{
		SendInput(data);
	}

	// Called by TerminalToolWindow.PreProcessMessage for keys VS would intercept (Escape).
	internal void SendInput(string data)
	{
		if (_sessionService?.IsRunning == true)
		{
			_sessionService.WriteInput(data);
		}
		else if (data is "\r" or "\n")
		{
			_sessionService?.RestartSession();
		}
	}

	void ITerminalConnection.Resize(uint rows, uint columns)
	{
		if (rows == 0 || columns == 0)
			return;

		var cols = (short)columns;
		var r = (short)rows;

		if (!_sessionStartedByResize && _sessionService is { IsRunning: false })
		{
			_sessionStartedByResize = true;
#pragma warning disable VSTHRD001 // Switch to UI thread via JTF for DTE access in callback from native control
			_ = Dispatcher.BeginInvoke(new Action(() =>
			{
				try
				{
					ThreadHelper.ThrowIfNotOnUIThread();
					var workspaceFolder = CopilotCliIdePackage.GetWorkspaceFolder();
					if (workspaceFolder != null)
						_sessionService?.StartSession(workspaceFolder, cols, r);
				}
				catch (Exception ex)
				{
					_logger?.Log($"Terminal: failed to start session: {ex.Message}");
				}
			}));
#pragma warning restore VSTHRD001
		}
		else
		{
			_sessionService?.Resize(cols, r);
		}
	}

	void ITerminalConnection.Close()
	{
		// Session lifecycle is managed by TerminalSessionService — nothing to do here.
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (_disposed)
			return;

		ThreadHelper.ThrowIfNotOnUIThread();
		SetTheme();
	}

	private static void OnUnloaded(object sender, RoutedEventArgs e)
	{
		// Don't detach — session service is a singleton, process survives hide/show.
	}

	private void OnGotFocus(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		_termControl?.Focus();
	}

	private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is true)
			_termControl?.Focus();
	}

	private void OnThemeChanged(ThemeChangedEventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		SetTheme();
	}

	private void SetTheme()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		_termControl?.SetTheme(TerminalThemer.GetTheme(), TerminalSettings.FontFamily, TerminalSettings.FontSize);
	}

	private void AttachToSession()
	{
		_sessionService = VsServices.Instance.TerminalSession;
		if (_sessionService == null)
			return;

		_sessionService.OutputReceived += OnOutputReceived;
		_sessionService.ProcessExited += OnProcessExited;
		_sessionService.SessionRestarted += OnSessionRestarted;
		_sessionStartedByResize = false;
	}

	private void DetachFromSession()
	{
		if (_sessionService == null)
			return;

		_sessionService.OutputReceived -= OnOutputReceived;
		_sessionService.ProcessExited -= OnProcessExited;
		_sessionService.SessionRestarted -= OnSessionRestarted;
		_sessionService = null;
	}

	private void OnOutputReceived(string data)
	{
		if (_disposed)
			return;

		TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
	}

	private void OnProcessExited()
	{
		const string exitMessage = "\r\n\x1b[90m[Process exited. Press Enter to restart.]\x1b[0m\r\n";
		OnOutputReceived(exitMessage);
	}

	private void OnSessionRestarted()
	{
		// The TerminalControl doesn't re-fire Resize when only the underlying
		// process changes (WPF size hasn't changed). Re-sync ConPTY dimensions
		// from the control's already-computed character grid.
		if (_termControl is { Rows: > 0, Columns: > 0 })
		{
			_sessionService?.Resize((short)_termControl.Columns, (short)_termControl.Rows);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;

		VSColorTheme.ThemeChanged -= OnThemeChanged;
		DetachFromSession();
		_termControl = null;
	}
}
