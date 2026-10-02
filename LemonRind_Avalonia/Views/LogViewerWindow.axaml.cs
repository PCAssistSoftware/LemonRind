using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonRindAvalonia.Services;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Live-streams Lemonade's own logs via its documented WS /logs/stream API
/// (see LemonadeLogClient). Not modal (Show, not ShowDialog) - meant to sit
/// open alongside the main window while you keep chatting, the way a real
/// log tail would.
///
/// Ported from the VB.NET/WPF LemonRind app's LogViewerWindow.xaml(.vb).
/// The multi-select "Copy as tab-separated text" feature isn't ported -
/// Avalonia's ListBox selection/clipboard APIs differ enough from WPF's
/// ListView that replicating it exactly wasn't worth it for this
/// checkpoint; worth adding back later if it's actually missed.
/// </summary>
public partial class LogViewerWindow : Window
{
    /// <summary>Capped, not unbounded - a long-running session could otherwise grow this without limit.</summary>
    private const int MaxDisplayedEntries = 3000;

    private readonly LemonadeLogClient _logClient;
    // _all is the full capped list; _entries is what the ListBox shows (_all through the filters, frozen while paused).
    private readonly List<LemonadeLogEntry> _all = [];
    private ObservableCollection<LemonadeLogEntry> _entries = [];
    private bool _paused;
    private int _pausedAt;
    private CancellationTokenSource? _streamCts;

    // Same "only auto-follow if the user hasn't deliberately scrolled
    // away" pattern as MainWindow's own message auto-scroll.
    private bool _autoScrollToBottom = true;
    private ScrollViewer? _logScrollViewer;

    public LogViewerWindow()
    {
        InitializeComponent();
    }

    public LogViewerWindow(LemonadeLogClient logClient) : this()
    {
        _logClient = logClient;
        LogListBox.ItemsSource = _entries;

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        _logScrollViewer = LogListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ScrollChanged += OnLogScrollChanged;
        }

        _streamCts = new CancellationTokenSource();
        try
        {
            await _logClient.StreamAsync(
                onSnapshot: snapshotEntries => Dispatcher.UIThread.Post(() => AddEntries(snapshotEntries)),
                onEntry: entry => Dispatcher.UIThread.Post(() => AddEntries([entry])),
                cancellationToken: _streamCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected - the window was closed, see OnWindowClosed.
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Disconnected: {ex.Message}";
            return;
        }

        // StreamAsync only returns without throwing if the server itself
        // closed the connection - not expected in normal use, worth saying
        // so rather than leaving "Connecting..." showing forever.
        if (_streamCts?.IsCancellationRequested != true)
        {
            StatusText.Text = "Disconnected - Lemonade closed the connection.";
        }
    }

    private static int SeverityRank(string severity) => severity switch { "Warn" => 1, "Error" or "Fatal" => 2, _ => 0 };

    private bool Matches(LemonadeLogEntry entry)
    {
        if (SeverityRank(entry.Severity) < SeverityFilter.SelectedIndex) return false;
        var text = TextFilter.Text;
        return string.IsNullOrWhiteSpace(text)
            || entry.Line.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Tag.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatus()
    {
        StatusText.Text = _paused
            ? $"Paused - {_all.Count - _pausedAt} new line(s) waiting ({_entries.Count} shown)"
            : $"Connected - streaming live ({_entries.Count} shown of {_all.Count})";
    }

    private void AddEntries(IEnumerable<LemonadeLogEntry> newEntries)
    {
        foreach (var entry in newEntries)
        {
            _all.Add(entry);
            if (!_paused && Matches(entry)) _entries.Add(entry);
        }
        if (_all.Count > MaxDisplayedEntries) _all.RemoveRange(0, _all.Count - MaxDisplayedEntries);
        while (_entries.Count > MaxDisplayedEntries) _entries.RemoveAt(0);

        UpdateStatus();

        if (!_paused && _autoScrollToBottom && _entries.Count > 0)
        {
            LogListBox.ScrollIntoView(_entries[^1]);
        }
    }

    /// <summary>Rebuilds the shown list from the full list (after a filter change or Resume) - one new collection, not 3000 adds.</summary>
    private void RebuildView()
    {
        var source = _paused ? _all.Take(_pausedAt) : _all;
        _entries = new ObservableCollection<LemonadeLogEntry>(source.Where(Matches));
        LogListBox.ItemsSource = _entries;
        UpdateStatus();
        _autoScrollToBottom = true;
        if (!_paused && _entries.Count > 0) LogListBox.ScrollIntoView(_entries[^1]);
    }

    private void OnSeverityChanged(object? sender, SelectionChangedEventArgs e) => OnFilterChanged();
    private void OnTextFilterChanged(object? sender, TextChangedEventArgs e) => OnFilterChanged();

    private void OnFilterChanged()
    {
        // Fires during InitializeComponent, before the rest of the controls exist.
        if (!IsInitialized || SeverityFilter is null || TextFilter is null || _logClient is null) return;
        RebuildView();
    }

    private void OnPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _paused = !_paused;
        _pausedAt = _all.Count;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        RebuildView();
    }

    private void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _all.Clear();
        _pausedAt = 0;
        RebuildView();
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = string.Join('\n', _entries.Select(x => $"{x.Timestamp}\t{x.Severity}\t{x.Tag}\t{x.Line}"));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>Same logic as MainWindow's own OnMessagesScrollChanged - see its comment.</summary>
    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer is null) return;
        const int nearBottomTolerance = 4;
        var distanceFromBottom = _logScrollViewer.Extent.Height - (_logScrollViewer.Offset.Y + _logScrollViewer.Viewport.Height);
        _autoScrollToBottom = distanceFromBottom < nearBottomTolerance;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _streamCts?.Cancel();
}
