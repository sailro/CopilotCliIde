using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

// VS-themed picker for resuming a previous Copilot CLI session.
// Filters to sessions whose cwd is at or below the supplied workspace path.
internal sealed class SessionPickerDialog : DialogWindow
{
	private readonly SessionStore _store;
	private readonly string _workspacePath;
	private readonly CancellationTokenSource _cts = new();
	private readonly TextBlock _statusText;
	private readonly ListView _list;
	private readonly Button _resumeButton;

	public string? SelectedSessionId { get; private set; }

	public SessionPickerDialog(SessionStore store, string workspacePath)
	{
		_store = store;
		_workspacePath = workspacePath;

		Title = "Resume Copilot CLI Session";
		Width = 720;
		Height = 480;
		MinWidth = 520;
		MinHeight = 320;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;

		var root = new DockPanel { Margin = new Thickness(12) };

		var header = new TextBlock
		{
			Text = $"Sessions for: {workspacePath}",
			Margin = new Thickness(0, 0, 0, 8),
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);

		_statusText = new TextBlock
		{
			Text = "Loading sessions...",
			Margin = new Thickness(0, 0, 0, 8),
			Visibility = Visibility.Visible,
		};
		DockPanel.SetDock(_statusText, Dock.Top);
		root.Children.Add(_statusText);

		var buttonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 8, 0, 0),
		};
		_resumeButton = new Button
		{
			Content = "_Resume",
			IsDefault = true,
			IsEnabled = false,
			MinWidth = 80,
			Margin = new Thickness(0, 0, 8, 0),
			Padding = new Thickness(12, 4, 12, 4),
		};
		_resumeButton.Click += OnResumeClick;
		var cancelButton = new Button
		{
			Content = "_Cancel",
			IsCancel = true,
			MinWidth = 80,
			Padding = new Thickness(12, 4, 12, 4),
		};
		buttonPanel.Children.Add(_resumeButton);
		buttonPanel.Children.Add(cancelButton);
		DockPanel.SetDock(buttonPanel, Dock.Bottom);
		root.Children.Add(buttonPanel);

		_list = new ListView { SelectionMode = SelectionMode.Single };
		var grid = new GridView();
		grid.Columns.Add(new GridViewColumn { Header = "Summary", Width = 360, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.SummaryDisplay)) });
		grid.Columns.Add(new GridViewColumn { Header = "Last Updated", Width = 140, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.UpdatedDisplay)) });
		grid.Columns.Add(new GridViewColumn { Header = "Turns", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.TurnCount)) });
		_list.View = grid;
		_list.SelectionChanged += (_, _) => _resumeButton.IsEnabled = _list.SelectedItem is Row;
		_list.MouseDoubleClick += OnListDoubleClick;
		_list.KeyDown += OnListKeyDown;
		root.Children.Add(_list);

		Content = root;

		Loaded += OnLoaded;
		Closed += (_, _) => _cts.Cancel();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		// Fire-and-forget load — exceptions are caught inside LoadAsync.
#pragma warning disable VSSDK007 // Dialog lifetime is bounded by the modal Show; not worth tracking the JoinableTask.
		_ = ThreadHelper.JoinableTaskFactory.RunAsync(LoadAsync);
#pragma warning restore VSSDK007
	}

	private async System.Threading.Tasks.Task LoadAsync()
	{
		var ct = _cts.Token;
		SessionQueryResult result;
		try
		{
			result = await _store.GetSessionsForWorkspaceAsync(_workspacePath, ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			result = SessionQueryResult.Empty(SessionStoreStatus.Unavailable, ex.Message);
		}

		if (ct.IsCancellationRequested)
			return;

		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
		ApplyResult(result);
	}

	private void ApplyResult(SessionQueryResult result)
	{
		switch (result.Status)
		{
			case SessionStoreStatus.NoDatabase:
				_statusText.Text = "No Copilot CLI session history found. Sessions are saved automatically once you start using the CLI.";
				return;
			case SessionStoreStatus.Unavailable:
				_statusText.Text = "Session history is unavailable (could not read session-store.db). See the Copilot CLI IDE output pane for details.";
				return;
		}

		if (result.Sessions.Count == 0)
		{
			_statusText.Text = "No previous Copilot CLI sessions for this workspace.";
			return;
		}

		_statusText.Visibility = Visibility.Collapsed;
		var rows = new Row[result.Sessions.Count];
		for (var i = 0; i < result.Sessions.Count; i++)
			rows[i] = Row.From(result.Sessions[i]);
		_list.ItemsSource = rows;
		_list.SelectedIndex = 0;
		_list.Focus();
	}

	private void OnResumeClick(object sender, RoutedEventArgs e) => Accept();

	private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (_list.SelectedItem is Row)
			Accept();
	}

	private void OnListKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && _list.SelectedItem is Row)
		{
			Accept();
			e.Handled = true;
		}
	}

	private void Accept()
	{
		if (_list.SelectedItem is Row row)
		{
			SelectedSessionId = row.Id;
			DialogResult = true;
			Close();
		}
	}

	private sealed class Row
	{
		public Row(string id, string summaryDisplay, string updatedDisplay, int turnCount)
		{
			Id = id;
			SummaryDisplay = summaryDisplay;
			UpdatedDisplay = updatedDisplay;
			TurnCount = turnCount;
		}

		public string Id { get; }
		public string SummaryDisplay { get; }
		public string UpdatedDisplay { get; }
		public int TurnCount { get; }

		public static Row From(SessionInfo s) => new(
			s.Id,
			string.IsNullOrWhiteSpace(s.Summary) ? "(no summary)" : s.Summary!.Trim(),
			FormatRelative(s.UpdatedAtUtc),
			s.TurnCount);

		private static string FormatRelative(DateTime utc)
		{
			if (utc == DateTime.MinValue)
				return "—";
			var delta = DateTime.UtcNow - utc;
			if (delta.TotalSeconds < 0)
				return utc.ToLocalTime().ToString("g");
			if (delta.TotalMinutes < 1) return "just now";
			if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
			if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
			if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
			return utc.ToLocalTime().ToString("yyyy-MM-dd");
		}
	}
}
