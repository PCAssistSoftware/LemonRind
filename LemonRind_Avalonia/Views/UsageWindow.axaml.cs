using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Views;

/// <summary>
/// The usage screen: tokens and speed over time, per model and per tool, from the numbers saved with every reply. The
/// report itself is worked out by UsageReports (plain functions, no screen); this window only chooses the period, asks
/// for the report and draws it. Not modal - it can sit open next to the chat.
/// </summary>
public partial class UsageWindow : Window
{
    private static readonly (string Label, int? Days)[] Periods = [("7 days", 7), ("30 days", 30), ("12 months", 365), ("All time", null)];

    private readonly ChatSessionRepository? _repository;
    private List<UsageRequest> _requests = [];
    private List<ToolUse> _toolUses = [];
    private bool _building; // true while the controls are being filled in, so that does not look like the person changing them

    /// <summary>Called with a chat's id when "Busiest chats" is clicked; the main window opens it.</summary>
    public Action<string>? OpenChat { get; set; }

    public UsageWindow()
    {
        InitializeComponent();
    }

    public UsageWindow(ChatSessionRepository repository) : this()
    {
        _repository = repository;
        Opened += (_, _) => LoadAndShow();
    }

    private void LoadAndShow()
    {
        _building = true;
        PeriodBox.ItemsSource = Periods.Select(p => p.Label).ToList();
        PeriodBox.SelectedIndex = 1; // 30 days
        _building = false;

        _requests = _repository!.UsageRequests();
        _toolUses = _repository.UsageToolUses();
        Rebuild();
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_building && _repository is not null) Rebuild();
    }

    private void Rebuild()
    {
        var days = Periods[Math.Max(0, PeriodBox.SelectedIndex)].Days;
        var tz = TimeZoneInfo.Local;
        var now = DateTime.UtcNow;
        DateTime? since = null;
        if (days is { } d)
        {
            // midnight, d calendar days back counting today as one
            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, tz)).AddDays(-(d - 1));
            since = TimeZoneInfo.ConvertTimeToUtc(firstDay.ToDateTime(TimeOnly.MinValue), tz);
        }

        var includeScheduled = ScheduledBox.IsChecked == true;
        var chosen = ModelBox.SelectedItem as string;
        var model = string.IsNullOrEmpty(chosen) || chosen == UsageReports.AllModels ? null : chosen;

        var report = UsageReports.Build(_requests, _toolUses, since, now, tz, model, includeScheduled);
        if (model is not null && !report.AvailableModels.Contains(model))
        {
            model = null; // the chosen model has no replies in this period: go back to all
            report = UsageReports.Build(_requests, _toolUses, since, now, tz, null, includeScheduled);
        }

        _building = true;
        ModelBox.ItemsSource = new[] { UsageReports.AllModels }.Concat(report.AvailableModels).ToList();
        ModelBox.SelectedItem = model ?? UsageReports.AllModels;
        _building = false;

        Draw(report);
    }

    private void Draw(UsageReport report)
    {
        var empty = report.Totals.Requests == 0;
        EmptyText.IsVisible = empty;
        ReportPanel.IsVisible = !empty;
        if (empty) return;

        var totals = report.Totals;
        TilesPanel.Children.Clear();
        AddTile("Replies", totals.Requests.ToString("N0"));
        AddTile("Tokens in", UsageReports.Compact(totals.InputTokens));
        AddTile("Tokens out", UsageReports.Compact(totals.OutputTokens));
        AddTile("Average speed", totals.TokensPerSecond > 0 ? $"{totals.TokensPerSecond:0} tok/s" : "-");
        AddTile("Average first token", totals.TimeToFirstToken > 0 ? $"{totals.TimeToFirstToken:0.0} s" : "-");

        TokensTitle.Text = $"Tokens per {report.BucketKind}";
        TokenChart.Buckets = report.Buckets;
        SpeedChart.Buckets = report.Buckets;

        FillTable(ModelsHost,
            ["Model", "Replies", "Tokens in", "Tokens out", "Avg tok/s", "Avg first token", "Slowest start"],
            report.Models.Select(r => new[]
            {
                r.Model,
                r.Totals.Requests.ToString("N0"),
                r.Totals.InputTokens.ToString("N0"),
                r.Totals.OutputTokens.ToString("N0"),
                r.Totals.TokensPerSecond > 0 ? r.Totals.TokensPerSecond.ToString("0") : "-",
                r.Totals.TimeToFirstToken > 0 ? $"{r.Totals.TimeToFirstToken:0.0} s" : "-",
                r.SlowestStart > 0 ? $"{r.SlowestStart:0.0} s" : "-",
            }).ToList());

        if (report.Tools.Count == 0)
        {
            ToolsHost.Children.Clear();
            ToolsHost.Children.Add(Note("No tool calls in this period."));
        }
        else
        {
            FillTable(ToolsHost, ["Tool", "Calls", "Failed", "Failure rate"],
                report.Tools.Select(t => new[] { t.Name, t.Calls.ToString("N0"), t.Failed.ToString("N0"), t.FailureRate.ToString("0%") }).ToList());
        }

        ChatsTitle.IsVisible = ChatsHost.IsVisible = report.Chats.Count > 0;
        ChatsHost.Children.Clear();
        foreach (var chat in report.Chats)
        {
            var id = chat.SessionId;
            var button = new Button
            {
                Content = $"{chat.Title}  ({UsageReports.Compact(chat.Tokens)} tokens)",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) =>
            {
                Close();
                OpenChat?.Invoke(id);
            };
            ChatsHost.Children.Add(button);
        }

        NotesHost.Children.Clear();
        NotesHost.Children.Add(Note("Tokens in is what the model had to process for each request; the part of the conversation it had already stored is not counted again. A reply that uses tools takes several requests, and the numbers for one reply add them all up."));
        NotesHost.Children.Add(Note("Speed is averaged by how long each answer was, so a long answer counts for more than a short one. Speed and first-token time come from Lemonade after each request, so two things running at the same moment can be mixed up."));
        if (report.AvailableModels.Contains(UsageReports.UnknownModel))
        {
            NotesHost.Children.Add(Note($"The model's name and the numbers have been saved since this screen was added; earlier replies are listed as '{UsageReports.UnknownModel}' where they have numbers at all. Their tool calls are still counted in the tools table."));
        }
    }

    private void AddTile(string caption, string value)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 36, 8) };
        tile.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#1976D2")) });
        tile.Children.Add(Note(caption));
        TilesPanel.Children.Add(tile);
    }

    private TextBlock Note(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        if (this.TryFindResource("TextMuted", ActualThemeVariant, out var found) && found is IBrush brush) block.Foreground = brush;
        return block;
    }

    /// <summary>A plain table: a bold header row, then one row per line. The first column is wider and left-aligned; the rest are numbers.</summary>
    private void FillTable(StackPanel host, string[] headers, List<string[]> rows)
    {
        host.Children.Clear();
        host.Children.Add(TableRow(headers, header: true));
        foreach (var row in rows) host.Children.Add(TableRow(row, header: false));
    }

    private Grid TableRow(string[] cells, bool header)
    {
        var grid = new Grid { Margin = new Thickness(0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Star));
        for (var i = 1; i < cells.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                FontSize = 12,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }
}
