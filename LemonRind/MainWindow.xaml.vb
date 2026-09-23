Imports System.Threading
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports LemonRind.Data
Imports LemonRind.ViewModels

''' <summary>
''' Code-behind is deliberately thin - just wiring the DI-supplied ViewModel
''' as DataContext and handling the one thing that's awkward to do purely
''' through binding (auto-scrolling to the newest message).
''' </summary>
Class MainWindow

    Private ReadOnly _viewModel As MainViewModel

    ' True as long as the user hasn't deliberately scrolled up - set to
    ' False the moment a scroll event shows they're no longer at the
    ' bottom, and back True once they scroll back down themselves. Needed
    ' because ScrollChanged fires for BOTH the user manually scrolling and
    ' the content growing underneath them (a streamed reply growing the
    ' ScrollViewer's Extent) - without distinguishing the two, auto-scroll
    ' would yank the view back to the bottom the instant the user tried to
    ' scroll up to reread something mid-stream.
    Private _autoScrollToBottom As Boolean = True

    Public Sub New(viewModel As MainViewModel)
        InitializeComponent()

        _viewModel = viewModel
        DataContext = _viewModel

        ' Messages.CollectionChanged alone (a new bubble added) would catch
        ' a new user/assistant turn starting, but not an EXISTING bubble's
        ' Text growing as a reply streams in, since that's a plain property
        ' change, not a collection Add - long replies would silently scroll
        ' past the bottom of the viewport with no auto-follow. ScrollChanged
        ' instead fires for both cases (a new bubble changes Extent;
        ' streamed text growing the current bubble also changes Extent), so
        ' it covers both.
        AddHandler MessagesScrollViewer.ScrollChanged, AddressOf OnMessagesScrollChanged

        ' Populate the model list and figure out what's currently loaded once
        ' the window actually exists, rather than blocking OnStartup in
        ' Application.xaml.vb on a network call before the window can even
        ' appear.
        AddHandler Loaded, AddressOf OnLoaded

        ' Picks up sessions a scheduled job created while the app
        ' was in the background/unfocused - refresh-on-focus rather than a
        ' blind polling timer, so it only ever happens at a moment the user
        ' is already looking at the window (not disruptive mid-task) and
        ' needs no cross-module event wiring to SchedulerModule.
        AddHandler Activated, AddressOf OnWindowActivated
    End Sub

    Private Async Sub OnLoaded(sender As Object, e As RoutedEventArgs)
        Await _viewModel.InitializeAsync(CancellationToken.None)
    End Sub

    ''' <summary>
    ''' Named OnWindowActivated, not OnActivated - Window already has a
    ''' same-named overridable method, and this is a plain event handler for
    ''' the Activated event, not an override of it. Also refreshes the Modules tab and the
    ''' model dropdown now (see MainViewModel.RefreshModulesDisplay/
    ''' RefreshAvailableModelsAsync's own comments) - a modal Settings
    ''' dialog closing reliably fires this event, the same trigger
    ''' RefreshSessions() already relies on, so no new plumbing needed for
    ''' any of them. Async Sub (like OnLoaded below), since refreshing the
    ''' model list is a real Lemonade call.
    ''' </summary>
    Private Async Sub OnWindowActivated(sender As Object, e As EventArgs)
        _viewModel.RefreshSessions()
        _viewModel.RefreshModulesDisplay()
        Await _viewModel.RefreshAvailableModelsAsync(CancellationToken.None)
    End Sub

    ''' <summary>
    ''' A ComboBox auto-scrolls its dropdown to the currently selected item
    ''' when it opens (Selector's own built-in behavior), which looks like a
    ''' jarring jump once the model list has group headers. Resets to the
    ''' top instead, every time. Runs via
    ''' Dispatcher.BeginInvoke at Background priority, deliberately after
    ''' the Popup's own layout/scroll-into-view has already happened - this
    ''' has to run AFTER that built-in behavior to actually win, not before.
    ''' </summary>
    Private Sub OnModelComboBoxDropDownOpened(sender As Object, e As EventArgs)
        Dim comboBox = CType(sender, ComboBox)
        Dispatcher.BeginInvoke(
            Sub()
                Dim scrollViewer = TryCast(comboBox.Template.FindName("DropDownScrollViewer", comboBox), ScrollViewer)
                scrollViewer?.ScrollToTop()
            End Sub,
            DispatcherPriority.Background)
    End Sub

    Private Sub OnMessagesScrollChanged(sender As Object, e As ScrollChangedEventArgs)
        ' ExtentHeightChange = 0 means this event was caused by the user
        ' actually scrolling (wheel/drag/keyboard), not by content growing -
        ' that's the moment to re-decide whether they're "following along"
        ' (at/near the bottom) or have deliberately scrolled away from it.
        If e.ExtentHeightChange = 0 Then
            Const nearBottomTolerance = 4
            _autoScrollToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - nearBottomTolerance
        End If

        If _autoScrollToBottom AndAlso e.ExtentHeightChange <> 0 Then
            MessagesScrollViewer.ScrollToEnd()
        End If
    End Sub

    ''' <summary>
    ''' Enter sends, Shift+Enter inserts a newline - the standard chat-app
    ''' convention, needed since the input box is AcceptsReturn/multi-line.
    ''' A declarative KeyBinding can't express "only when Shift isn't held,
    ''' otherwise let the TextBox's own newline-insertion through", so this
    ''' handles it directly.
    ''' </summary>
    Private Sub OnInputTextBoxPreviewKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Return AndAlso Keyboard.Modifiers <> ModifierKeys.Shift Then
            e.Handled = True
            If _viewModel.SendCommand.CanExecute(Nothing) Then _viewModel.SendCommand.Execute(Nothing)
        End If
    End Sub

    ''' <summary>
    ''' Commits an in-progress session rename on any click elsewhere in the
    ''' window. Not the sidebar ListBox's bubbling LostFocus event - that
    ''' only fires when focus actually moves to something else, and
    ''' clicking a non-focusable area (the message bubbles are plain
    ''' TextBlocks, Focusable="False" by default) never moves focus at all,
    ''' so LostFocus would never fire there. PreviewMouseDown at the Window
    ''' level tunnels down first and catches every click regardless of
    ''' whether its target can take focus.
    ''' </summary>
    Private Sub OnWindowPreviewMouseDown(sender As Object, e As MouseButtonEventArgs)
        Dim editingSession = _viewModel.Sessions.FirstOrDefault(Function(s) s.IsEditing)
        If editingSession Is Nothing Then Return

        ' Walk up from whatever was actually clicked - if it's inside that
        ' session's own edit TextBox, this is just the user repositioning
        ' the caret or selecting text, not clicking away, so let it proceed
        ' rather than committing mid-edit.
        Dim clickedElement = TryCast(e.OriginalSource, DependencyObject)
        While clickedElement IsNot Nothing
            Dim textBox = TryCast(clickedElement, TextBox)
            If textBox IsNot Nothing AndAlso TryCast(textBox.DataContext, ChatSessionSummary) Is editingSession Then
                Return
            End If
            clickedElement = VisualTreeHelper.GetParent(clickedElement)
        End While

        _viewModel.CommitRenameCommand.Execute(editingSession)
    End Sub

End Class
