using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
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

		// Two-row layout: a thin right-aligned toolbar above the terminal control.
		// VS's ToolWindow toolbars are left-aligned only — we host our own here so
		// the buttons sit at the top right where the user expects them.
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

		var toolbar = BuildToolbar();
		Grid.SetRow(toolbar, 0);
		grid.Children.Add(toolbar);

		Grid.SetRow(_termControl, 1);
		grid.Children.Add(_termControl);

		Content = grid;

		AttachToSession();

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		GotFocus += OnGotFocus;
		IsVisibleChanged += OnVisibleChanged;

		VSColorTheme.ThemeChanged += OnThemeChanged;
	}

	private FrameworkElement BuildToolbar()
	{
		// Themed bar background — matches the rest of the tool window chrome.
		var bar = new Border
		{
			Background = new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey).ProvideValue(null) as Brush,
			BorderBrush = new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey).ProvideValue(null) as Brush,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(2, 1, 2, 1),
		};

		var stack = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		stack.Children.Add(MakeToolbarButton(KnownMonikers.History, "View Session History",
			"Resume a previous Copilot CLI session for this workspace", OnViewSessionHistoryClick));
		stack.Children.Add(MakeToolbarButton(KnownMonikers.NewItem, "New Session",
			"Start a fresh Copilot CLI session", OnNewSessionClick));
		stack.Children.Add(MakeToolbarButton(KnownMonikers.DeleteListItem, "Delete Current Thread",
			"Permanently delete the current Copilot CLI chat thread", OnDeleteCurrentSessionClick));

		bar.Child = stack;
		return bar;
	}

	private static Button MakeToolbarButton(ImageMoniker moniker, string accessibleName, string tooltip, RoutedEventHandler onClick)
	{
		var img = new CrispImage
		{
			Moniker = moniker,
			Width = 16,
			Height = 16,
			Margin = new Thickness(2),
		};
		var btn = new Button
		{
			Content = img,
			ToolTip = tooltip,
			Padding = new Thickness(4, 2, 4, 2),
			Margin = new Thickness(1, 0, 1, 0),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Focusable = false,
			Cursor = Cursors.Hand,
		};
		System.Windows.Automation.AutomationProperties.SetName(btn, accessibleName);
		btn.Click += onClick;
		return btn;
	}

	private void OnViewSessionHistoryClick(object sender, RoutedEventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		VsServices.Instance.OnViewSessionHistory?.Invoke();
	}

	private void OnNewSessionClick(object sender, RoutedEventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		VsServices.Instance.OnNewSession?.Invoke();
	}

	private void OnDeleteCurrentSessionClick(object sender, RoutedEventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		VsServices.Instance.OnDeleteCurrentSession?.Invoke();
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
			_sessionService?.RestartPreservingMode();
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
			// Resize can be called from a non-UI thread by the native TerminalContainer.
			// This is safe because TerminalProcess.Resize() uses a lock internally.
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

	internal void FocusTerminal() => _termControl?.Focus();

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
