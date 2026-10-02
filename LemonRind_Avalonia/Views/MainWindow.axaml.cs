using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonRindAvalonia.ViewModels;

namespace LemonRindAvalonia.Views;

public partial class MainWindow : Window
{
    // Whether the message list should keep following new content as it
    // arrives (a new message, or a reply streaming in). Starts true so a
    // freshly-opened/just-loaded session lands scrolled to its newest
    // message; flips to false the moment the user scrolls away to reread
    // something, so auto-scroll doesn't fight a deliberate scroll-up, and
    // flips back to true on its own once they scroll back near the bottom.
    private bool _autoScrollToBottom = true;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    // Context-usage breakdown popup - a hoverable Border, not a click
    // target, since this is a glanceable hover detail rather than
    // something to navigate into. RefreshContextBreakdownAsync's own
    // IsContextBreakdownLoading guard covers the mouse re-entering before a
    // previous refresh finished.
    private async void OnContextUsageAnchorPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.IsContextBreakdownOpen = true;
        await viewModel.RefreshContextBreakdownAsync();
    }

    private void OnContextUsageAnchorPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.IsContextBreakdownOpen = false;
    }

    // Avalonia's ScrollViewer doesn't follow growing content on its own the
    // way a terminal or chat app usually does - this reacts to the SAME
    // ScrollChanged event for both "a new message was added" and "the
    // current reply is still streaming in" (both grow the scrollable
    // extent), rather than needing two separate hooks.
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;

        if (e.ExtentDelta.Y > 0 && _autoScrollToBottom)
        {
            scrollViewer.ScrollToEnd();
        }

        var distanceFromBottom = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        _autoScrollToBottom = distanceFromBottom < 40;
    }

    // Avalonia's equivalent of WPF's Window.Loaded - fires once the window
    // is actually shown, which is when InitializeAsync's real Lemonade
    // calls (list models, check health, resume last chat) should run, not
    // the constructor.
    // Pane limits (the same ranges as the Blazor port's drag handles) and persistence.
    private const double SidebarMin = 200, SidebarMax = 560, RightPanelMin = 220, RightPanelMax = 520;

    private void ApplyPaneWidths()
    {
        var columns = RootGrid.ColumnDefinitions;
        columns[0].MinWidth = SidebarMin;
        columns[0].MaxWidth = SidebarMax;
        columns[4].MinWidth = RightPanelMin;
        columns[4].MaxWidth = RightPanelMax;
        if (DataContext is MainViewModel viewModel)
        {
            var (sidebar, rightPanel) = viewModel.SavedPaneWidths;
            if (sidebar > 0) columns[0].Width = new GridLength(Math.Clamp(sidebar, SidebarMin, SidebarMax));
            if (rightPanel > 0) columns[4].Width = new GridLength(Math.Clamp(rightPanel, RightPanelMin, RightPanelMax));
        }
    }

    private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.SavePaneWidths(RootGrid.ColumnDefinitions[0].ActualWidth, RootGrid.ColumnDefinitions[4].ActualWidth);
    }

    // Test hooks for screenshots (env vars, harmless in normal use):
    //   LEMONRIND_MODEL=id     selects (and waits for the load of) that model first;
    //   LEMONRIND_SEND="text"  starts a new chat, sends that message shortly after startup and waits for the reply to finish;
    //   LEMONRIND_OPEN=settings|logs|system|tools|model|context|filewrite  then opens that window.
    private async Task RunTestHooksAsync(MainViewModel viewModel)
    {
        if (Environment.GetEnvironmentVariable("LEMONRIND_MODEL") is { Length: > 0 } model)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            viewModel.SelectedModel = model;
            await Task.Delay(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 240 && viewModel.IsModelLoading; i++) await Task.Delay(500);
        }

        if (Environment.GetEnvironmentVariable("LEMONRIND_SEND") is { Length: > 0 } text)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            viewModel.NewChatCommand.Execute(null);
            viewModel.InputText = text;
            viewModel.SendCommand.Execute(null);
            await Task.Delay(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 180 && viewModel.IsBusy; i++) await Task.Delay(500);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        switch (Environment.GetEnvironmentVariable("LEMONRIND_OPEN"))
        {
            case "settings": viewModel.OpenSettingsCommand.Execute(null); break;
            case "logs": viewModel.OpenLogViewerCommand.Execute(null); break;
            case "system": viewModel.OpenSystemPromptCommand.Execute(null); break;
            case "tools": viewModel.OpenToolsSentCommand.Execute(null); break;
            case "model": viewModel.OpenModelDetailsCommand.Execute(null); break;
            case "context": viewModel.OpenTurnContextCommand.Execute(null); break;
            case "filewrite":
                new FileWriteApprovalDialog("C:/Users/demo/Lemon Rind/data/Workspace/meeting-notes.txt".Replace('/', Path.DirectorySeparatorChar),
                    "Meeting notes - 2 October" + Environment.NewLine + "- Q3 budget approved" + Environment.NewLine + "- Hiring freeze lifted for two roles" + Environment.NewLine + "- Launch moved to 14 November").Show();
                break;
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        ApplyPaneWidths();

        if (DataContext is MainViewModel hookViewModel) _ = RunTestHooksAsync(hookViewModel);
        // Wired up here, not via a plain KeyDown="..." XAML attribute on
        // the TextBox, and explicitly at the Tunnel routing strategy - see
        // OnInputTextBoxKeyDown's own comment for why a Bubble-phase
        // handler arrives too late to intercept TextBox's own AcceptsReturn
        // newline insertion: a Bubble-attached handler never runs for Enter
        // at all, since AcceptsReturn's own internal handling already
        // consumed (and marked Handled on) the event before an ordinary
        // instance handler on the same element sees it.
        InputTextBox.AddHandler(InputElement.KeyDownEvent, OnInputTextBoxKeyDown, RoutingStrategies.Tunnel);

        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync(CancellationToken.None);
        }
    }

    private void OnSessionTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChatSessionSummary session } textBox) return;

        if (e.Key == Key.Enter)
        {
            session.IsEditingTitle = false;
            if (DataContext is MainViewModel viewModel) viewModel.CommitRename(session);
        }
        else if (e.Key == Key.Escape)
        {
            session.IsEditingTitle = false;
        }
    }

    // Avalonia's TextBox has no built-in "Enter submits" behaviour the way
    // some frameworks' single-line input controls do - AcceptsReturn="False"
    // only stops it inserting a newline, it doesn't wire Enter to anything.
    // Enter sends, Shift+Enter inserts a newline (deliberately not handled
    // here, letting AcceptsReturn's own default behaviour through).
    //
    // Must be wired at the Tunnel routing strategy (see OnWindowOpened),
    // not via a plain Bubble-phase KeyDown="..." attribute: once
    // AcceptsReturn="True" is set for the multi-line input box, Enter stops
    // sending messages entirely, because TextBox's own internal
    // AcceptsReturn handling (inserting the newline) runs during the same
    // Bubble phase and marks the event Handled before an ordinary instance
    // handler on the same element ever sees it. Attaching at Tunnel instead
    // means this runs FIRST, on the way down - if it sends and marks the
    // event Handled, TextBox's own newline-insertion (which only runs
    // later, during Bubble) never fires at all.
    private void OnInputTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers != KeyModifiers.Shift && DataContext is MainViewModel viewModel && viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Opens Avalonia's own StorageProvider file picker (the
    // Avalonia-idiomatic replacement for WPF's Microsoft.Win32.
    // OpenFileDialog, which doesn't exist here) and hands the picked path
    // straight to MainViewModel.AttachFile, which does the actual text
    // extraction. File pickers are inherently a View-side concern in
    // Avalonia - there's no ViewModel-friendly equivalent - so this stays
    // a plain code-behind handler rather than a [RelayCommand].
    private async void OnAttachFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || DataContext is not MainViewModel viewModel) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach a file to analyze in this chat",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported files") { Patterns = ["*.pdf", "*.docx", "*.xlsx", "*.txt", "*.md", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
        {
            viewModel.AttachFile(localPath);
        }
    }

    // Same "picker is View-side" reasoning as OnAttachFileClick:
    // opens Avalonia's own StorageProvider save-file dialog, then hands the
    // picked destination straight to MainViewModel.ExportSessionToMarkdown,
    // which does the actual read/build/write work. Triggered from the row's
    // right-click context menu (a MenuItem, not a Button - see the sidebar
    // row's own comment in MainWindow.axaml) - its
    // DataContext is still the row's own ChatSessionSummary, since Avalonia
    // resolves $parent/DataContext bindings inside a ContextMenu back
    // through its placement target's own visual tree.
    private async void OnExportToMarkdownClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || sender is not MenuItem { DataContext: ChatSessionSummary session } || DataContext is not MainViewModel viewModel) return;

        var sanitizedTitle = string.Concat(session.Title.Split(Path.GetInvalidFileNameChars()));

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chat to Markdown",
            SuggestedFileName = $"{sanitizedTitle}.md",
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown files") { Patterns = ["*.md"] }, FilePickerFileTypes.All],
        });

        if (file?.TryGetLocalPath() is { } localPath)
        {
            viewModel.ExportSessionToMarkdown(session, localPath);
        }
    }

    // Commits a sidebar row's rename once the user clicks/tabs away - the
    // TextBox's own two-way Text binding has already pushed the edited
    // value into the ChatSessionSummary by the time this fires, so this
    // just needs to persist whatever's currently there.
    private void OnSessionTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ChatSessionSummary session } && DataContext is MainViewModel viewModel)
        {
            session.IsEditingTitle = false;
            viewModel.CommitRename(session);
        }
    }

    // Save/copy-image follow-up - same "picker is View-side" reasoning as
    // OnAttachFileClick/OnExportToMarkdownClick. Copies the real on-disk
    // generated-image file straight to the picked destination - no
    // re-encoding needed, it's already a real PNG.
    private async void OnSaveImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || sender is not Button { DataContext: ChatMessageViewModel { GeneratedImagePath: { } imagePath } }) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image",
            SuggestedFileName = Path.GetFileName(imagePath),
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }, FilePickerFileTypes.All],
        });

        if (file?.TryGetLocalPath() is { } localPath)
        {
            File.Copy(imagePath, localPath, overwrite: true);
        }
    }

    // Avalonia 12's clipboard API (IClipboard.SetDataAsync(IAsyncDataTransfer))
    // has real, first-class cross-platform image support via DataFormat.Bitmap
    // - confirmed via reflection against the installed Avalonia.Base.dll
    // before writing this, not assumed. DataTransfer itself implements
    // IAsyncDataTransfer directly, so no separate sync-to-async wrapper is
    // needed.
    private async void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null || sender is not Button { DataContext: ChatMessageViewModel { GeneratedImagePath: { } imagePath } }) return;

        var bitmap = new Avalonia.Media.Imaging.Bitmap(imagePath);
        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
        await topLevel.Clipboard.SetDataAsync(dataTransfer);
    }

    // Copy-regression fix - right-click a bubble to copy its raw Text,
    // regardless of whether the plain streaming TextBox or the Markdown-
    // rendered content is currently showing (see the ContextMenu's own
    // comment in the XAML for why this was needed at all). SetTextAsync is
    // a ClipboardExtensions convenience method (Avalonia.Input.Platform) -
    // confirmed via reflection this exists alongside the lower-level
    // SetDataAsync used for images above.
    private async void OnCopyMessageTextClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null || sender is not MenuItem { DataContext: ChatMessageViewModel bubble }) return;

        await topLevel.Clipboard.SetTextAsync(bubble.Text);
    }
}
