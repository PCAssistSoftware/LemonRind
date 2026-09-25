Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports CommunityToolkit.Mvvm.ComponentModel
Imports CommunityToolkit.Mvvm.Input
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration
Imports LemonRind.Data
Imports LemonRind.Knowledge
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Scheduler
Imports LemonRind.Services

Namespace ViewModels

    ''' <summary>
    ''' Drives the main chat window: the message list, sending/streaming,
    ''' which Lemonade model is active, the sessions sidebar, and long-term
    ''' memory - folding known/relevant facts into the system prompt each
    ''' turn and extracting new ones afterward.
    '''
    ''' Properties here are hand-written (Get/Set + SetProperty) rather than
    ''' CommunityToolkit.Mvvm's &lt;ObservableProperty&gt; attribute - that
    ''' attribute's source generator doesn't run for VB.NET projects. The
    ''' ObservableObject base
    ''' class and AsyncRelayCommand are plain runtime classes though, not
    ''' generated code, so both work fine here.
    ''' </summary>
    Public Class MainViewModel
        Inherits ObservableObject

        Private ReadOnly _chatClient As IChatClient
        Private ReadOnly _moduleRegistry As ModuleRegistry
        Private ReadOnly _managementClient As LemonadeManagementClient
        Private ReadOnly _sessionRepository As ChatSessionRepository
        Private ReadOnly _memoryService As MemoryService
        Private ReadOnly _compactionService As ConversationCompactionService
        Private ReadOnly _knowledgeRepository As KnowledgeRepository
        Private ReadOnly _knowledgeService As KnowledgeService

        ''' <summary>
        ''' Read live off AssistantSettings, not cached, same pattern as
        ''' every other Settings-driven field in this app. Since
        ''' RefreshStableSystemPrompt already re-reads this every turn (see
        ''' its own comment on why that's cheap and cache-safe), a changed
        ''' prompt takes effect on the very next message with no restart.
        ''' </summary>
        Private ReadOnly _assistantSettings As AssistantSettings
        Private ReadOnly _personaSettings As PersonaSettings

        ''' <summary>Used by InitializeAsync as a fallback to the configured chat model - see its own comment for why.</summary>
        Private ReadOnly _lemonadeSettings As LemonadeSettings

        ' Short-term memory / context compaction - the same "trigger once
        ' context usage crosses a percentage of the real limit" mental model
        ' as Microsoft.Agents.AI.Compaction's ContextWindowCompactionStrategy,
        ' hand-rolled on top of the ContextUsageCurrent/Max numbers already
        ' tracked for the UI bar. Configurable in Settings
        ' (AppSettings.Memory.CompactionTriggerPercent, 75% by default)
        ' rather than a fixed constant - 75% leaves real headroom for the
        ' reply itself (triggering at, say, 95% risks the compaction call
        ' and the next turn's reply together still not fitting), but the
        ' right number depends on the model's real context size and how
        ' verbose replies tend to be, which varies enough to be worth
        ' exposing rather than guessing one fixed value for every setup.
        ' Read fresh from _memorySettings.CompactionTriggerPercent on each
        ' check (see CompactHistoryIfNeededAsync), so changing this in
        ' Settings takes effect on the very next turn instead of needing a
        ' restart.
        Private ReadOnly _memorySettings As MemorySettings

        ' How many of the most recent messages stay verbatim (uncompacted) -
        ' enough for the model to still see the last few exchanges in full,
        ' not just a summary of them. An even number so it tends to land on
        ' user/assistant pairs rather than splitting one.
        Private Const MessagesToKeepAfterCompaction As Integer = 6

        ' Running summary of everything compacted out of _history so far -
        ' folded into the system prompt each turn (see BuildSystemPromptText),
        ' never persisted: this is *short-term* memory, scoped to the
        ' currently open session's live in-memory history, unlike the
        ' pinned/semantic facts MemoryService persists to SQLite. Cleared on
        ' NewChat/LoadSession along with _history itself.
        Private _conversationSummaryText As String = ""

        ''' <summary>How many times CompactHistoryIfNeededAsync has actually compacted this session, for the breakdown popup's "compacted N times" badge - same in-memory-only, per-session lifecycle as _conversationSummaryText, reset alongside it.</summary>
        Private _compactionCount As Integer = 0

        ' Keeps the real Microsoft.Extensions.AI conversation history (what
        ' actually gets sent to the model) for the CURRENTLY OPEN session.
        ' Rebuilt from scratch whenever the user switches sessions (see
        ' LoadSession) rather than trying to keep multiple sessions' history
        ' in memory at once. A system message is added once, first, and never
        ' duplicated - Qwen's chat template rejects a second system message or
        ' one that isn't at position 0.
        Private ReadOnly _history As New List(Of ChatMessage)

        ''' <summary>
        ''' Caches PendingTokens' own live turn-context preview
        ''' (RefreshContextBreakdownAsync) keyed by the exact InputText it
        ''' was computed for - confirmed live via Lemonade's own logs that
        ''' without this, every single hover re-ran real memory/knowledge
        ''' embedding searches even when the message box hadn't changed at
        ''' all since the last hover, wasted work each time. Nothing
        ''' (_lastPendingPreviewInputText) forces a fresh computation on the
        ''' very first hover. Also keyed on _lastPendingPreviewKnowledgeBaseId
        ''' - confirmed live that without this, switching the knowledge-base
        ''' dropdown without also editing the message box returned the
        ''' stale PendingTokens figure computed against the PREVIOUS
        ''' knowledge base, since InputText alone hadn't changed.
        ''' </summary>
        Private _lastPendingPreviewInputText As String = Nothing
        Private _lastPendingPreviewKnowledgeBaseId As String = Nothing
        Private _lastPendingPreviewTokens As Integer = 0

        Private _currentSessionId As String = ""

        ' Non-Nothing only while a SendAsync call is actually streaming a
        ' reply - StopCommand cancels it. Kept separate from IsBusy (which
        ' also covers model-switching) so Stop only ever targets an
        ' in-progress generation, never a model load.
        Private _generationCts As CancellationTokenSource = Nothing

        Public ReadOnly Property Messages As New ObservableCollection(Of ChatMessageViewModel)()

        Private _inputText As String = ""
        Public Property InputText As String
            Get
                Return _inputText
            End Get
            Set(value As String)
                If SetProperty(_inputText, value) Then
                    SendCommand.NotifyCanExecuteChanged()
                End If
            End Set
        End Property

        ' Attached file text is extracted at attach time, not send time, so
        ' a bad/unsupported file fails fast right when picked rather than
        ' after a question about it has already been typed. Nothing here is
        ' persisted separately from the chat message itself (see SendAsync's
        ' comment on how the extracted text gets folded in) - this is purely
        ' staging state for the *next* message.
        Private _attachedFileText As String = Nothing

        Private _attachedFileName As String = Nothing
        Public Property AttachedFileName As String
            Get
                Return _attachedFileName
            End Get
            Set(value As String)
                SetProperty(_attachedFileName, value)
            End Set
        End Property

        ''' <summary>
        ''' Local path to a resized copy of a picked image, already saved to
        ''' disk by AttachFile - staging state for the next message, same
        ''' role as _attachedFileText/AttachedFileName above, just for the
        ''' image path. Mutually exclusive with them - AttachFile only ever
        ''' sets one or the other, matching the app's single-attachment-slot
        ''' design.
        ''' </summary>
        Private _attachedImagePath As String = Nothing
        Public Property AttachedImagePath As String
            Get
                Return _attachedImagePath
            End Get
            Set(value As String)
                If SetProperty(_attachedImagePath, value) Then
                    SendCommand?.NotifyCanExecuteChanged()
                    OnPropertyChanged(NameOf(AttachedImageVisionWarning))
                End If
            End Set
        End Property

        ''' <summary>
        ''' Non-Nothing (and blocks Send - see CanSend) when an image is
        ''' attached but the currently selected model isn't vision-labeled -
        ''' same source as the model dropdown's own Chat/Image/Embedding
        ''' grouping (Lemonade's real /v1/models "labels" field), just a
        ''' separate lookup since a model can be both "chat" and "vision" at
        ''' once. Blocking with a clear reason here, rather than
        ''' letting the request go through and showing whatever error
        ''' Lemonade returns, mirrors how the image-model dropdown routing
        ''' already avoids a confusing HTTP 400 (see ModelSelectorItem's
        ''' comment).
        ''' </summary>
        Public ReadOnly Property AttachedImageVisionWarning As String
            Get
                If AttachedImagePath Is Nothing Then Return Nothing
                If _modelVisionSupport.GetValueOrDefault(SelectedModel, False) Then Return Nothing
                Return "The selected model doesn't support image input - pick a vision-capable model, or remove the attached image."
            End Get
        End Property

        ' True while sending a chat message OR switching models - either way
        ' the user shouldn't be able to send another message until it's done.
        Private _isBusy As Boolean = False
        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                If SetProperty(_isBusy, value) Then
                    SendCommand.NotifyCanExecuteChanged()
                End If
            End Set
        End Property

        ' Narrower than IsBusy (which also covers a normal chat reply
        ' streaming) - only true while SwitchModelAsync is actually loading a
        ' model. The model-info (ⓘ) button should stay available mid-reply,
        ' only needing to be off while there's genuinely no model loaded yet
        ' or one is actively loading.
        Private _isModelLoading As Boolean = False
        Public Property IsModelLoading As Boolean
            Get
                Return _isModelLoading
            End Get
            Set(value As Boolean)
                If SetProperty(_isModelLoading, value) Then
                    OnPropertyChanged(NameOf(IsModelDetailsAvailable))
                End If
            End Set
        End Property

        ''' <summary>Drives the model-info button's enabled state - see IsModelLoading's own comment for why this is narrower than IsBusy. Re-raised from SelectedModel's setter too, since "no model loaded yet" is the other half of this condition.</summary>
        Public ReadOnly Property IsModelDetailsAvailable As Boolean
            Get
                Return Not IsModelLoading AndAlso Not String.IsNullOrWhiteSpace(SelectedModel)
            End Get
        End Property

        ' Short status text shown near the model selector - "Loading
        ' <model>..." while a switch is in progress, an error message if one
        ' fails, or empty the rest of the time.
        Private _statusText As String = ""
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        ' Health dot in the header - refreshed on startup, after every window
        ' activation, and on a background timer while unhealthy - see
        ' _connectionRetryTimer below.
        Private _isHealthy As Boolean = False
        Public Property IsHealthy As Boolean
            Get
                Return _isHealthy
            End Get
            Set(value As Boolean)
                SetProperty(_isHealthy, value)
            End Set
        End Property

        ''' <summary>
        ''' The detailed exception message behind "Lemonade unreachable",
        ''' shown as a ToolTip on the health indicator rather than in the
        ''' shared StatusText field, since a long connection-refused message
        ''' there would crowd out everything else in the header. Nothing
        ''' while healthy, so the
        ''' ToolTip itself doesn't show at all (WPF suppresses a null
        ''' ToolTip binding).
        ''' </summary>
        Private _lastHealthCheckError As String
        Public Property LastHealthCheckError As String
            Get
                Return _lastHealthCheckError
            End Get
            Set(value As String)
                SetProperty(_lastHealthCheckError, value)
            End Set
        End Property

        ' Per-turn performance numbers, sourced from Lemonade's own GET
        ' /v1/stats (server-measured, not timed client-side - more accurate
        ' since it isn't skewed by UI/network latency). Refreshed after every
        ' completed reply, not live during streaming.
        Private _tokensInOutText As String = "-"
        Public Property TokensInOutText As String
            Get
                Return _tokensInOutText
            End Get
            Set(value As String)
                SetProperty(_tokensInOutText, value)
            End Set
        End Property

        Private _tokensPerSecondText As String = "-"
        Public Property TokensPerSecondText As String
            Get
                Return _tokensPerSecondText
            End Get
            Set(value As String)
                SetProperty(_tokensPerSecondText, value)
            End Set
        End Property

        Private _timeToFirstTokenText As String = "-"
        Public Property TimeToFirstTokenText As String
            Get
                Return _timeToFirstTokenText
            End Get
            Set(value As String)
                SetProperty(_timeToFirstTokenText, value)
            End Set
        End Property

        ' Running total across every turn in the CURRENTLY OPEN session, not
        ' Lemonade's own *_total fields (those are cumulative since the
        ' server started, across every session/app that's used it - not
        ' useful here). Reset in NewChat/LoadSession, not just at app
        ' startup, since per-turn stats shown under a tab literally named
        ' "Session" would otherwise read as misleading across different
        ' sessions ("In: 11/48" then "In: 28/33" looks like it's losing
        ' data, when it's actually just a different request's own numbers).
        ' This also resets to 0 when reopening a past session, since
        ' per-turn stats are never persisted to the database - it's "since
        ' this session was opened in this app run", not a true lifetime
        ' total.
        Private _sessionTotalInputTokens As Integer = 0
        Private _sessionTotalOutputTokens As Integer = 0

        Private _sessionTotalTokensText As String = "0 / 0"
        Public Property SessionTotalTokensText As String
            Get
                Return _sessionTotalTokensText
            End Get
            Set(value As String)
                SetProperty(_sessionTotalTokensText, value)
            End Set
        End Property

        ''' <summary>
        ''' Tool calls from the most recently completed turn - a copy of that
        ''' turn's ChatMessageViewModel.ToolCalls, kept here too so the right
        ''' panel's Session tab can show them without needing to reach into
        ''' the Messages collection and find the last assistant bubble itself.
        ''' </summary>
        Public ReadOnly Property CurrentTurnToolCalls As New ObservableCollection(Of ToolCallResult)()

        ''' <summary>Which right-panel tab is showing - "Stats" or "Modules".</summary>
        Private _rightPanelTab As String = "Stats"
        Public Property RightPanelTab As String
            Get
                Return _rightPanelTab
            End Get
            Set(value As String)
                SetProperty(_rightPanelTab, value)
            End Set
        End Property

        ''' <summary>
        ''' Read-only status list for the right panel's Modules tab - actually
        ''' toggling a module lives in the Settings screen (see
        ''' OpenSettingsCommand). A real ObservableCollection, populated once
        ''' in the constructor and rebuilt via RefreshModulesDisplay() -
        ''' IAssistantModule doesn't implement INotifyPropertyChanged, so a
        ''' module's IsEnabled changing is otherwise invisible to an
        ''' already-open ItemsControl bound to it; rebuilding the collection
        ''' forces WPF to regenerate each item's container, which reads the
        ''' current IsEnabled fresh.
        ''' </summary>
        Public ReadOnly Property Modules As New ObservableCollection(Of IAssistantModule)()

        ''' <summary>Called on MainWindow's Activated event (MainWindow.xaml.vb) - a modal Settings dialog closing reliably re-activates the main window, the same trigger RefreshSessions() already relies on.</summary>
        Public Sub RefreshModulesDisplay()
            Modules.Clear()
            For Each assistantModule In _moduleRegistry.AllModules
                Modules.Add(assistantModule)
            Next

            ' Drives whether the KB dropdown shows in the header
            ' (MainWindow.xaml) and whether SendAsync bothers calling
            ' KnowledgeService at all - refreshed at the same point Modules
            ' itself already is, so toggling it in Settings takes effect on
            ' the next window activation.
            IsKnowledgeModuleEnabled = _moduleRegistry.AllModules.OfType(Of Knowledge.KnowledgeModule)().Any(Function(m) m.IsEnabled)
        End Sub

        Private _isKnowledgeModuleEnabled As Boolean
        Public Property IsKnowledgeModuleEnabled As Boolean
            Get
                Return _isKnowledgeModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isKnowledgeModuleEnabled, value)
            End Set
        End Property

        ''' <summary>Grouped by capability (see ModelSelectorItem) - populated already sorted by CategorySortOrder in InitializeAsync.</summary>
        Public ReadOnly Property AvailableModels As New ObservableCollection(Of ModelSelectorItem)()

        ''' <summary>
        ''' Knowledge bases the header's KB selector can offer - always
        ''' starts with a "(No knowledge base)" sentinel (Id = Nothing) since
        ''' having none attached is the normal default, not a special case to
        ''' hide. Loaded once in InitializeAsync; the Knowledge Bases Settings
        ''' screen doesn't need this list live-updated while a chat is open.
        ''' </summary>
        Public ReadOnly Property AvailableKnowledgeBases As New ObservableCollection(Of KnowledgeBaseSummary)()

        Private _selectedKnowledgeBase As KnowledgeBaseSummary = Nothing
        ''' <summary>
        ''' Which knowledge base (if any) is attached to the CURRENT chat -
        ''' one at a time by design. Persisted per-session immediately on
        ''' change (like renaming a chat), not deferred to an explicit save,
        ''' matching how the rest of this app treats session-scoped state.
        ''' Bound to the ComboBox via SelectedItem, not SelectedValue/
        ''' SelectedValuePath - combining SelectedValuePath with
        ''' DisplayMemberPath doesn't reliably drive a WPF ComboBox's own
        ''' display text (it shows the raw KnowledgeBaseSummary.ToString()
        ''' instead of Name), while plain SelectedItem always works
        ''' correctly.
        ''' </summary>
        Public Property SelectedKnowledgeBase As KnowledgeBaseSummary
            Get
                Return _selectedKnowledgeBase
            End Get
            Set(value As KnowledgeBaseSummary)
                If SetProperty(_selectedKnowledgeBase, value) Then
                    OnPropertyChanged(NameOf(SelectedKnowledgeBaseId))
                    If Not String.IsNullOrEmpty(_currentSessionId) Then
                        _sessionRepository.SetAttachedKnowledgeBase(_currentSessionId, SelectedKnowledgeBaseId)
                    End If
                End If
            End Set
        End Property

        ''' <summary>The Id side of SelectedKnowledgeBase - what SendAsync actually needs for retrieval.</summary>
        Public ReadOnly Property SelectedKnowledgeBaseId As String
            Get
                Return SelectedKnowledgeBase?.Id
            End Get
        End Property

        ' ComboBox binding only needs model names (AvailableModels above) -
        ' this side table is what looks up each model's MaxContextWindow for
        ' the context-usage bar, keyed the same way, populated once in
        ' InitializeAsync from the same ListDownloadedModelsAsync call.
        Private ReadOnly _modelContextWindows As New Dictionary(Of String, Integer)()

        ''' <summary>Model id -> ModelSelectorItem.Category, populated alongside _modelContextWindows - checked in SendAsync to decide whether a turn goes to the LLM at all or straight to image generation.</summary>
        Private ReadOnly _modelCategories As New Dictionary(Of String, String)()

        ''' <summary>Model id -> whether Lemonade's own /v1/models labels this model "vision" - a model can be both "chat" and "vision" at once, so this is a separate lookup from _modelCategories, not folded into it.</summary>
        Private ReadOnly _modelVisionSupport As New Dictionary(Of String, Boolean)()

        ' Context-usage bar, above the input box - tokens used vs. the
        ' selected model's max context window. "Used" is approximated as the
        ' last completed turn's input+output tokens (from GET /v1/stats),
        ' since that's what the NEXT turn's request will already contain as
        ' history - matches app-architecture.md's "both numbers are already
        ' available, no new surface needed" note.
        Private _contextUsageCurrent As Integer = 0
        Public Property ContextUsageCurrent As Integer
            Get
                Return _contextUsageCurrent
            End Get
            Set(value As Integer)
                SetProperty(_contextUsageCurrent, value)
            End Set
        End Property

        Private _contextUsageMax As Integer = 0
        Public Property ContextUsageMax As Integer
            Get
                Return _contextUsageMax
            End Get
            Set(value As Integer)
                SetProperty(_contextUsageMax, value)
            End Set
        End Property

        Private _contextUsageText As String = ""
        Public Property ContextUsageText As String
            Get
                Return _contextUsageText
            End Get
            Set(value As String)
                SetProperty(_contextUsageText, value)
            End Set
        End Property

        ''' <summary>Total pixel width the context-breakdown popup's bar is drawn at - fixed rather than measured, so RefreshContextBreakdown can precompute every segment's own width without a live layout pass.</summary>
        Private Const ContextBreakdownBarWidth As Double = 260

        Private _isContextBreakdownOpen As Boolean = False
        ''' <summary>Drives the popup's IsOpen - toggled from MainWindow's MouseEnter/MouseLeave handlers on the context-usage text, not a click, since this is a glanceable hover detail, not something to navigate into.</summary>
        Public Property IsContextBreakdownOpen As Boolean
            Get
                Return _isContextBreakdownOpen
            End Get
            Set(value As Boolean)
                SetProperty(_isContextBreakdownOpen, value)
            End Set
        End Property

        Private _contextBreakdownInfo As ContextBreakdown
        ''' <summary>Nothing until RefreshContextBreakdown first runs (on hover) - the popup's own XAML only reads this while IsContextBreakdownOpen is true, by which point it's always already set.</summary>
        Public Property ContextBreakdownInfo As ContextBreakdown
            Get
                Return _contextBreakdownInfo
            End Get
            Set(value As ContextBreakdown)
                SetProperty(_contextBreakdownInfo, value)
            End Set
        End Property

        Private _isContextBreakdownLoading As Boolean = False
        ''' <summary>True while RefreshContextBreakdownAsync's real /v1/tokenize calls are in flight - guards against overlapping refreshes if the mouse re-enters quickly, same reasoning openlumara's own context popup guards its refresh for.</summary>
        Public Property IsContextBreakdownLoading As Boolean
            Get
                Return _isContextBreakdownLoading
            End Get
            Set(value As Boolean)
                SetProperty(_isContextBreakdownLoading, value)
            End Set
        End Property

        ' Tracks what Lemonade actually has loaded right now, separately from
        ' SelectedModel (what the ComboBox shows) - InitializeAsync sets both
        ' to the same value without triggering a reload; after that, changing
        ' SelectedModel is what triggers a real Lemonade /v1/load call.
        Private _currentlyLoadedModel As String = ""

        Private _selectedModel As String = ""
        Public Property SelectedModel As String
            Get
                Return _selectedModel
            End Get
            Set(value As String)
                ' Never store a bare Nothing - the ComboBox's SelectedValue
                ' binding (MainWindow.xaml) is two-way, and pushes Nothing in
                ' here whenever its ItemsSource is momentarily empty (e.g.
                ' RefreshAvailableModelsAsync's Clear()-then-repopulate).
                ' Every other _modelContextWindows.GetValueOrDefault(SelectedModel, ...)
                ' call site assumes a string, and Dictionary throws
                ' ArgumentNullException on a null key.
                Dim normalizedValue = If(value, "")
                If SetProperty(_selectedModel, normalizedValue) Then
                    ' CanSend depends on SelectedModel now (see its comment) -
                    ' SendCommand is Nothing the very first time this setter
                    ' could theoretically run (the "" default doesn't go
                    ' through this Set), so this is safe without a null-check
                    ' in practice, but Nothing-conditional anyway since a
                    ' property setter running before the constructor finishes
                    ' assigning SendCommand is exactly the kind of ordering
                    ' assumption worth not trusting blindly.
                    SendCommand?.NotifyCanExecuteChanged()
                    OnPropertyChanged(NameOf(AttachedImageVisionWarning))
                    OnPropertyChanged(NameOf(IsModelDetailsAvailable))

                    If Not String.IsNullOrEmpty(normalizedValue) AndAlso normalizedValue <> _currentlyLoadedModel Then
                        ' Property setters can't be Async Functions, so the actual
                        ' load work happens in a fire-and-forget helper below -
                        ' SwitchModelAsync itself handles its own errors so this
                        ' never surfaces as an unobserved task exception.
                        SwitchModelFireAndForget(normalizedValue)
                    End If
                End If
            End Set
        End Property

        ''' <summary>Sidebar list, most recently active session first.</summary>
        Public ReadOnly Property Sessions As New ObservableCollection(Of ChatSessionSummary)()

        ''' <summary>
        ''' What the sidebar ListBox actually binds to: a flat mix of
        ''' FolderHeaderSummary and ChatSessionSummary rows (a collapsed
        ''' folder's sessions simply aren't in this list), rebuilt by
        ''' RebuildSidebarItems whenever Sessions changes. Sessions itself
        ''' stays the authoritative list every other method in this class
        ''' reads/mutates, avoiding a type change rippling through
        ''' DeleteSession/NewChat/SendAsync/etc.
        ''' </summary>
        Public ReadOnly Property SidebarItems As New ObservableCollection(Of Object)()

        ''' <summary>
        ''' What the ListBox's own SelectedItem is really bound to (not
        ''' SelectedSession directly) - SidebarItems can contain a
        ''' FolderHeaderSummary, which SelectedSession's declared type
        ''' (ChatSessionSummary) can't hold. Delegates into the existing,
        ''' unchanged SelectedSession/LoadSession flow only when the newly
        ''' selected row really is a session; clicking a folder header just
        ''' leaves the previously selected session as-is.
        ''' </summary>
        Private _selectedSidebarItem As Object
        Public Property SelectedSidebarItem As Object
            Get
                Return _selectedSidebarItem
            End Get
            Set(value As Object)
                If SetProperty(_selectedSidebarItem, value) Then
                    Dim sessionValue = TryCast(value, ChatSessionSummary)
                    If sessionValue IsNot Nothing Then
                        SelectedSession = sessionValue
                    End If
                End If
            End Set
        End Property

        Private _searchText As String = ""
        Public Property SearchText As String
            Get
                Return _searchText
            End Get
            Set(value As String)
                If SetProperty(_searchText, value) Then
                    RefreshSessions()
                End If
            End Set
        End Property

        ' Guards against LoadSession firing again while it's the one setting
        ' SelectedSession itself (e.g. right after NewChat inserts a summary
        ' and selects it) - without this, selecting a session from code would
        ' immediately re-trigger a reload of the session already being shown.
        Private _isLoadingSession As Boolean = False

        Private _selectedSession As ChatSessionSummary
        Public Property SelectedSession As ChatSessionSummary
            Get
                Return _selectedSession
            End Get
            Set(value As ChatSessionSummary)
                If SetProperty(_selectedSession, value) AndAlso value IsNot Nothing AndAlso Not _isLoadingSession Then
                    LoadSession(value.Id)
                End If
            End Set
        End Property

        Public ReadOnly Property SendCommand As IAsyncRelayCommand
        Public ReadOnly Property NewChatCommand As IRelayCommand

        ''' <summary>Paperclip button - opens a file picker and extracts the chosen file's text immediately (see FileTextExtractor).</summary>
        Public ReadOnly Property AttachFileCommand As IRelayCommand
        Public ReadOnly Property RemoveAttachedFileCommand As IRelayCommand

        ''' <summary>Cancels an in-progress reply - enabled only while one is actually streaming.</summary>
        Public ReadOnly Property StopCommand As IRelayCommand

        ''' <summary>Pencil icon - enters edit mode on the clicked session's row.</summary>
        Public ReadOnly Property StartRenameCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Enter key in the edit box - persists the new title.</summary>
        Public ReadOnly Property CommitRenameCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Escape key in the edit box - reverts to the title before editing started.</summary>
        Public ReadOnly Property CancelRenameCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Trash icon - confirms, then permanently deletes a session.</summary>
        Public ReadOnly Property DeleteSessionCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Right-click menu item - opens MoveToFolderDialog, which handles creating a new folder itself if needed.</summary>
        Public ReadOnly Property MoveToFolderCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Right-click menu item - opens TagManagerDialog, which writes tag changes immediately, not deferred.</summary>
        Public ReadOnly Property ManageTagsCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Right-click menu item - saves the whole session (any session, not just the currently open one - reads straight from the DB) as a single Markdown file.</summary>
        Public ReadOnly Property ExportChatToMarkdownCommand As IRelayCommand(Of ChatSessionSummary)

        ''' <summary>Gear icon in the header - opens the Settings window.</summary>
        Public ReadOnly Property OpenSettingsCommand As IRelayCommand

        ''' <summary>Header button next to the health indicator - opens a non-modal LogViewerWindow streaming Lemonade's own logs.</summary>
        Public ReadOnly Property OpenLogViewerCommand As IRelayCommand

        ''' <summary>Button next to the model dropdown - opens ModelDetailsDialog for whichever model is currently selected.</summary>
        Public ReadOnly Property OpenModelDetailsCommand As IAsyncRelayCommand

        ''' <summary>Clicking the health indicator while "Lemonade unreachable" is shown retries immediately, rather than waiting for the next background poll or the next window activation.</summary>
        Public ReadOnly Property RetryConnectionCommand As IAsyncRelayCommand

        ''' <summary>Stats tab button - shows the real, current _history(0) text, for visibility into what's actually being sent.</summary>
        Public ReadOnly Property OpenSystemPromptCommand As IAsyncRelayCommand

        ''' <summary>
        ''' Stats tab button - shows the VOLATILE per-turn context
        ''' (time-awareness + relevant memories/knowledge) that
        ''' AppendVolatileContext folds into the newest message each turn,
        ''' which View system prompt deliberately never shows (that one only
        ''' reads _history(0)).
        ''' </summary>
        Public ReadOnly Property OpenTurnContextCommand As IAsyncRelayCommand

        ''' <summary>Stats tab button - shows every enabled tool's real name/description/JSON schema, exactly as sent to Lemonade on every request - the context breakdown popup can show how many tokens tools cost, but not what's actually in them.</summary>
        Public ReadOnly Property OpenToolsSentCommand As IAsyncRelayCommand

        ''' <summary>Stats/Modules tab buttons in the right panel - CommandParameter is the tab name.</summary>
        Public ReadOnly Property SelectRightPanelTabCommand As IRelayCommand(Of String)

        Private ReadOnly _settingsWindowFactory As Func(Of SettingsWindow)
        Private ReadOnly _logViewerWindowFactory As Func(Of LogViewerWindow)
        Private ReadOnly _schedulerNotifier As SchedulerNotifier
        Private ReadOnly _imageGenerationService As ImageGenerationService
        Private ReadOnly _imageGenSettings As ImageGenSettings
        Private ReadOnly _imageAttachmentService As ImageAttachmentService
        Private ReadOnly _connectionRetryTimer As DispatcherTimer

        Public Sub New(chatClient As IChatClient, moduleRegistry As ModuleRegistry, managementClient As LemonadeManagementClient, sessionRepository As ChatSessionRepository, memoryService As MemoryService, compactionService As ConversationCompactionService, knowledgeRepository As KnowledgeRepository, knowledgeService As KnowledgeService, appSettings As AppSettings, settingsWindowFactory As Func(Of SettingsWindow), schedulerNotifier As SchedulerNotifier, imageGenerationService As ImageGenerationService, logViewerWindowFactory As Func(Of LogViewerWindow), imageAttachmentService As ImageAttachmentService)
            _chatClient = chatClient
            _moduleRegistry = moduleRegistry
            _managementClient = managementClient
            _sessionRepository = sessionRepository
            _memoryService = memoryService
            _compactionService = compactionService
            _knowledgeRepository = knowledgeRepository
            _knowledgeService = knowledgeService
            _memorySettings = appSettings.Memory
            _lemonadeSettings = appSettings.Lemonade
            _assistantSettings = appSettings.Assistant
            _personaSettings = appSettings.Persona
            _settingsWindowFactory = settingsWindowFactory
            _schedulerNotifier = schedulerNotifier
            _imageGenerationService = imageGenerationService
            _imageGenSettings = appSettings.ImageGen
            _logViewerWindowFactory = logViewerWindowFactory
            _imageAttachmentService = imageAttachmentService
            ' A scheduled job fires on the poller's own thread pool thread
            ' (SchedulerModule's Timer), not the UI thread - RefreshSessions()
            ' touches an ObservableCollection bound to the sidebar, so it
            ' must be marshaled onto the Dispatcher. Window.Activated
            ' (MainWindow.xaml.vb) only refreshes on focus change, which
            ' would miss a job completing while the window is already
            ' focused. ReloadCurrentSessionIfMatching alongside it - a job's
            ' own updates (its prompt appearing, a "running in background"
            ' note, its eventual reply/error) would otherwise only become
            ' visible in an already-open chat the next time the user
            ' navigated away and back, since LoadSession's own same-session
            ' guard (see its comment) deliberately no-ops a spurious
            ' reselect of the currently-open chat.
            AddHandler _schedulerNotifier.SessionUpdated, Sub(sender, sessionId) Application.Current.Dispatcher.Invoke(Sub()
                RefreshSessions()
                ReloadCurrentSessionIfMatching(sessionId)
            End Sub)
            SendCommand = New AsyncRelayCommand(AddressOf SendAsync, AddressOf CanSend)
            NewChatCommand = New RelayCommand(AddressOf NewChat)
            AttachFileCommand = New RelayCommand(AddressOf AttachFile)
            RemoveAttachedFileCommand = New RelayCommand(AddressOf RemoveAttachedFile)
            StartRenameCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf StartRename)
            CommitRenameCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf CommitRename)
            CancelRenameCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf CancelRename)
            DeleteSessionCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf DeleteSession)
            MoveToFolderCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf MoveToFolder)
            ManageTagsCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf ManageTags)
            ExportChatToMarkdownCommand = New RelayCommand(Of ChatSessionSummary)(AddressOf ExportChatToMarkdown)
            StopCommand = New RelayCommand(AddressOf StopGeneration, AddressOf CanStop)
            OpenSettingsCommand = New RelayCommand(AddressOf OpenSettings)
            OpenLogViewerCommand = New RelayCommand(AddressOf OpenLogViewer)
            OpenModelDetailsCommand = New AsyncRelayCommand(AddressOf OpenModelDetailsAsync)
            RetryConnectionCommand = New AsyncRelayCommand(AddressOf RefreshAvailableModelsAsync)
            OpenSystemPromptCommand = New AsyncRelayCommand(AddressOf OpenSystemPromptAsync)
            OpenTurnContextCommand = New AsyncRelayCommand(AddressOf OpenTurnContextAsync)
            OpenToolsSentCommand = New AsyncRelayCommand(AddressOf OpenToolsSentAsync)
            SelectRightPanelTabCommand = New RelayCommand(Of String)(Sub(tab) RightPanelTab = tab)

            ' Keeps trying in the background while Lemonade is unreachable,
            ' so coming back doesn't require clicking Retry or alt-tabbing
            ' away and back. A no-op tick while already healthy costs
            ' nothing worth guarding against, so this just runs for the
            ' app's whole lifetime rather than being started/stopped around
            ' IsHealthy transitions.
            _connectionRetryTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(15)}
            AddHandler _connectionRetryTimer.Tick,
                Async Sub(sender, e)
                    If Not IsHealthy Then Await RefreshAvailableModelsAsync(CancellationToken.None)
                End Sub
            _connectionRetryTimer.Start()
        End Sub

        Private Sub OpenSettings()
            Dim settingsWindow = _settingsWindowFactory()
            settingsWindow.Owner = Application.Current.MainWindow
            settingsWindow.ShowDialog()
        End Sub

        ''' <summary>
        ''' Non-modal (Show, not ShowDialog) - meant to sit open alongside
        ''' the main window while chatting, not block it. Each click opens a
        ''' fresh window (and its own WebSocket connection) rather than
        ''' tracking/reusing a single instance - simplest thing that works,
        ''' and the user can just close a duplicate if they open one by
        ''' accident.
        ''' </summary>
        Private Sub OpenLogViewer()
            Dim logViewerWindow = _logViewerWindowFactory()
            logViewerWindow.Owner = Application.Current.MainWindow
            logViewerWindow.Show()
        End Sub

        ''' <summary>
        ''' Always queries fresh (both /v1/models and /v1/health) rather than
        ''' reusing _modelContextWindows/_modelCategories/_modelVisionSupport -
        ''' those cache what the dropdown needs (labels/context window for
        ''' routing/display), not Recipe/SizeGb, and the real runtime detail
        ''' (device, llamacpp_args) only ever exists on /v1/health, not
        ''' cached anywhere. A low-frequency, user-triggered click is exactly
        ''' the kind of call worth keeping simple/always-correct over adding
        ''' more cached state to keep in sync.
        ''' </summary>
        Private Async Function OpenModelDetailsAsync() As Task
            Try
                Dim models = Await _managementClient.ListDownloadedModelsAsync(CancellationToken.None)
                Dim modelInfo = models.FirstOrDefault(Function(m) m.Id = SelectedModel)
                If modelInfo Is Nothing Then Return

                Dim health = Await _managementClient.GetHealthAsync(CancellationToken.None)
                Dim loadedInfo = health.AllModelsLoaded?.FirstOrDefault(Function(m) m.ModelName = SelectedModel)

                ' modelInfo.MaxContextWindow is the catalog's declared
                ' maximum, not necessarily what this model is actually
                ' loaded with right now (Lemonade allows loading at a
                ' smaller ctx_size) - prefer the real loaded value
                ' (loadedInfo.RecipeOptions.CtxSize) whenever it's available,
                ' same real-vs-declared distinction ApplyRealLoadedContextWindow
                ' exists for. Falls back to the catalog max for a model
                ' that's downloaded but not currently loaded, since there's
                ' nothing more real to show in that case.
                Dim realOrMaxContextWindow = If(loadedInfo?.RecipeOptions?.CtxSize, modelInfo.MaxContextWindow)

                ModelDetailsDialog.Show(
                    modelInfo.Id, modelInfo.Labels, realOrMaxContextWindow, modelInfo.SizeGb, modelInfo.Recipe,
                    isCurrentlyLoaded:=loadedInfo IsNot Nothing,
                    device:=loadedInfo?.Device,
                    llamacppArgs:=loadedInfo?.RecipeOptions?.LlamacppArgs)
            Catch ex As Exception
                StatusText = $"Couldn't load model details: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Shows _history(0) exactly as it stands right now - not a fresh
        ''' rebuild via BuildSystemPromptText, since the whole point is to
        ''' show what's really in the context this instant, matching what
        ''' the last (or next) actual request would carry.
        ''' </summary>
        Private Async Function OpenSystemPromptAsync() As Task
            ' _history(0) is always the system message - the same invariant
            ' SendAsync/CompactHistoryIfNeededAsync already rely on.
            Dim promptText = If(_history.Count > 0, _history(0).Text, Nothing)
            If promptText Is Nothing Then
                SystemPromptDialog.Show("(no system message yet - send a message first)")
                Return
            End If
            Dim tokenCount = Await TryTokenizeAsync(promptText, CancellationToken.None)
            SystemPromptDialog.Show(promptText,
                description:=$"Exactly what's currently sitting at the front of the model's context for this chat - the same text that gets rebuilt each turn. (~{tokenCount:N0} tokens)")
        End Function

        ''' <summary>
        ''' Shows every enabled tool's real name/description/JSON schema,
        ''' pretty-printed (markdown headers, indented JSON, "---"
        ''' separators) for actual readability - the token count itself is
        ''' still computed from BuildRawToolsText's plain, un-formatted
        ''' version (the literal text that's tokenized everywhere else this
        ''' figure appears), NOT from this prettified display text.
        ''' Confirmed live that mixing those up matters: pasting this
        ''' READABLE version into an external tokenizer to double-check
        ''' will read noticeably higher than the number shown here, since
        ''' formatting alone costs real tokens a tokenizer counts but this
        ''' app's real request never actually sends - hence the note in the
        ''' description below, so that's expected, not a bug, if anyone
        ''' tries it.
        ''' </summary>
        Private Async Function OpenToolsSentAsync() As Task
            Dim tools = _moduleRegistry.GetEnabledTools()
            If Not tools.Any() Then
                SystemPromptDialog.Show("(no modules with tools are currently enabled)", title:="Tools sent",
                    description:="Every enabled tool's real name/description/JSON schema, exactly as sent to Lemonade alongside every request.")
                Return
            End If

            Dim jsonOptions As New JsonSerializerOptions With {.WriteIndented = True}
            Dim sections = tools.Select(Function(t)
                                             Dim declaration = TryCast(t, AIFunctionDeclaration)
                                             Dim schemaText = If(declaration IsNot Nothing, JsonSerializer.Serialize(declaration.JsonSchema, jsonOptions), "(no schema)")
                                             Return $"### {t.Name}{Environment.NewLine}{t.Description}{Environment.NewLine}{Environment.NewLine}{schemaText}"
                                         End Function)
            Dim prettyToolsText = String.Join(Environment.NewLine & Environment.NewLine & "---" & Environment.NewLine & Environment.NewLine, sections)

            ' Reuses EstimateCommittedContextTokensAsync's own Tools figure
            ' (not a separate tokenize call) so this always matches the
            ' breakdown popup's "tools" number exactly, including the
            ' tool-use preamble overhead folded into it - see
            ' ToolUsePreambleOverheadTokens' own comment for why that's not
            ' visible in the text below (it's not real schema content, so
            ' there's nothing to show here for it).
            Dim toolsTokenCount = (Await EstimateCommittedContextTokensAsync(CancellationToken.None)).Tools
            SystemPromptDialog.Show(prettyToolsText, title:="Tools sent",
                description:=$"Every enabled tool's real name/description/JSON schema, exactly as sent to Lemonade alongside every request - this is what the context breakdown popup's ""tools"" figure is the token cost of (includes an estimated tool-use framework overhead on top of the schemas themselves - see the breakdown popup for more). Reformatted here for readability - pasting THIS text into an external tokenizer will read lower than the figure below for that same reason. (~{toolsTokenCount:N0} tokens)")
        End Function

        ''' <summary>
        ''' The genuine volatile per-turn content (time-awareness + whatever
        ''' memory/knowledge search surfaces) for the given draft text -
        ''' shared by OpenTurnContextAsync (a full preview dialog) and
        ''' RefreshContextBreakdownAsync's PendingTokens (just needs the
        ''' token cost) so the exact same real search
        ''' (GetRelevantMemoriesTextAsync/GetRelevantChunksTextAsync,
        ''' matching what SendAsync itself calls) only needs writing once.
        ''' Genuinely empty (not a placeholder note - that's each caller's
        ''' own UI concern) when draftText is blank, since neither search
        ''' has real text to search against.
        ''' </summary>
        Private Async Function BuildLiveTurnContextTextAsync(draftText As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim sections As New List(Of String) From {TimeAwareness.BuildTimeAwarenessText()}

            If Not String.IsNullOrWhiteSpace(draftText) Then
                Dim relevantMemoriesText = Await _memoryService.GetRelevantMemoriesTextAsync(draftText, cancellationToken)
                If Not String.IsNullOrEmpty(relevantMemoriesText) Then sections.Add(relevantMemoriesText)

                If IsKnowledgeModuleEnabled Then
                    Dim relevantKnowledgeText = Await _knowledgeService.GetRelevantChunksTextAsync(SelectedKnowledgeBaseId, draftText, cancellationToken)
                    If Not String.IsNullOrEmpty(relevantKnowledgeText) Then sections.Add(relevantKnowledgeText)
                End If
            End If

            Return String.Join(Environment.NewLine & Environment.NewLine, sections)
        End Function

        ''' <summary>
        ''' Shows the VOLATILE per-turn context - time-awareness plus
        ''' whatever memory/knowledge search would surface for the text
        ''' currently sitting in the message box, via
        ''' BuildLiveTurnContextTextAsync - a genuine preview, not a guess.
        ''' Memory/knowledge both need real text to search against, so with
        ''' an empty message box only the time line is real content, with an
        ''' explanation appended for display rather than silently omitting
        ''' the other sections.
        ''' </summary>
        Private Async Function OpenTurnContextAsync() As Task
            Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                Dim turnContextText = Await BuildLiveTurnContextTextAsync(InputText, cts.Token)
                Dim tokenCount = Await TryTokenizeAsync(turnContextText, cts.Token)

                Dim displayText = turnContextText
                If String.IsNullOrWhiteSpace(InputText) Then
                    displayText &= Environment.NewLine & Environment.NewLine &
                        "(Type something in the message box first to preview what relevant memories/knowledge would be included for it - both need real text to search against.)"
                End If

                SystemPromptDialog.Show(displayText, title:="Turn context (preview)",
                    description:=$"What gets silently prepended to your NEXT message when you hit Send - never shown in the chat, never in View system prompt. Recomputed fresh every turn, so this is a live preview based on the message box's current text, not a historical record. (~{tokenCount:N0} tokens)")
            End Using
        End Function

        ''' <summary>
        ''' Raw chars/4 estimates (not the model's real tokenizer - see
        ''' ContextBreakdown's own comment) for what's already committed to
        ''' context right now: the stable system prompt, every enabled
        ''' tool's schema, and _history. Shared by RefreshContextBreakdown
        ''' (which further rescales these for display) and SendAsync (which
        ''' needs their sum to keep MaxOutputTokens from ever requesting more
        ''' total tokens than the model's context window actually has room
        ''' for on top of them - a real cause of an always-empty reply,
        ''' distinct from the "model got stuck mid-reasoning" empty-reply
        ''' case the retry loop already handles).
        ''' </summary>
        ''' <summary>
        ''' Real counts via Lemonade's own POST /v1/tokenize (the actual
        ''' model tokenizer), batched to three calls total (one string per
        ''' category) rather than one per tool/message - confirmed live
        ''' this endpoint exists and works, and that it's dramatically more
        ''' accurate than the chars/4 guess this app used before: that
        ''' guess was confirmed to disagree with Lemonade's own real
        ''' prompt_tokens by thousands of tokens, in both directions,
        ''' depending on how JSON-heavy the content was - no fixed
        ''' chars-per-token ratio can track that. Falls back to the old
        ''' chars/4 approximation (UsedRealTokenizer:=False) only if the
        ''' tokenize call itself fails - an older Lemonade version without
        ''' this endpoint, or a transient network issue - so estimation
        ''' still degrades gracefully rather than breaking outright.
        ''' </summary>
        ''' <summary>
        ''' Even the exact wire-format tools array (BuildRawToolsText) can't
        ''' capture the tool-use preamble/instructions Qwen's chat template
        ''' injects once, server-side, whenever tools are present in a
        ''' request - there's no API to render that without a real
        ''' generation (see SendAsync's own MaxOutputTokens comment for why
        ''' that's been checked and ruled out). Unlike the earlier,
        ''' rightly-rejected "template overhead" figure (circularly defined
        ''' as "whatever makes the numbers agree"), this is a genuinely
        ''' measured, independently-confirmed constant: repeated live
        ''' testing (three consecutive turns of a fresh conversation,
        ''' tools-enabled vs. tools-disabled as a direct control) found a
        ''' stable ~2,200-token gap between the real total and every other
        ''' known component, present ONLY when tools are active and
        ''' essentially zero with tools off - consistent with a roughly
        ''' fixed per-request cost, not something that scales with
        ''' conversation length. Added directly onto the tools figure
        ''' (rather than a separate line) since that's genuinely where it
        ''' comes from. Worth recalibrating if this app's typical active
        ''' tool count changes drastically from what this was measured
        ''' against (~68).
        ''' </summary>
        Private Const ToolUsePreambleOverheadTokens As Integer = 2200

        Private Async Function EstimateCommittedContextTokensAsync(cancellationToken As CancellationToken) As Task(Of (SystemPrompt As Integer, Tools As Integer, History As Integer, UsedRealTokenizer As Boolean))
            Dim systemPromptText = If(_history.Count > 0, _history(0).Text, "")

            Dim toolsText = BuildRawToolsText()
            Dim toolUseOverhead = If(_moduleRegistry.GetEnabledTools().Count > 0, ToolUsePreambleOverheadTokens, 0)

            Dim historyTextBuilder As New Text.StringBuilder()
            For i = 1 To _history.Count - 1
                historyTextBuilder.AppendLine(ExtractMessageText(_history(i)))
            Next
            Dim historyText = historyTextBuilder.ToString()

            Try
                Dim systemPromptTokens = Await _managementClient.TokenizeAsync(systemPromptText, cancellationToken)
                Dim toolsTokens = Await _managementClient.TokenizeAsync(toolsText, cancellationToken)
                Dim historyTokens = Await _managementClient.TokenizeAsync(historyText, cancellationToken)
                Return (systemPromptTokens, toolsTokens + toolUseOverhead, historyTokens, True)
            Catch
                Const charsPerToken = 4
                Return (systemPromptText.Length \ charsPerToken, toolsText.Length \ charsPerToken + toolUseOverhead, historyText.Length \ charsPerToken, False)
            End Try
        End Function

        ''' <summary>
        ''' The literal text tokenized for the "tools" figure everywhere it
        ''' appears (breakdown popup, "Tools sent" viewer) - one shared
        ''' builder so what's DISPLAYED to the user and what's TOKENIZED can
        ''' never drift apart again. Builds the actual OpenAI wire-format
        ''' tools array - [{"type":"function","function":{"name":...,
        ''' "description":...,"parameters":...}}, ...] - not just a bare
        ''' concatenation of each tool's name/description/schema. Confirmed
        ''' live via a direct side-by-side tokenize comparison that the
        ''' wrapper syntax itself costs real tokens (~15/tool) a bare
        ''' concatenation missed entirely - a genuine accuracy improvement,
        ''' not the whole story: this still can't capture whatever tool-use
        ''' preamble the chat template might inject once when tools are
        ''' present, since no API exists to render that without a real
        ''' generation (see SendAsync's own MaxOutputTokens comment) - the
        ''' remaining gap against Lemonade's real total should be smaller
        ''' now, not necessarily zero. AIFunction (every real tool in this
        ''' app) derives from AIFunctionDeclaration, which is where
        ''' JsonSchema lives - confirmed via reflection against the actual
        ''' installed package, not assumed. A tool that's somehow a plain
        ''' AITool (declared but not invokable - see
        ''' FunctionInvokingChatClient's own docs) contributes an empty
        ''' parameters object, matching how the OpenAI SDK itself would
        ''' have nothing real to serialize for one either.
        ''' </summary>
        Private Function BuildRawToolsText() As String
            Dim wireTools = _moduleRegistry.GetEnabledTools().Select(
                Function(t)
                    Dim declaration = TryCast(t, AIFunctionDeclaration)
                    Dim parameters As Object = If(declaration IsNot Nothing, CType(declaration.JsonSchema, Object), CType(JsonDocument.Parse("{}").RootElement, Object))
                    Return New With {
                        .type = "function",
                        .function = New With {
                            .name = t.Name,
                            .description = t.Description,
                            .parameters = parameters
                        }
                    }
                End Function).ToList()
            Return JsonSerializer.Serialize(wireTools)
        End Function

        ''' <summary>
        ''' ChatMessage.Text only concatenates TextContent - same behavior
        ''' already confirmed for a streaming update's own .Text (see
        ''' StreamOneReplyAsync's comment on reasoning). Confirmed live this
        ''' was a real, substantial gap for the history count above: any
        ''' message with an actual tool call in it (a FunctionCallContent
        ''' for the call, a FunctionResultContent for its result - inserted
        ''' into _history automatically by UseFunctionInvocation whenever a
        ''' tool is genuinely called) contributes its arguments/result
        ''' content to the real prompt Lemonade tokenizes, but nothing to a
        ''' .Text-only read.
        ''' </summary>
        Private Shared Function ExtractMessageText(message As ChatMessage) As String
            Dim sb As New Text.StringBuilder()
            For Each content In message.Contents
                Dim textContent = TryCast(content, TextContent)
                If textContent IsNot Nothing Then
                    sb.Append(textContent.Text)
                    Continue For
                End If

                Dim functionCall = TryCast(content, FunctionCallContent)
                If functionCall IsNot Nothing Then
                    sb.Append(functionCall.Name)
                    If functionCall.Arguments IsNot Nothing Then
                        sb.Append(JsonSerializer.Serialize(functionCall.Arguments))
                    End If
                    Continue For
                End If

                Dim functionResult = TryCast(content, FunctionResultContent)
                If functionResult IsNot Nothing AndAlso functionResult.Result IsNot Nothing Then
                    sb.Append(JsonSerializer.Serialize(functionResult.Result))
                End If
            Next
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Computes a fresh ContextBreakdown snapshot right now - called
        ''' from MainWindow's context-usage-text hover handler. Async since
        ''' EstimateCommittedContextTokensAsync calls Lemonade's real
        ''' POST /v1/tokenize rather than guessing - IsContextBreakdownLoading
        ''' guards against overlapping calls if the mouse re-enters before a
        ''' previous refresh finished, same "if (loading) return" pattern
        ''' openlumara's own context popup uses for the same reason. The
        ''' composition (system prompt/tools/history) reflects what's
        ''' genuinely already in context; PendingTokens is a live preview of
        ''' what sending right now would ADD (see its own comment) - the
        ''' live turn-context/message-box cost is cached and only
        ''' recomputed when InputText actually changes, not on every hover,
        ''' since it involves real memory/knowledge embedding searches that
        ''' are wasted work when nothing's changed since the last one.
        ''' </summary>
        Public Async Function RefreshContextBreakdownAsync() As Task
            If IsContextBreakdownLoading Then Return
            IsContextBreakdownLoading = True
            Try
                Dim committed = Await EstimateCommittedContextTokensAsync(CancellationToken.None)
                Dim systemPromptTokens = committed.SystemPrompt
                Dim toolsTokens = committed.Tools
                Dim historyTokens = committed.History
                Dim toolsActiveCount = _moduleRegistry.GetEnabledTools().Count

                Dim percentFull = If(ContextUsageMax > 0, CInt(Math.Round(100.0 * ContextUsageCurrent / ContextUsageMax)), 0)

                ' Widths sized against ContextUsageMax directly (the real
                ' window size), NOT rescaled to force these three parts to
                ' sum exactly to ContextUsageCurrent - deliberately: even
                ' with the real tokenizer, this can't see chat-template
                ' rendering overhead (per-message role markers, the tool-
                ' calling JSON wrapper) that Lemonade's own real total does
                ' include, so a small honest gap between "sum of parts" and
                ' "the one true total" is expected and left as unlabeled
                ' free space rather than force-summed with another
                ' estimated number - see this class's own comment for the
                ' full history of why that went wrong twice already.
                Dim widthPerToken = If(ContextUsageMax > 0, ContextBreakdownBarWidth / ContextUsageMax, 0)
                Dim systemPromptWidth = Math.Min(systemPromptTokens * widthPerToken, ContextBreakdownBarWidth)
                Dim toolsWidth = Math.Min(toolsTokens * widthPerToken, ContextBreakdownBarWidth - systemPromptWidth)
                Dim historyWidth = Math.Min(historyTokens * widthPerToken, ContextBreakdownBarWidth - systemPromptWidth - toolsWidth)
                Dim freeWidth = Math.Max(ContextBreakdownBarWidth - systemPromptWidth - toolsWidth - historyWidth, 0)

                ' PendingTokens: the real, tokenized cost of what would
                ' actually be added on top of the total above if Send were
                ' hit right now - the live turn-context preview (time-
                ' awareness/memory/knowledge for the message box's CURRENT
                ' text, the exact same computation OpenTurnContextAsync's
                ' own dialog uses, not stale leftover data from the last
                ' send) plus the message box's own text. An addition on top
                ' of the one real total, never a second competing total of
                ' its own. Cached against the exact InputText it was
                ' computed for (see _lastPendingPreviewInputText's own
                ' comment) - re-running the real memory/knowledge embedding
                ' searches on every hover regardless of whether anything
                ' changed was confirmed live as real, wasted load on
                ' Lemonade, visible in its own logs as repeated tiny
                ' embedding completions.
                Dim pendingTokens As Integer
                If InputText = _lastPendingPreviewInputText AndAlso SelectedKnowledgeBaseId = _lastPendingPreviewKnowledgeBaseId Then
                    pendingTokens = _lastPendingPreviewTokens
                Else
                    Dim turnContextText = Await BuildLiveTurnContextTextAsync(InputText, CancellationToken.None)
                    Dim pendingText = If(String.IsNullOrEmpty(turnContextText), "", turnContextText & Environment.NewLine) & InputText
                    pendingTokens = If(String.IsNullOrWhiteSpace(pendingText), 0, Await TryTokenizeAsync(pendingText, CancellationToken.None))
                    _lastPendingPreviewInputText = InputText
                    _lastPendingPreviewKnowledgeBaseId = SelectedKnowledgeBaseId
                    _lastPendingPreviewTokens = pendingTokens
                End If

                ContextBreakdownInfo = New ContextBreakdown With {
                    .ContextNowTokens = ContextUsageCurrent,
                    .PercentFull = percentFull,
                    .UsedRealTokenizer = committed.UsedRealTokenizer,
                    .SystemPromptTokens = systemPromptTokens,
                    .SystemPromptWidth = systemPromptWidth,
                    .ToolsActiveCount = toolsActiveCount,
                    .ToolsTokens = toolsTokens,
                    .ToolsWidth = toolsWidth,
                    .HistoryTokens = historyTokens,
                    .HistoryWidth = historyWidth,
                    .FreeWidth = freeWidth,
                    .CompactionThresholdLeft = ContextBreakdownBarWidth * (_memorySettings.CompactionTriggerPercent / 100.0),
                    .CompactionCount = _compactionCount,
                    .HasCompactions = _compactionCount > 0,
                    .PendingTokens = pendingTokens,
                    .WouldExceedLimit = ContextUsageMax > 0 AndAlso ContextUsageCurrent + pendingTokens >= ContextUsageMax
                }
            Finally
                IsContextBreakdownLoading = False
            End Try
        End Function

        ''' <summary>
        ''' Opens a native file picker and extracts text (or, for an image,
        ''' resizes/saves a local copy) immediately, not deferred to Send, so
        ''' a corrupt/unsupported file fails right when picked, with the
        ''' failure reason visible, rather than silently after a question
        ''' about it has already been typed and sent. A document and an
        ''' image are mutually exclusive - the app has a single attachment
        ''' slot that accepts either kind, not two parallel slots.
        ''' </summary>
        Private Sub AttachFile()
            Dim dialog As New Microsoft.Win32.OpenFileDialog With {
                .Title = "Attach a file to analyze in this chat",
                .Filter = "Supported files (*.pdf;*.docx;*.xlsx;*.txt;*.md;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files (*.*)|*.*"
            }
            If dialog.ShowDialog() <> True Then Return

            Try
                If _imageAttachmentService.IsSupported(dialog.FileName) Then
                    ' AttachedFileName stays Nothing here, not the picked
                    ' filename - the thumbnail (AttachedImagePath, bound in
                    ' MainWindow.xaml) already shows what's attached, so a
                    ' filename chip next to it would just be redundant.
                    _attachedFileText = Nothing
                    AttachedFileName = Nothing
                    AttachedImagePath = _imageAttachmentService.SaveResizedCopy(dialog.FileName)
                Else
                    AttachedImagePath = Nothing
                    _attachedFileText = FileTextExtractor.ExtractText(dialog.FileName)
                    AttachedFileName = IO.Path.GetFileName(dialog.FileName)
                End If
            Catch ex As Exception
                _attachedFileText = Nothing
                AttachedImagePath = Nothing
                AttachedFileName = Nothing

                ' Visible in the chat itself, not StatusText - the header
                ' status slot is easy to miss entirely, especially here where
                ' nothing else about sending a message has happened yet to
                ' draw the eye there. A real assistant-style bubble (not a
                ' SystemNotes chip) - SystemNotes
                ' is for a short note ALONGSIDE a real reply's own text (see
                ' its XAML), not a standalone message, and this has no reply
                ' to sit alongside. Not persisted - this is a pre-send UI
                ' failure, not something either party actually said, so
                ' there's nothing meaningful to restore on a later reload.
                Dim errorBubble As New ChatMessageViewModel(isUser:=False,
                    initialText:=$"⚠️ Couldn't attach {IO.Path.GetFileName(dialog.FileName)}: {ex.Message}")
                Messages.Add(errorBubble)
                SetDateDividerIfNeeded(errorBubble)
            End Try
        End Sub

        Private Sub RemoveAttachedFile()
            _attachedFileText = Nothing
            AttachedImagePath = Nothing
            AttachedFileName = Nothing
        End Sub

        Private Function CanStop() As Boolean
            Return _generationCts IsNot Nothing
        End Function

        Private Sub StopGeneration()
            _generationCts?.Cancel()
        End Sub

        Private Sub StartRename(session As ChatSessionSummary)
            If session Is Nothing Then Return
            session.TitleBeforeEdit = session.Title
            session.IsEditing = True
        End Sub

        ''' <summary>
        ''' Commits the edited title. Called both from the edit box's Enter
        ''' key binding and from MainWindow.xaml.vb's OnSessionsListLostFocus
        ''' (clicking away also commits) - safe to call twice in a row since
        ''' it's a no-op once IsEditing is already False.
        ''' </summary>
        Private Sub CommitRename(session As ChatSessionSummary)
            If session Is Nothing OrElse Not session.IsEditing Then Return

            session.IsEditing = False
            If String.IsNullOrWhiteSpace(session.Title) Then
                session.Title = session.TitleBeforeEdit
                Return
            End If

            _sessionRepository.RenameSession(session.Id, session.Title)
        End Sub

        Private Sub CancelRename(session As ChatSessionSummary)
            If session Is Nothing Then Return
            session.Title = session.TitleBeforeEdit
            session.IsEditing = False
        End Sub

        ''' <summary>
        ''' Confirms with the user (a destructive, unrecoverable action - see
        ''' MainWindow.xaml's trash icon), then deletes the session from the
        ''' database and the sidebar. If the deleted session was the one
        ''' currently open, falls back to the next session in the list, or a
        ''' brand-new chat if that was the last one.
        ''' </summary>
        Private Sub DeleteSession(session As ChatSessionSummary)
            If session Is Nothing Then Return

            Dim confirmed = ConfirmDialog.Show(
                $"Delete ""{session.Title}""? This can't be undone.",
                "Delete chat",
                confirmText:="Delete",
                isDestructive:=True)
            If Not confirmed Then Return

            _sessionRepository.DeleteSession(session.Id)
            Dim wasCurrentSession = session.Id = _currentSessionId

            ' Look up by Id, not Sessions.Remove(session) directly - the
            ' confirmation dialog above is a native modal, and when it
            ' closes, MainWindow re-fires its own Activated event
            ' (MainWindow.xaml.vb), which calls RefreshSessions() and
            ' rebuilds Sessions with brand-new ChatSessionSummary objects
            ' from the database, all on the same call stack before this
            ' method resumes. ChatSessionSummary has no Equals override, so
            ' Sessions.Remove(session) against the original (now-stale)
            ' object reference would silently do nothing even though the row
            ' really was deleted.
            Dim currentRow = Sessions.FirstOrDefault(Function(s) s.Id = session.Id)
            If currentRow IsNot Nothing Then Sessions.Remove(currentRow)

            If wasCurrentSession Then
                If Sessions.Count > 0 Then
                    SelectedSession = Sessions(0)
                Else
                    NewChat()
                End If
            End If

            RebuildSidebarItems()
        End Sub

        ''' <summary>
        ''' StatusText is otherwise a persistent-until-overwritten field
        ''' (connectivity warnings, "Loading {model}...") which is correct
        ''' for those, but a one-off confirmation like "Exported to ..." has
        ''' no natural next event to overwrite it, so it needs its own
        ''' flash-then-auto-clear timer - same pattern as
        ''' SettingsViewModel.ShowStatusTextTemporarily.
        ''' </summary>
        Private _statusTextClearTimer As DispatcherTimer

        Private Sub ShowStatusTextTemporarily(text As String)
            StatusText = text

            _statusTextClearTimer?.Stop()
            _statusTextClearTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(3)}
            AddHandler _statusTextClearTimer.Tick,
                Sub(sender, e)
                    StatusText = ""
                    _statusTextClearTimer.Stop()
                End Sub
            _statusTextClearTimer.Start()
        End Sub

        ''' <summary>MoveToFolderDialog handles both picking an existing folder and creating a new one; this just applies the result and refreshes (the sidebar is grouped by folder, so a move needs a full rebuild, not just an in-place property update - same "just refresh the whole list" pattern used everywhere else in this app).</summary>
        Private Sub MoveToFolder(session As ChatSessionSummary)
            If session Is Nothing Then Return

            Dim folders = _sessionRepository.ListFolders()
            Dim result = MoveToFolderDialog.Show(folders, session.FolderId, _sessionRepository)
            If Not result.Confirmed Then Return

            _sessionRepository.SetSessionFolder(session.Id, result.FolderId)
            RefreshSessions()
        End Sub

        ''' <summary>TagManagerDialog writes each add/remove immediately, so this just refreshes afterward to pick up the new tag chips.</summary>
        Private Sub ManageTags(session As ChatSessionSummary)
            If session Is Nothing Then Return

            TagManagerDialog.Show(session.Id, session.Tags, _sessionRepository)
            RefreshSessions()
        End Sub

        ''' <summary>Reads straight from the DB (LoadMessages), not the live Messages collection, so this works for any session in the sidebar, not just whichever one happens to be currently open.</summary>
        Private Sub ExportChatToMarkdown(session As ChatSessionSummary)
            If session Is Nothing Then Return

            Dim sanitizedTitle = String.Join("_", session.Title.Split(IO.Path.GetInvalidFileNameChars()))
            Dim dialog As New Microsoft.Win32.SaveFileDialog With {
                .Title = "Export chat to Markdown",
                .FileName = $"{sanitizedTitle}.md",
                .Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*"
            }
            If dialog.ShowDialog() <> True Then Return

            Try
                Dim messages = _sessionRepository.LoadMessages(session.Id)
                Dim markdown = ChatMarkdownExporter.BuildMarkdown(session.Title, messages)
                IO.File.WriteAllText(dialog.FileName, markdown)
                ShowStatusTextTemporarily($"Exported to {IO.Path.GetFileName(dialog.FileName)}.")
            Catch ex As Exception
                ShowStatusTextTemporarily($"Couldn't export chat: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Loads the downloaded-model list, finds out which one Lemonade
        ''' currently has loaded, and populates the sessions sidebar with a
        ''' fresh new chat selected. Called once from MainWindow's Loaded event.
        ''' </summary>
        Public Async Function InitializeAsync(cancellationToken As CancellationToken) As Task
            RefreshModulesDisplay()

            Try
                Await RefreshAvailableModelsAsync(cancellationToken)

                Dim health = Await _managementClient.GetHealthAsync(cancellationToken)
                IsHealthy = health.Status = "ok"
                If Not String.IsNullOrEmpty(health.ModelLoaded) Then
                    _currentlyLoadedModel = health.ModelLoaded

                    ' Real bug found live: health.ModelLoaded is genuinely
                    ' whatever Lemonade last loaded into its one model slot -
                    ' which could be the embedding model, not a chat model
                    ' at all, if a background call (memory fact extraction,
                    ' RAG indexing) happened to be the last thing to touch
                    ' Lemonade before the app closed, evicting the chat
                    ' model. Accurate to Lemonade's real state, but never
                    ' what the user wants the main chat dropdown to default
                    ' to. Only trust health.ModelLoaded here if it's
                    ' actually chat-capable; otherwise fall back to the
                    ' configured default chat model.
                    Dim loadedModelCategory = _modelCategories.GetValueOrDefault(health.ModelLoaded)
                    If loadedModelCategory = ModelSelectorItem.ChatCategory Then
                        ' Bypass the Set block above (SetProperty directly, not the
                        ' SelectedModel property) so this doesn't trigger a pointless
                        ' reload of the model that's already loaded.
                        SetProperty(_selectedModel, health.ModelLoaded, NameOf(SelectedModel))
                        ' This bypass skips SwitchModelAsync entirely (nothing
                        ' to load, it's already loaded) - so it also skips
                        ' SwitchModelAsync's own real-ctx_size correction. Same
                        ' fix applied here directly (see
                        ' ApplyRealLoadedContextWindow's own comment).
                        ApplyRealLoadedContextWindow(health.ModelLoaded, health.AllModelsLoaded)
                        SendCommand.NotifyCanExecuteChanged()
                        OnPropertyChanged(NameOf(IsModelDetailsAvailable))
                    ElseIf AvailableModels.Any(Function(m) m.Id = _lemonadeSettings.ChatModel) Then
                        ' Deliberately through the NORMAL SelectedModel setter
                        ' this time (not the bypass above) - the configured
                        ' chat model genuinely isn't loaded right now, so this
                        ' should trigger a real SwitchModelAsync call to bring
                        ' it into memory, not just show a selection that
                        ' doesn't match reality until the first message.
                        SelectedModel = _lemonadeSettings.ChatModel
                    End If
                    ' This bypasses SelectedModel's own setter (see the
                    ' comment above), so CanSend's dependency on SelectedModel
                    ' needs its own explicit notification here too - a real
                    ' message could otherwise be sent before this ever ran
                    ' (send button not yet correctly disabled while
                    ' InitializeAsync is still in flight), which is the exact
                    ' bug CanSend's SelectedModel check was added to prevent.
                    SendCommand.NotifyCanExecuteChanged()
                End If
            Catch ex As Exception
                IsHealthy = False
                LastHealthCheckError = $"Couldn't reach Lemonade: {ex.Message}"
            End Try

            AvailableKnowledgeBases.Clear()
            AvailableKnowledgeBases.Add(New KnowledgeBaseSummary With {.Id = Nothing, .Name = "(No knowledge base)"})
            For Each knowledgeBase In _knowledgeRepository.ListKnowledgeBases()
                AvailableKnowledgeBases.Add(knowledgeBase)
            Next

            ' Resumes the most recently active session on launch, only
            ' starting a brand-new chat if there's truly nothing to resume -
            ' same logic DeleteSession already uses when its own current
            ' session disappears.
            RefreshSessions()
            If Sessions.Count > 0 Then
                SelectedSession = Sessions(0)
            Else
                NewChat()
            End If
        End Function

        ''' <summary>
        ''' Public so MainWindow can also call it on window activation (see
        ''' its comment) - if the Image generation module is disabled in
        ''' Settings, image models should stop appearing in this dropdown
        ''' without needing a restart, since leaving them selectable would
        ''' reintroduce the "picked an image model as the main chat model"
        ''' confusion this grouped dropdown avoids. _modelContextWindows/
        ''' _modelCategories are still populated for EVERY model regardless
        ''' of visibility, not just the shown ones - SendAsync's category
        ''' routing needs to stay correct even for a model that's currently
        ''' hidden from the dropdown (e.g. one still selected from before
        ''' the module was disabled).
        ''' </summary>
        ''' <summary>
        ''' Try/Catch internal to this method, not left to callers - called
        ''' as an Async Sub fire-and-forget from MainWindow's Activated event
        ''' (a plain "Async Sub" can't be awaited/caught by its caller, so an
        ''' unhandled exception there would crash the app on the UI thread
        ''' every time the window regains focus while Lemonade happens to be
        ''' briefly unreachable) - this needs to be safe to call standalone.
        ''' </summary>
        Public Async Function RefreshAvailableModelsAsync(cancellationToken As CancellationToken) As Task
            Try
                Dim models = Await _managementClient.ListDownloadedModelsAsync(cancellationToken)

                ' Reaching this line means the request just succeeded, so
                ' Lemonade is genuinely reachable - IsHealthy is set back to
                ' True here, not just set False in the Catch below, so a
                ' brief outage doesn't leave the health indicator
                ' permanently red once the server recovers.
                IsHealthy = True
                LastHealthCheckError = Nothing

                Dim imageGenEnabled = _moduleRegistry.AllModules.OfType(Of ImageGen.ImageGenModule)().Any(Function(m) m.IsEnabled)

                ' Captured before Clear() below - the model ComboBox's
                ' SelectedValue binding is two-way, so clearing AvailableModels
                ' (its ItemsSource) pushes Nothing straight back into
                ' SelectedModel via the binding, synchronously, before this
                ' function resumes. This method runs on every window
                ' activation (MainWindow.xaml.vb's OnWindowActivated), so a
                ' subsequent SetContextUsage call against a nulled
                ' SelectedModel would crash on
                ' _modelContextWindows.GetValueOrDefault(Nothing, ...) -
                ' Dictionary throws ArgumentNullException on a null key.
                Dim previouslySelectedModel = SelectedModel

                ' Fetched BEFORE touching _modelContextWindows, and folded
                ' into the SAME population pass below - not a separate
                ' "reset everything to catalog defaults, then hope to fix
                ' the current model" step. That two-step version had a real
                ' gap, confirmed live: if this health call happened to
                ' fail/time out (e.g. Lemonade still settling right after
                ' generating a reply), the wrong catalog max was already in
                ' place with nothing left to correct it until some later
                ' refresh happened to succeed - sometimes not for a long
                ' time. Best-effort: Nothing here just means every model
                ' falls back to whatever it already had, below.
                Dim health As LemonadeHealthInfo = Nothing
                Try
                    health = Await _managementClient.GetHealthAsync(cancellationToken)
                Catch
                End Try

                AvailableModels.Clear()
                _modelCategories.Clear()
                _modelVisionSupport.Clear()
                ' Chat models first, then Image, then Embedding, then
                ' anything else - PropertyGroupDescription (MainWindow.xaml)
                ' groups in first-encountered order, not alphabetically, so
                ' the sort here is what actually controls group order in the
                ' dropdown.
                For Each model In models.OrderBy(Function(m) ModelSelectorItem.FromLabels(m.Id, m.Labels).CategorySortOrder)
                    Dim item = ModelSelectorItem.FromLabels(model.Id, model.Labels)
                    If item.Category <> ModelSelectorItem.ImageCategory OrElse imageGenEnabled Then
                        AvailableModels.Add(item)
                    End If

                    ' Prefer the real loaded ctx_size (this refresh's own
                    ' health snapshot) over the catalog's static
                    ' max_context_window whenever it's actually known - see
                    ' ApplyRealLoadedContextWindow's own comment for why.
                    ' _modelContextWindows is deliberately NOT Clear()'d
                    ' before this loop (see above) - a model this refresh
                    ' has no real data for this time keeps whatever it
                    ' already had (quite possibly an already-correct real
                    ' reading from a moment ago) rather than being blindly
                    ' downgraded to the catalog max; only a genuinely
                    ' never-seen-before model falls back to that.
                    Dim realCtxSize = health?.AllModelsLoaded?.FirstOrDefault(Function(m) m.ModelName = model.Id)?.RecipeOptions?.CtxSize
                    If realCtxSize.HasValue AndAlso realCtxSize.Value > 0 Then
                        _modelContextWindows(model.Id) = realCtxSize.Value
                    ElseIf Not _modelContextWindows.ContainsKey(model.Id) Then
                        _modelContextWindows(model.Id) = model.MaxContextWindow
                    End If

                    _modelCategories(model.Id) = item.Category
                    _modelVisionSupport(model.Id) = model.Labels IsNot Nothing AndAlso
                        model.Labels.Contains("vision", StringComparer.OrdinalIgnoreCase)
                Next

                ' Restore the selection the Clear() above wiped out, same
                ' bypass-the-setter pattern InitializeAsync already uses
                ' below (NameOf(SelectedModel)) - going through the normal
                ' setter here is unnecessary since nothing actually changed
                ' about which model is loaded.
                If AvailableModels.Any(Function(m) m.Id = previouslySelectedModel) Then
                    SetProperty(_selectedModel, previouslySelectedModel, NameOf(SelectedModel))
                    SendCommand?.NotifyCanExecuteChanged()
                    OnPropertyChanged(NameOf(AttachedImageVisionWarning))
                    OnPropertyChanged(NameOf(IsModelDetailsAvailable))
                End If

                ' Immediate feedback for the correction above, rather than
                ' waiting for the next completed turn's stats to refresh it -
                ' SetContextUsage isn't used here since ContextUsageCurrent
                ' shouldn't reset just from a routine refresh.
                ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0)
                ContextUsageText = FormatContextUsageText(ContextUsageCurrent, ContextUsageMax)
            Catch ex As Exception
                IsHealthy = False
                LastHealthCheckError = $"Couldn't reach Lemonade: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Public so MainWindow can call it on window activation (see its
        ''' comment) - picks up sessions a scheduled job created while the
        ''' app was in the background, without anything needing to be done
        ''' manually. Re-selects whatever was already open by Id after
        ''' rebuilding the list, guarded so it doesn't re-trigger LoadSession
        ''' for the same session - a blind Sessions.Clear()/rebuild would
        ''' otherwise visually deselect the currently-open chat in the
        ''' sidebar every time this runs, which is confusing regardless of
        ''' whether anything actually changed.
        ''' </summary>
        Public Sub RefreshSessions()
            Dim results = _sessionRepository.SearchSessions(SearchText)
            Dim previouslySelectedId = _currentSessionId
            Sessions.Clear()
            For Each session In results
                Sessions.Add(session)
            Next

            Dim stillSelected = Sessions.FirstOrDefault(Function(s) s.Id = previouslySelectedId)
            If stillSelected IsNot Nothing Then
                _isLoadingSession = True
                Try
                    SelectedSession = stillSelected
                Finally
                    _isLoadingSession = False
                End Try
            End If

            RebuildSidebarItems()
        End Sub

        ''' <summary>
        ''' Sessions itself (the authoritative, already-sorted list
        ''' everywhere else in this class reads/mutates) is left untouched;
        ''' this builds a separate, sidebar-display-only projection that
        ''' interleaves a FolderHeaderSummary row before each folder's
        ''' sessions, omitting a collapsed folder's sessions entirely. The
        ''' "Unfiled" bucket gets a header too (for visual consistency with
        ''' the grouping the sidebar already had), but it's never
        ''' collapsible - it has no real Folders row to persist a collapsed
        ''' state against.
        ''' </summary>
        Private Sub RebuildSidebarItems()
            Dim folders = _sessionRepository.ListFolders().ToDictionary(Function(f) f.Id)

            SidebarItems.Clear()
            Dim isFirstItem = True
            Dim currentFolderId As String = Nothing
            For Each session In Sessions
                If isFirstItem OrElse Not String.Equals(session.FolderId, currentFolderId, StringComparison.Ordinal) Then
                    currentFolderId = session.FolderId
                    Dim header As FolderHeaderSummary
                    If currentFolderId Is Nothing Then
                        header = New FolderHeaderSummary With {.FolderId = Nothing, .Name = "Unfiled", .IsCollapsed = False, .IsCollapsible = False}
                    Else
                        Dim isCollapsed = folders.GetValueOrDefault(currentFolderId)?.IsCollapsed = True
                        header = New FolderHeaderSummary With {.FolderId = currentFolderId, .Name = session.FolderName, .IsCollapsed = isCollapsed}
                    End If
                    header.ToggleExpandedCommand = New RelayCommand(Sub() ToggleFolderExpanded(header))
                    SidebarItems.Add(header)
                End If
                isFirstItem = False

                Dim folderIsCollapsed = currentFolderId IsNot Nothing AndAlso folders.GetValueOrDefault(currentFolderId)?.IsCollapsed = True
                If Not folderIsCollapsed Then SidebarItems.Add(session)
            Next

            ' Keeps the ListBox's own highlighted row correct after a
            ' rebuild - Nothing (no visible selection) if the real selected
            ' session's folder is currently collapsed, which is the same
            ' "a collapsed group hides its children, including a selected
            ' one" behavior a tree view already has.
            SelectedSidebarItem = SidebarItems.OfType(Of ChatSessionSummary)().FirstOrDefault(Function(s) s.Id = _currentSessionId)
        End Sub

        ''' <summary>Click handler for a folder header row's own ToggleExpandedCommand - persists immediately, then a full RebuildSidebarItems (via RefreshSessions) picks up the new state.</summary>
        Private Sub ToggleFolderExpanded(header As FolderHeaderSummary)
            If Not header.IsCollapsible Then Return

            _sessionRepository.SetFolderCollapsed(header.FolderId, Not header.IsCollapsed)
            RefreshSessions()
        End Sub

        ''' <summary>Starts a brand-new, empty session and switches to it - like clicking "New chat".</summary>
        Private Sub NewChat()
            _currentSessionId = _sessionRepository.CreateSession("New chat")

            ' RefreshSessions() (full rebuild + restore-by-id), not a direct
            ' Sessions.Insert(0, ...) - same "just refresh the whole list"
            ' pattern DeleteSession/MoveToFolder/ManageTags already use for
            ' the same reason (see MoveToFolder's comment). Inserting
            ' straight into Sessions would leave the grouped, virtualized
            ' sidebar out of sync with the group order the CollectionView
            ' expects.
            RefreshSessions()

            _history.Clear()
            ' _conversationSummaryText cleared BEFORE RefreshStableSystemPrompt
            ' - a brand-new chat must not inherit whatever chat was open
            ' before it, since BuildSystemPromptText reads this field.
            _conversationSummaryText = ""
            _compactionCount = 0
            RefreshStableSystemPrompt()
            Messages.Clear()
            ' Cleared here rather than only when a message finishes sending,
            ' so it doesn't keep showing the *previous* chat's last-turn
            ' tool calls - visually, that would look like it belonged to
            ' whatever chat is currently open.
            CurrentTurnToolCalls.Clear()
            ResetSessionStats()
            SetContextUsage(0)
            ' The "(No knowledge base)" sentinel object itself, not a bare
            ' Nothing - the ComboBox needs a real SelectedItem to display
            ' that label; a null SelectedItem would just show empty.
            SelectedKnowledgeBase = AvailableKnowledgeBases.FirstOrDefault(Function(kb) kb.Id Is Nothing)
        End Sub

        ''' <summary>
        ''' "Today"/"Yesterday"/a full date, or Nothing if the
        ''' previous message was the same calendar day (no divider needed).
        ''' Shared logic for both a live-session bubble (SendAsync/
        ''' StreamOneReplyAsync) and a reloaded one (LoadSession).
        ''' </summary>
        Private Shared Function ComputeDateDividerText(previousDate As Date?, currentCreatedAt As DateTime) As String
            Dim currentDate = currentCreatedAt.Date
            If previousDate.HasValue AndAlso previousDate.Value = currentDate Then Return Nothing

            If currentDate = Date.Today Then Return "Today"
            If currentDate = Date.Today.AddDays(-1) Then Return "Yesterday"
            Return currentCreatedAt.ToString("dddd, d MMMM yyyy")
        End Function

        ''' <summary>Call right after Messages.Add(bubble) - compares against whatever is now the second-to-last entry.</summary>
        Private Sub SetDateDividerIfNeeded(bubble As ChatMessageViewModel)
            Dim previousDate As Date? = If(Messages.Count > 1, Messages(Messages.Count - 2).CreatedAt.Date, CType(Nothing, Date?))
            bubble.DateDividerText = ComputeDateDividerText(previousDate, bubble.CreatedAt)
        End Sub

        ''' <summary>
        ''' Zeroes the running "this session" token totals - shared by
        ''' NewChat and LoadSession since both start a fresh view of a
        ''' session's stats (there's no historical per-turn input/output
        ''' breakdown to restore, since only the combined context-token count
        ''' is persisted - see SetContextUsage).
        ''' </summary>
        Private Sub ResetSessionStats()
            _sessionTotalInputTokens = 0
            _sessionTotalOutputTokens = 0
            SessionTotalTokensText = "0 / 0"
        End Sub

        ''' <summary>
        ''' Sets the context-usage bar to a specific, known value - 0 for a
        ''' brand-new empty chat (NewChat), or that session's real last-known
        ''' figure when switching back to an existing one (LoadSession),
        ''' since switching to an existing chat doesn't clear its history
        ''' and the next message sent there still starts from that chat's
        ''' real context size, not an empty one. Max is looked up fresh from
        ''' the selected model rather than
        ''' trusting the existing ContextUsageMax field, since that field is
        ''' otherwise only ever set after a turn completes.
        ''' </summary>
        Private Sub SetContextUsage(contextTokens As Integer)
            ContextUsageCurrent = contextTokens
            ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0)
            ContextUsageText = FormatContextUsageText(ContextUsageCurrent, ContextUsageMax)
        End Sub

        ''' <summary>
        ''' Switches the chat window to an existing session's history.
        ''' forceReload is for ReloadCurrentSessionIfMatching only - see its
        ''' own comment for why bypassing the guard below is safe there and
        ''' nowhere else.
        ''' </summary>
        Private Sub LoadSession(sessionId As String, Optional forceReload As Boolean = False)
            ' Loading the session you're ALREADY in is a no-op, not a full
            ' reset - confirmed live this matters, not just as a
            ' micro-optimization: LoadSession always rebuilds _history from
            ' the full PERSISTED message record, since compaction only ever
            ' trims the in-memory copy, never the database (see
            ' _conversationSummaryText's own comment below). Any spurious
            ' re-trigger of SelectedSession's setter for the session
            ' that's already open - whatever the exact cause - would
            ' otherwise silently discard everything compaction had just
            ' trimmed, undoing it completely. A conversation that hit 64+
            ' uncompacted messages despite compaction genuinely having run
            ' was traced to exactly this.
            If sessionId = _currentSessionId AndAlso Not forceReload Then Return

            _currentSessionId = sessionId
            _history.Clear()
            ' Not persisted (see _conversationSummaryText's comment) - a
            ' session reloaded from disk has no in-memory summary yet, only
            ' its full raw message history, which LoadMessages below restores
            ' in full (only _history, the live model-facing copy, ever gets
            ' compacted - the loaded transcript itself never is). Cleared
            ' BEFORE RefreshStableSystemPrompt, same reasoning as NewChat.
            _conversationSummaryText = ""
            _compactionCount = 0
            RefreshStableSystemPrompt()
            Messages.Clear()

            For Each stored In _sessionRepository.LoadMessages(sessionId)
                Select Case stored.Role
                    Case ChatRole.User.Value
                        ' Restore a real image, not just its text marker, if
                        ' the locally-saved copy is still there. Unlike a
                        ' document attachment (whose extracted text
                        ' is already fully IN stored.Content, so the existing
                        ' plain-text path below already carries it forward
                        ' correctly), an image has no text-extraction
                        ' equivalent - losing it here would mean losing it
                        ' for good, not just a cosmetic chip.
                        Dim parsedImage = ImageAttachmentService.TryParseAttachedImageMarker(stored.Content)
                        If parsedImage.ImagePath IsNot Nothing AndAlso File.Exists(parsedImage.ImagePath) Then
                            _history.Add(New ChatMessage(ChatRole.User, New List(Of AIContent) From {
                                New TextContent(stored.Content),
                                _imageAttachmentService.BuildDataContent(parsedImage.ImagePath)
                            }))
                            Dim imageBubble As New ChatMessageViewModel(isUser:=True, initialText:=parsedImage.RemainingText, createdAt:=stored.CreatedAt.ToLocalTime())
                            imageBubble.AttachedImagePath = parsedImage.ImagePath
                            Messages.Add(imageBubble)
                            SetDateDividerIfNeeded(imageBubble)
                        Else
                            _history.Add(New ChatMessage(ChatRole.User, stored.Content))
                            Dim userReloadBubble As New ChatMessageViewModel(isUser:=True, initialText:=stored.Content, createdAt:=stored.CreatedAt.ToLocalTime())
                            Messages.Add(userReloadBubble)
                            SetDateDividerIfNeeded(userReloadBubble)
                        End If
                    Case ChatRole.Assistant.Value
                        _history.Add(New ChatMessage(ChatRole.Assistant, stored.Content))
                        Dim bubble As New ChatMessageViewModel(isUser:=False, initialText:=stored.Content, createdAt:=stored.CreatedAt.ToLocalTime())
                        bubble.ReasoningText = stored.ReasoningContent
                        For Each toolCall In ToolCallResult.ParseFromStorage(stored.ToolCalls)
                            bubble.ToolCalls.Add(toolCall)
                        Next
                        Messages.Add(bubble)
                        SetDateDividerIfNeeded(bubble)
                End Select
            Next

            ' Restores this session's own last reply's tool calls, rather
            ' than leaving whatever the previously-open chat's last turn
            ' happened to show, or just going blank. Empty if the last
            ' assistant reply had none.
            CurrentTurnToolCalls.Clear()
            Dim lastAssistantBubble = Messages.LastOrDefault(Function(m) Not m.IsUser)
            If lastAssistantBubble IsNot Nothing Then
                For Each toolCall In lastAssistantBubble.ToolCalls
                    CurrentTurnToolCalls.Add(toolCall)
                Next
            End If

            ResetSessionStats()
            SetContextUsage(_sessionRepository.GetLastContextTokens(sessionId).GetValueOrDefault(0))
            ' Falls back to the "(No knowledge base)" sentinel if the
            ' attached id doesn't match anything currently loaded - e.g. the
            ' knowledge base was deleted since this session last attached it.
            Dim attachedKnowledgeBaseId = _sessionRepository.GetAttachedKnowledgeBase(sessionId)
            SelectedKnowledgeBase = If(
                AvailableKnowledgeBases.FirstOrDefault(Function(kb) kb.Id = attachedKnowledgeBaseId),
                AvailableKnowledgeBases.FirstOrDefault(Function(kb) kb.Id Is Nothing))
        End Sub

        ''' <summary>
        ''' Called from the SchedulerNotifier.SessionUpdated handler - forces
        ''' LoadSession to actually re-read from disk despite its own
        ''' same-session guard (which exists specifically to stop a spurious
        ''' reselect from discarding compaction's in-memory trim - see
        ''' LoadSession's comment). Safe here because sessionId only ever
        ''' equals _currentSessionId in this call chain if the user has
        ''' navigated into that specific scheduled job's own fresh session
        ''' (ScheduledJobRunner always creates a brand-new session per run,
        ''' never writes into an existing open chat) and is watching it live -
        ''' there's no in-memory compaction state on a session that young to
        ''' lose. A no-op for every other session (the overwhelmingly common
        ''' case - most job runs happen with a different chat, or no chat,
        ''' open).
        ''' </summary>
        Public Sub ReloadCurrentSessionIfMatching(sessionId As String)
            If sessionId <> _currentSessionId Then Return
            LoadSession(sessionId, forceReload:=True)
        End Sub

        ' Generous - a genuinely large local model can legitimately take
        ' minutes to load - but still bounded, same "never leave a
        ' background call truly uncancellable" reasoning as
        ' BackgroundModelCallTimeout, just a longer allowance for the
        ' different kind of work.
        Private Const ModelLoadTimeout = 300 ' seconds

        Private Async Sub SwitchModelFireAndForget(modelName As String)
            Using timeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(ModelLoadTimeout))
                Await SwitchModelAsync(modelName, timeoutCts.Token)
            End Using
        End Sub

        ''' <summary>
        ''' This call (and CompactHistoryIfNeededAsync's summarization call)
        ''' needs its own bounded timeout, not CancellationToken.None - if
        ''' the model gets stuck in a generation loop that never emits a
        ''' stop token (a real local-model failure mode), there would
        ''' otherwise be no way to cancel it: not the Stop button (this
        ''' isn't the visible reply), not closing the app (fire-and-forget
        ''' Subs aren't tracked or cancelled on shutdown). A bounded timeout
        ''' self-limits regardless of whether the app is still running to
        ''' cancel it, and .NET's HttpClient aborting the request on timeout
        ''' closes the connection, which Lemonade/llama.cpp notices and
        ''' stops generating for.
        ''' </summary>
        Private Const BackgroundModelCallTimeout = 60 ' seconds

        Private Async Sub ExtractFactsFireAndForget(userText As String, assistantReply As String)
            Using timeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(BackgroundModelCallTimeout))
                Await _memoryService.ExtractAndSaveFactsAsync(userText, assistantReply, timeoutCts.Token)
            End Using
        End Sub

        ''' <summary>
        ''' Short-term memory/compaction (see this class's _memorySettings
        ''' comment for the design rationale). Only _history (what actually
        ''' gets sent to the model) is ever trimmed - Messages (the visible
        ''' chat transcript) and the SQLite-persisted messages are untouched,
        ''' so the user still sees and can scroll their full conversation;
        ''' only the model's own context gets compacted.
        ''' </summary>
        Private Async Function CompactHistoryIfNeededAsync(assistantBubble As ChatMessageViewModel, cancellationToken As CancellationToken) As Task
            If ContextUsageMax <= 0 Then Return

            ' A FRESH, real measurement of what's actually in _history right
            ' now - not ContextUsageCurrent, which only updates after a
            ' SUCCESSFULLY COMPLETED turn (see its own comment). Confirmed
            ' live this was a real, serious bug: a run of failed sends
            ' (blank replies, context-exceeded errors) left ContextUsageCurrent
            ' stuck at a stale, too-low reading for a long stretch, during
            ' which this trigger kept comparing against that stale number
            ' and never fired - _history grew unchecked to 64 messages
            ' (~38,000 characters) before compaction finally caught up once
            ' a turn happened to succeed again. Checking the real current
            ' size directly, every time, closes that gap regardless of how
            ' many previous turns failed.
            Dim committed = Await EstimateCommittedContextTokensAsync(cancellationToken)
            Dim currentTokens = committed.SystemPrompt + committed.Tools + committed.History
            Dim compactionTriggerFraction = _memorySettings.CompactionTriggerPercent / 100.0
            If currentTokens / ContextUsageMax < compactionTriggerFraction Then Return

            ' _history(0) is always the system message - everything else is
            ' real conversation. Nothing to usefully compact yet if there
            ' isn't more than the "keep verbatim" tail.
            Dim compactibleCount = _history.Count - 1 - MessagesToKeepAfterCompaction
            If compactibleCount <= 0 Then Return

            ' Shown BEFORE the summarization call below, which is itself a
            ' full model round-trip and can take a while - confirmed live
            ' this was a real gap: with no note yet (the "Compacted..." one
            ' only gets added once it's done) and streaming already finished,
            ' the user was left looking at just the red stop button with no
            ' sign anything was happening. Removed again in both the success
            ' and failure paths below, never left showing once this
            ' function returns.
            Dim compactingNote = "Compacting older messages to save context space..."
            assistantBubble.SystemNotes.Add(compactingNote)

            Try
                Dim itemsToSummarize As New List(Of ChatMessage)
                If Not String.IsNullOrEmpty(_conversationSummaryText) Then
                    ' Rolls the existing summary into the new one (rather than
                    ' being overwritten by it) so a second compaction pass
                    ' later in a long chat doesn't lose what the first pass
                    ' already captured.
                    itemsToSummarize.Add(New ChatMessage(ChatRole.System, $"Summary of even earlier conversation: {_conversationSummaryText}"))
                End If
                itemsToSummarize.AddRange(_history.Skip(1).Take(compactibleCount))

                _conversationSummaryText = Await _compactionService.SummarizeAsync(itemsToSummarize, cancellationToken)
                _compactionCount += 1

                Dim keptTail = _history.Skip(_history.Count - MessagesToKeepAfterCompaction).ToList()
                _history.Clear()
                RefreshStableSystemPrompt() ' _conversationSummaryText was already updated above, so this correctly includes the new summary
                _history.AddRange(keptTail)

                ' ContextUsageCurrent otherwise stays at its last REAL
                ' Lemonade-reported figure (from the reply just before this)
                ' until the *next* turn's own stats come back, which would
                ' read as "compaction did nothing" since the number doesn't
                ' move. Re-estimated the same reliable way as everywhere
                ' else (EstimateCommittedContextTokensAsync, real tokenizer
                ' when available) - confirmed live that an earlier version
                ' of this used a cruder local estimate that only summed
                ' _history's own text and completely omitted tools
                ' (consistently ~10,000+ real tokens on every request),
                ' making ContextUsageCurrent wildly, systematically too low
                ' right after every compaction. That false-small baseline
                ' then made the very next real turn look like an alarming,
                ' unexplained jump, when the context had actually been that
                ' large the whole time - this was the real, unifying cause
                ' behind several "why did this jump by thousands of tokens"
                ' reports. Marked with "~" rather than presented as another
                ' real Lemonade number, and isn't persisted via
                ' UpdateLastContextTokens - the real figure from the next
                ' actual completed turn overwrites it as usual.
                Dim recomputed = Await EstimateCommittedContextTokensAsync(cancellationToken)
                Dim estimatedTokens = recomputed.SystemPrompt + recomputed.Tools + recomputed.History
                ContextUsageCurrent = estimatedTokens
                ContextUsageText = FormatContextUsageText(estimatedTokens, ContextUsageMax, isEstimate:=True)

                ' Visible in the chat itself (see SystemNotes) rather than
                ' the header's StatusText - that slot is too narrow for a
                ' sentence like this (it sits between the model dropdown and
                ' health dot), and doing this with no visible indicator at
                ' all would leave no way to tell it had happened. The saved
                ' figure is currentTokens (the fresh, real pre-compaction
                ' measurement taken above, before the trigger check) minus
                ' this same post-compaction estimate - both computed the
                ' same way (EstimateCommittedContextTokensAsync), so the
                ' difference is a real, comparable saving rather than two
                ' numbers from different methods.
                Dim tokensSaved = Math.Max(0, currentTokens - estimatedTokens)
                assistantBubble.SystemNotes.Remove(compactingNote)
                assistantBubble.SystemNotes.Add($"Compacted older messages to save context space · saved ~{tokensSaved:N0} tokens")
            Catch
                ' Best-effort, same as fact extraction - a failed compaction
                ' attempt just means the next turn's request stays as large
                ' as it already was, not a crash or a lost reply.
                assistantBubble.SystemNotes.Remove(compactingNote)
            End Try
        End Function

        ''' <summary>Rough ~4-characters-per-token English approximation - only ever used as a stand-in until a real Lemonade-reported number is available (see CompactHistoryIfNeededAsync).</summary>
        Private Shared Function EstimateTokenCount(text As String) As Integer
            If String.IsNullOrEmpty(text) Then Return 0
            Return Math.Max(1, text.Length \ 4)
        End Function

        ''' <summary>Real count via Lemonade's own tokenizer, falling back to the chars/4 guess if the call fails (older Lemonade version, transient network issue) - shared by every "view X (~N tokens)" dialog rather than duplicating the same Try/Catch three times.</summary>
        Private Async Function TryTokenizeAsync(text As String, cancellationToken As CancellationToken) As Task(Of Integer)
            Try
                Return Await _managementClient.TokenizeAsync(text, cancellationToken)
            Catch
                Return EstimateTokenCount(text)
            End Try
        End Function

        ''' <summary>
        ''' Builds the bottom bar's "X / Y tokens (Z%)" text - one shared
        ''' place so the percentage is computed/rounded the same way
        ''' everywhere ContextUsageCurrent/Max change, whether from a real
        ''' Lemonade stats update or one of the "~"-prefixed local estimates
        ''' (post-compaction, post-error/blank-reply).
        ''' </summary>
        Private Shared Function FormatContextUsageText(current As Integer, max As Integer, Optional isEstimate As Boolean = False) As String
            Dim prefix = If(isEstimate, "~", "")
            Dim percentText = If(max > 0, $" ({Math.Round(100.0 * current / max):0}%)", "")
            Return $"{prefix}{current:N0} / {max:N0} tokens{percentText}"
        End Function

        ''' <summary>
        ''' Persona (Identity/AboutUser/writing style) + base system prompt +
        ''' conversation summary + pinned facts only - deliberately NOT this
        ''' turn's relevant-memories/knowledge search
        ''' results (see AppendVolatileContext and RefreshStableSystemPrompt's
        ''' own comments for where those go instead and why). Everything here
        ''' is genuinely stable turn-to-turn (only changes when the summary
        ''' updates via compaction, or the user pins/unpins a memory).
        ''' llama.cpp's KV-cache reuse matches a common *prefix*, so a
        ''' system message (position 0) that changed every turn would mean
        ''' nothing could ever be cached, even though the actual
        ''' conversation history itself was unchanged - hence keeping
        ''' per-turn search results out of it entirely.
        ''' </summary>
        Private Function BuildSystemPromptText() As String
            Dim sections As New List(Of String)

            ' Persona: Identity/AboutUser come before the operating
            ' instructions (SystemPrompt) - "who am I / who are you talking
            ' to" reads naturally before "how should you behave". Both are
            ' omitted entirely when unset, same as every other section here,
            ' so an unconfigured Persona costs nothing in prompt size.
            If Not String.IsNullOrWhiteSpace(_personaSettings.Identity) Then
                sections.Add(_personaSettings.Identity)
            End If

            If Not String.IsNullOrWhiteSpace(_personaSettings.AboutUser) Then
                sections.Add("About the person you're talking to:" & Environment.NewLine & _personaSettings.AboutUser)
            End If

            sections.Add(_assistantSettings.SystemPrompt)

            Dim writingStyleText = BuildWritingStyleText()
            If Not String.IsNullOrEmpty(writingStyleText) Then sections.Add(writingStyleText)

            If Not String.IsNullOrEmpty(_conversationSummaryText) Then
                sections.Add("Summary of earlier parts of this conversation (older messages were trimmed to save context space):" & Environment.NewLine & _conversationSummaryText)
            End If

            Dim pinnedFactsText = _memoryService.GetPinnedFactsText()
            If Not String.IsNullOrEmpty(pinnedFactsText) Then sections.Add(pinnedFactsText)

            Return String.Join(Environment.NewLine & Environment.NewLine, sections)
        End Function

        ''' <summary>
        ''' Compiles Persona's structured writing-style settings into compact
        ''' bullet lines - cheap (a handful of tokens) but has an outsized
        ''' effect on how the reply actually reads. Returns Nothing (not an
        ''' empty string) when every
        ''' setting is still "Default" and no custom instructions are set, so
        ''' BuildSystemPromptText's IsNullOrEmpty check correctly omits this
        ''' section entirely for an unconfigured Persona.
        ''' </summary>
        Private Function BuildWritingStyleText() As String
            Dim persona = _personaSettings
            Dim lines As New List(Of String)

            If persona.Tone <> "Default" Then lines.Add($"Tone: {persona.Tone}")
            If persona.Verbosity <> "Default" Then lines.Add($"Verbosity: {persona.Verbosity}")
            If persona.EmojiUsage <> "Default" Then lines.Add($"Emoji use: {persona.EmojiUsage}")
            If Not String.IsNullOrWhiteSpace(persona.CustomInstructions) Then lines.Add(persona.CustomInstructions)

            If lines.Count = 0 Then Return Nothing

            Return "Writing style:" & Environment.NewLine & "- " & String.Join(Environment.NewLine & "- ", lines)
        End Function

        ''' <summary>
        ''' Deliberately built into the VOLATILE per-turn context (folded in
        ''' by AppendVolatileContext), never the stable system prompt - the
        ''' current time changes on literally every turn, so putting it in
        ''' _history(0) would defeat the point of keeping a stable prefix
        ''' llama.cpp can actually reuse. Always included (no Settings
        ''' toggle) - it's cheap, and
        ''' unlike Identity/AboutUser there's no meaningful "unset" state for
        ''' the current time.
        ''' </summary>
        ''' <summary>
        ''' Sets _history(0) to the current stable system prompt - safe to
        ''' call whenever _history is empty (NewChat/LoadSession/right after
        ''' CompactHistoryIfNeededAsync's Clear()) or already has a system
        ''' message at index 0 (every other call site). Deliberately called
        ''' at the start of every turn too (cheap - a local DB read of
        ''' pinned facts, no network calls), so a newly-pinned fact shows up
        ''' immediately rather than needing the app restarted - this doesn't
        ''' hurt prompt caching, since the *content* only actually changes
        ''' when pinned facts or the
        ''' summary really do, not on every single message.
        ''' </summary>
        Private Sub RefreshStableSystemPrompt()
            Dim systemMessage As New ChatMessage(ChatRole.System, BuildSystemPromptText())
            If _history.Count = 0 Then
                _history.Add(systemMessage)
            Else
                _history(0) = systemMessage
            End If
        End Sub

        ''' <summary>
        ''' _modelContextWindows is seeded from /v1/models' static
        ''' max_context_window (RefreshAvailableModelsAsync) - the model's
        ''' declared MAXIMUM, not necessarily what it's actually loaded with
        ''' right now. Lemonade lets a model be loaded at a smaller ctx_size
        ''' than its max (e.g. deliberately, to test compaction sooner), and
        ''' when that happens, everything reading _modelContextWindows (the
        ''' context-usage bar, ModelDetailsDialog, and MaxOutputTokens' own
        ''' remaining-room calculation in SendAsync) silently used the wrong,
        ''' far larger number - confirmed live as the real root cause of an
        ''' always-empty reply: requesting a MaxOutputTokens sized against a
        ''' 262144 window when the model was actually loaded at 10752 asks
        ''' for more total tokens than the real window holds. Called right
        ''' after Lemonade confirms what's actually loaded, overwriting the
        ''' catalog guess with the real ctx_size whenever Lemonade reports
        ''' one for this model.
        ''' </summary>
        Private Sub ApplyRealLoadedContextWindow(modelName As String, allModelsLoaded As IEnumerable(Of LemonadeLoadedModelInfo))
            Dim realCtxSize = allModelsLoaded?.FirstOrDefault(Function(m) m.ModelName = modelName)?.RecipeOptions?.CtxSize
            If realCtxSize.HasValue AndAlso realCtxSize.Value > 0 Then
                _modelContextWindows(modelName) = realCtxSize.Value
            End If
        End Sub

        Private Async Function SwitchModelAsync(modelName As String, cancellationToken As CancellationToken) As Task
            IsBusy = True
            IsModelLoading = True
            StatusText = $"Loading {modelName}... this can take a while for a large model"
            Try
                Await _managementClient.LoadModelAsync(modelName, cancellationToken)
                _currentlyLoadedModel = modelName

                ' Best-effort - if this particular health check fails, the
                ' context window just stays at the catalog's static max
                ' until the next successful one (RefreshAvailableModelsAsync,
                ' InitializeAsync), same as before this fix existed.
                Try
                    Dim health = Await _managementClient.GetHealthAsync(cancellationToken)
                    ApplyRealLoadedContextWindow(modelName, health.AllModelsLoaded)
                    ' Immediate feedback in the context-usage bar rather than
                    ' waiting for the next completed turn's stats to refresh
                    ' it - SetContextUsage isn't used here since
                    ' ContextUsageCurrent shouldn't reset just from switching
                    ' models mid-chat.
                    ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0)
                    ContextUsageText = FormatContextUsageText(ContextUsageCurrent, ContextUsageMax)
                Catch
                End Try

                StatusText = ""
            Catch ex As Exception
                StatusText = $"Failed to load {modelName}: {ex.Message}"
            Finally
                IsBusy = False
                IsModelLoading = False
            End Try
        End Function

        ''' <summary>
        ''' Requires a real SelectedModel (not just non-empty InputText) -
        ''' without it, sending would be possible before InitializeAsync's
        ''' model list/health check finishes, with SelectedModel still "".
        ''' Lemonade would still answer (whatever it already had loaded),
        ''' but ContextUsageMax's
        ''' `_modelContextWindows.GetValueOrDefault(SelectedModel, 0)` lookup
        ''' fails for the empty key, silently showing "X / 0 tokens" and a
        ''' blank model dropdown.
        ''' </summary>
        Private Function CanSend() As Boolean
            Return Not IsBusy AndAlso Not String.IsNullOrWhiteSpace(InputText) AndAlso Not String.IsNullOrWhiteSpace(SelectedModel) AndAlso
                AttachedImageVisionWarning Is Nothing
        End Function

        Private Async Function SendAsync() As Task
            Dim userText = InputText.Trim()
            InputText = ""
            IsBusy = True

            ' Captured and cleared up front - a single-use attachment for
            ' this one turn, not something that should silently carry over
            ' and reattach itself to a later message if it's still "queued"
            ' above the input box. Local names deliberately don't just
            ' differ in case from the AttachedFileName property - VB is
            ' case-insensitive, so "attachedFileName" and "AttachedFileName"
            ' would be the literal same identifier to the compiler.
            Dim pendingFileName = AttachedFileName
            Dim pendingFileText = _attachedFileText
            Dim pendingImagePath = AttachedImagePath
            AttachedFileName = Nothing
            _attachedFileText = Nothing
            AttachedImagePath = Nothing

            ' What the MODEL sees (and what gets persisted/sent to history)
            ' includes the full extracted text; what the BUBBLE shows stays
            ' just the typed message, with a small chip for the filename -
            ' otherwise a multi-thousand-character PDF dump would flood the
            ' visible transcript. Same "displayed vs. sent" split already
            ' used on the assistant side for the "[Stopped]" case. An
            ' attached image gets a lightweight path marker instead of its
            ' actual bytes here - unlike the document case, the real image
            ' content travels through historyMessage's Contents below, not
            ' through this text; this marker only matters for what gets
            ' persisted to the DB and for how a now-older turn still reads
            ' once StripOldImageContent (see the request-building code below)
            ' has dropped its real image content on a later turn.
            Dim textForModel = userText
            If pendingFileText IsNot Nothing Then
                textForModel = $"[Attached file: {pendingFileName}]{Environment.NewLine}{Environment.NewLine}{pendingFileText}{Environment.NewLine}{Environment.NewLine}{userText}"
            ElseIf pendingImagePath IsNot Nothing Then
                textForModel = $"[Attached image: {pendingImagePath}]{Environment.NewLine}{Environment.NewLine}{userText}"
            End If

            Dim cts As New CancellationTokenSource()
            _generationCts = cts
            StopCommand.NotifyCanExecuteChanged()

            Try
                ' The real image bytes live in this ChatMessage's
                ' Contents, not in textForModel - textForModel is still used
                ' as the TextContent alongside it (not bare userText), so
                ' once StripOldImageContent (see StreamOneReplyAsync) drops
                ' the DataContent from an older turn, the remaining text
                ' still reads coherently ("[Attached image: ...]" plus
                ' whatever was actually asked) instead of leaving a bare
                ' question with no indication an image was ever there.
                Dim historyMessage As ChatMessage
                If pendingImagePath IsNot Nothing Then
                    historyMessage = New ChatMessage(ChatRole.User, New List(Of AIContent) From {
                        New TextContent(textForModel),
                        _imageAttachmentService.BuildDataContent(pendingImagePath)
                    })
                Else
                    historyMessage = New ChatMessage(ChatRole.User, textForModel)
                End If
                _history.Add(historyMessage)

                Dim userBubble As New ChatMessageViewModel(isUser:=True, initialText:=userText)
                userBubble.AttachedFileName = pendingFileName
                userBubble.AttachedImagePath = pendingImagePath
                Messages.Add(userBubble)
                SetDateDividerIfNeeded(userBubble)
                _sessionRepository.SaveMessage(_currentSessionId, ChatRole.User.Value, textForModel, reasoningContent:="")

                ' _history(0) only ever holds the STABLE part of the system
                ' prompt (RefreshStableSystemPrompt); this
                ' turn's relevant-memories/knowledge-base search results are
                ' genuinely volatile (different nearly every message) and go
                ' into volatileContextText instead, folded into just this
                ' one outgoing request by AppendVolatileContext - never
                ' _history, never the database. See BuildSystemPromptText's
                ' own comment for why this split matters (prompt caching).
                RefreshStableSystemPrompt()
                Dim relevantMemoriesText = Await _memoryService.GetRelevantMemoriesTextAsync(userText, cts.Token)
                ' Skipped entirely (not just an empty result) when
                ' the module is off, so a disabled Knowledge Bases module
                ' means genuinely zero extra tokens spent on it, not even the
                ' "no knowledge base is attached" note.
                Dim relevantKnowledgeText = ""
                If IsKnowledgeModuleEnabled Then
                    relevantKnowledgeText = Await _knowledgeService.GetRelevantChunksTextAsync(SelectedKnowledgeBaseId, userText, cts.Token)
                End If
                Dim volatileContextSections As New List(Of String) From {TimeAwareness.BuildTimeAwarenessText()}
                If Not String.IsNullOrEmpty(relevantMemoriesText) Then volatileContextSections.Add(relevantMemoriesText)
                If Not String.IsNullOrEmpty(relevantKnowledgeText) Then volatileContextSections.Add(relevantKnowledgeText)
                Dim volatileContextText = String.Join(Environment.NewLine & Environment.NewLine, volatileContextSections)

                Dim summary = Sessions.FirstOrDefault(Function(s) s.Id = _currentSessionId)
                If summary IsNot Nothing Then
                    ' Only auto-title while it's still the "New chat"
                    ' placeholder - if the user already renamed it by hand
                    ' (pencil icon), don't clobber that with an auto-title.
                    If summary.Title = "New chat" Then
                        Dim title = If(userText.Length > 60, userText.Substring(0, 60) & "...", userText)
                        _sessionRepository.RenameSession(_currentSessionId, title)
                    End If

                    ' RefreshSessions() (full rebuild + restore-by-id), not a
                    ' direct Sessions.Remove/Insert(0, ...) - same pattern as
                    ' NewChat's (see its own comment): SaveMessage above
                    ' already bumped UpdatedAt, so the SQL-ordered rebuild
                    ' naturally puts this session first. Removing/re-inserting
                    ' straight into Sessions would leave the grouped,
                    ' virtualized sidebar out of sync with the
                    ' CollectionView's expected order.
                    RefreshSessions()
                End If

                ' Selecting an image-labeled model directly (per the grouped
                ' dropdown - see ModelSelectorItem) skips the LLM entirely
                ' for this turn, matching how Lemonade's own web UI behaves
                ' when an image model is active - avoiding a confusing HTTP
                ' 400 from picking an image model as the "chat" model. Still
                ' returns through the same Finally below (IsBusy/cts
                ' cleanup), just skips everything LLM-specific.
                If _modelCategories.GetValueOrDefault(SelectedModel) = ModelSelectorItem.ImageCategory Then
                    Await SendImageGenerationTurnAsync(userText, cts.Token)
                    Return
                End If

                ' Add the assistant's bubble immediately, empty, then mutate its
                ' Text as tokens stream in - the bound TextBlock updates itself
                ' via INotifyPropertyChanged, no manual re-render needed.
                Dim assistantBubble As New ChatMessageViewModel(isUser:=False, initialText:="")
                assistantBubble.IsStreaming = True
                Messages.Add(assistantBubble)
                SetDateDividerIfNeeded(assistantBubble)

                Dim options As New ChatOptions With {
                    .Tools = _moduleRegistry.GetEnabledTools().ToList(),
                    .RawRepresentationFactory = AddressOf BuildRawChatCompletionOptions
                }
                ' ModelId overrides which model this particular request targets,
                ' without needing to rebuild the IChatClient every time the
                ' selector changes.
                If Not String.IsNullOrEmpty(SelectedModel) Then
                    options.ModelId = SelectedModel
                End If

                ' Left unset, this falls back to Lemonade/llama.cpp's own
                ' server-side default n_predict, which can be low enough that
                ' a reasoning-heavy model exhausts it mid-<think> block and
                ' never reaches real answer text - indistinguishable, from
                ' this app's side, from the model genuinely getting stuck
                ' (see the retry loop below).
                Dim contextWindow = _modelContextWindows.GetValueOrDefault(SelectedModel, 0)
                ' Half the model's own context window, capped at 16384 -
                ' generous, but sized off the selected model so a
                ' small-context model isn't asked for more output than it
                ' could ever produce.
                Dim generousCap = If(contextWindow > 0, Math.Min(contextWindow \ 2, 16384), 8192)
                ' That cap alone isn't enough on its own: it's sized against
                ' the model's TOTAL window, not what's actually left once the
                ' system prompt/tools/history already committed to this
                ' request are accounted for. A large tool list in particular
                ' (every enabled module's schema, sent on every request) can
                ' already eat a real chunk of a small-to-mid context window
                ' before the conversation even starts - requesting a
                ' generous MaxOutputTokens on top of that can ask for more
                ' total tokens (prompt + max_tokens) than the window holds,
                ' which some llama.cpp-server versions handle by returning
                ' immediately with zero tokens generated rather than an
                ' error - an always-empty reply that looks identical to the
                ' "model got stuck" case above but has nothing to do with it
                ' and isn't fixed by retrying. EstimateCommittedContextTokensAsync
                ' uses Lemonade's own real tokenizer when available (see its
                ' own comment) - still not perfect (doesn't count the
                ' volatile end-prompt content appended below, or chat-
                ' template wrapper tokens), so a safety margin is subtracted
                ' on top of it rather than trusting it to the exact token.
                Const safetyMarginTokens = 512
                Dim committed = Await EstimateCommittedContextTokensAsync(cts.Token)
                Dim estimatedPromptTokens = committed.SystemPrompt + committed.Tools + committed.History
                Dim remainingRoom = If(contextWindow > 0, Math.Max(contextWindow - estimatedPromptTokens - safetyMarginTokens, 256), generousCap)
                options.MaxOutputTokens = Math.Min(generousCap, remainingRoom)

                ' After a tool call, Qwen sometimes never properly closes its
                ' reasoning and puts its whole real answer inside
                ' reasoning_content, leaving the actual response text empty -
                ' a known flakiness. Retrying once rather than showing a
                ' blank reply is what this loop does.
                Dim fullReplyText = ""
                Dim wasCancelled = False
                Dim errorMessage As String = Nothing
                Dim finishReason As ChatFinishReason? = Nothing
                Const maxAttempts = 2
                Dim attempt = 1
                While attempt <= maxAttempts
                    assistantBubble.Text = ""
                    assistantBubble.ReasoningText = ""
                    assistantBubble.PromptProgressText = Nothing
                    ' NOT ToolCalls.Clear() here - a retry is for getting a
                    ' coherent final TEXT answer when the model left it
                    ' blank, not because the tool call itself was invalid, so
                    ' a successful tool call from attempt 1 (e.g. write_file)
                    ' must survive into attempt 2 even if attempt 2 doesn't
                    ' call the same tool again. StreamOneReplyAsync's own
                    ' dedup-by-CallId already prevents the same call showing
                    ' twice if it does get re-surfaced.

                    Dim result = Await StreamOneReplyAsync(assistantBubble, options, volatileContextText, cts.Token)
                    fullReplyText = result.Text
                    wasCancelled = result.WasCancelled
                    errorMessage = result.ErrorMessage
                    finishReason = result.FinishReason

                    ' A real error isn't retried - the empty-reply retry above
                    ' is for known model flakiness on an otherwise-working
                    ' request, not for a request that's actually broken (e.g.
                    ' the wrong kind of model selected), where trying again
                    ' would just fail the same way.
                    If wasCancelled OrElse errorMessage IsNot Nothing OrElse Not String.IsNullOrWhiteSpace(fullReplyText) Then
                        Exit While
                    End If
                    attempt += 1
                End While

                ' From here the bubble is done changing - switches its
                ' Markdown-rendered view on (see IsStreaming's comment).
                assistantBubble.IsStreaming = False

                If wasCancelled Then
                    assistantBubble.Text &= If(assistantBubble.Text.Length > 0, Environment.NewLine, "") & "[Stopped]"
                ElseIf errorMessage IsNot Nothing Then
                    ' Two real causes share this branch: selecting a non-chat
                    ' model (e.g. the embedding model), which would otherwise
                    ' crash the whole app (see StreamOneReplyAsync's Catch),
                    ' and Lemonade rejecting the request outright because the
                    ' prompt itself already exceeds the model's context size
                    ' (see StreamOneReplyAsync's "$.error" handling) - the
                    ' generic "switch models" hint doesn't apply to the
                    ' second one, so it gets its own, more useful hint.
                    Dim hint = If(errorMessage.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase),
                        "Try loading this model with a larger context size, disabling modules/tools you don't need for this chat, or starting a new chat.",
                        "If you've selected a non-chat model (e.g. an embedding model) in the model dropdown above, switch back to a real chat model and try again.")
                    ' A single newline reads as a real line break while
                    ' IsStreaming is still true (plain TextBox), but the
                    ' bubble switches to MarkdownViewer once streaming ends -
                    ' CommonMark collapses a single "\n" into just a space,
                    ' not a line break, so this needs a real blank line
                    ' (double newline) to render as two separate lines.
                    ' Lemonade's own error text also has no trailing
                    ' punctuation, so one's added here for a clean sentence
                    ' break rather than running straight into the hint.
                    assistantBubble.Text = $"⚠️ Something went wrong generating a reply: {errorMessage.TrimEnd("."c, " "c)}." &
                        Environment.NewLine & Environment.NewLine & hint
                ElseIf String.IsNullOrWhiteSpace(fullReplyText) Then
                    ' A Length finish reason means the reply was cut off by
                    ' MaxOutputTokens before any real answer text arrived.
                    ' Two different real causes look identical from here: the
                    ' model still being genuinely mid-reasoning when a
                    ' generous budget ran out, or - much more diagnosable,
                    ' and worth naming specifically when it's true - the
                    ' system prompt/tools/history already filling most of
                    ' the context window before generation even started
                    ' (confirmed live: a large active tool list can eat most
                    ' of a small/reduced context window on its own).
                    ' contextWindow/estimatedPromptTokens are the exact
                    ' numbers MaxOutputTokens was already computed from
                    ' above, reused here rather than guessed again.
                    Dim contextLikelyTooSmall = finishReason = ChatFinishReason.Length AndAlso
                        contextWindow > 0 AndAlso estimatedPromptTokens >= contextWindow * 0.9

                    If contextLikelyTooSmall Then
                        assistantBubble.Text = $"(No response - your system prompt, tools, and conversation history already use an estimated ~{estimatedPromptTokens:N0} of this model's {contextWindow:N0}-token context window, leaving little or no room for a reply. Try loading this model with a larger context size, or disable modules/tools you don't need for this chat.)"
                    ElseIf finishReason = ChatFinishReason.Length Then
                        assistantBubble.Text = "(No response - the model's reply was cut off before it reached an answer, most likely still mid-reasoning when it ran out of output tokens. Try asking again or simplifying the request.)"
                    Else
                        assistantBubble.Text = "(No response - the model's reasoning didn't lead to an answer. Try asking again.)"
                    End If
                End If


                ' What actually goes into history/the DB: the model's real
                ' text when there is any (even a cancelled-partial reply),
                ' falling back to the same message shown in the bubble only
                ' when there's truly nothing else - keeps "[Stopped]" (a UI
                ' decoration, not something the model said) out of what gets
                ' fed back to the model on the next turn.
                Dim textToPersist = If(String.IsNullOrWhiteSpace(fullReplyText), assistantBubble.Text, fullReplyText)
                _history.Add(New ChatMessage(ChatRole.Assistant, textToPersist))
                _sessionRepository.SaveMessage(_currentSessionId, ChatRole.Assistant.Value, textToPersist, assistantBubble.ReasoningText,
                    toolCalls:=ToolCallResult.SerializeForStorage(assistantBubble.ToolCalls))

                ' Auto-tagging - a chat model calling generate_image
                ' mid-conversation still tags the session "image", same as
                ' the direct-image-model path does in
                ' SendImageGenerationTurnAsync. AddTagToSession is idempotent
                ' (INSERT OR IGNORE), so tagging on every turn that happens
                ' to call the tool again is harmless, not a growing duplicate.
                If assistantBubble.ToolCalls.Any(Function(t) t.Name = "generate_image") Then
                    _sessionRepository.AddTagToSession(_currentSessionId, "image")
                    ' Without this, the sidebar's tag chip wouldn't show
                    ' until the next unrelated RefreshSessions() (e.g.
                    ' switching chats) - the live ChatSessionSummary object's
                    ' own Tags collection is only ever populated once, when
                    ' it was first loaded, and adding a tag to the DB
                    ' doesn't retroactively update an object already sitting
                    ' in Sessions/SidebarItems.
                    RefreshSessions()
                End If

                ' Same auto-tagging idea, extended to the two
                ' other modules whose use is a deliberate, notable action
                ' worth being able to find later (unlike e.g. web search,
                ' which would fire in a large fraction of ordinary chats and
                ' make the tag meaningless as a filter). Coder's tools all
                ' share a consistent "code_" prefix, so a simple pattern
                ' match is enough - MCP tool names come straight from
                ' whatever each connected server calls them, with no such
                ' convention, hence ModuleRegistry.IsToolFromModule.
                If assistantBubble.ToolCalls.Any(Function(t) t.Name.StartsWith("code_")) Then
                    _sessionRepository.AddTagToSession(_currentSessionId, "coder")
                    RefreshSessions()
                End If
                If assistantBubble.ToolCalls.Any(Function(t) _moduleRegistry.IsToolFromModule(t.Name, "Mcp")) Then
                    _sessionRepository.AddTagToSession(_currentSessionId, "mcp")
                    RefreshSessions()
                End If

                If Not wasCancelled AndAlso errorMessage Is Nothing Then
                    ' Best-effort - a failed stats fetch shouldn't undo a reply
                    ' the user already received, so this gets its own Try
                    ' rather than being covered by the outer one. Skipped
                    ' entirely on cancellation since a stopped request has no
                    ' meaningful stats to show.
                    Try
                        ' Bounded even though this is just a quick metadata
                        ' GET, not a generation - consistent "no background
                        ' call left truly uncancellable" rule, since a stuck
                        ' generation elsewhere can hang indefinitely with
                        ' CancellationToken.None.
                        Dim statsTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                        Dim stats = Await _managementClient.GetStatsAsync(statsTimeoutCts.Token)
                        TokensInOutText = $"{stats.InputTokens} / {stats.OutputTokens}"
                        TokensPerSecondText = $"{stats.TokensPerSecond:0.0}"
                        ' Three decimal places, not two, since two rounds
                        ' away real precision Lemonade itself reports (e.g.
                        ' its own 0.066s would show as 0.07s). The card
                        ' label carries the unit ("Time to first token (s)"),
                        ' so this is just the bare number, no "s" suffix.
                        TimeToFirstTokenText = $"{stats.TimeToFirstToken:0.000}"

                        _sessionTotalInputTokens += stats.InputTokens
                        _sessionTotalOutputTokens += stats.OutputTokens
                        SessionTotalTokensText = $"{_sessionTotalInputTokens} / {_sessionTotalOutputTokens}"

                        ' PromptTokens alone, not + OutputTokens - see
                        ' PromptTokens' own comment for why InputTokens was
                        ' wrong here. Output is deliberately excluded too:
                        ' this needs to be directly comparable with the
                        ' breakdown popup's "Next turn" estimate (also a
                        ' prompt-only figure), and a reply heavy on reasoning
                        ' can generate a lot of OUTPUT tokens that then never
                        ' get persisted into history at all (see
                        ' StreamOneReplyAsync's ReasoningText handling) -
                        ' including them here made "Last reported" bigger
                        ' than the very next prompt would actually be,
                        ' reading as context somehow shrinking. The Stats
                        ' tab's "Tokens in/out" already shows OutputTokens
                        ' separately for anyone who wants that figure.
                        ContextUsageCurrent = stats.PromptTokens
                        ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0)
                        ContextUsageText = FormatContextUsageText(ContextUsageCurrent, ContextUsageMax)

                        ' Persisted per-session so switching back to this chat
                        ' later can restore the real number instead of
                        ' guessing or showing 0 (see LoadSession).
                        _sessionRepository.UpdateLastContextTokens(_currentSessionId, ContextUsageCurrent)
                    Catch
                        ' Leave the previous stats/context-usage numbers showing rather than blanking them on a transient failure.
                    End Try

                    ' Fire-and-forget, not Await - extraction is a second full
                    ' model round-trip, and there's no reason to make the user
                    ' wait for it before they can send their next message.
                    ' Skipped on cancellation since a "[Stopped]"/partial reply
                    ' isn't a real exchange worth mining for facts.
                    ExtractFactsFireAndForget(userText, textToPersist)
                End If

                ' Deliberately its OWN condition, not folded into the stats
                ' block above - that one is gated on errorMessage Is Nothing,
                ' but a genuine rejection (most notably Lemonade's own
                ' "exceeds the available context size" error) is exactly the
                ' moment compaction is needed most. Confirmed live this was
                ' a real, serious bug: once a conversation hit that error,
                ' the one path that could shrink it back down never ran,
                ' leaving the conversation permanently stuck failing every
                ' subsequent send. Still skipped on wasCancelled - a
                ' user-stopped generation isn't a reason to react.
                If Not wasCancelled Then
                    ' Awaited, not fire-and-forget (unlike fact extraction
                    ' above) - this mutates _history directly, and IsBusy
                    ' stays True (blocking a second SendAsync) only until
                    ' this Finally block, so a background mutation here could
                    ' still be running when the user's next message starts
                    ' building the next request. Checked after this turn's
                    ' reply and stats, not before, so it only ever affects
                    ' the *next* request, never the one just answered.
                    ' Bounded, not CancellationToken.None - see
                    ' ExtractFactsFireAndForget's comment for why (a stuck
                    ' summarization generation is the same risk as stuck
                    ' fact extraction).
                    Using compactionTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(BackgroundModelCallTimeout))
                        Await CompactHistoryIfNeededAsync(assistantBubble, compactionTimeoutCts.Token)
                    End Using
                End If

                ' Mirrors this turn's tool calls into a MainViewModel-level
                ' property so the right panel's Stats tab can show them
                ' without reaching into Messages to find the last assistant
                ' bubble itself.
                CurrentTurnToolCalls.Clear()
                For Each toolName In assistantBubble.ToolCalls
                    CurrentTurnToolCalls.Add(toolName)
                Next
            Finally
                IsBusy = False
                cts.Dispose()
                _generationCts = Nothing
                StopCommand.NotifyCanExecuteChanged()
            End Try
        End Function

        ''' <summary>
        ''' The direct path for an image-labeled model selected in the main
        ''' dropdown - the typed message IS the prompt, sent straight to
        ''' Lemonade's images endpoint via the shared ImageGenerationService
        ''' (the same one ImageGenModule's generate_image tool uses), no LLM
        ''' round-trip at all. Still asks for size confirmation every time
        ''' (ImageSizeDialog), same as the tool-based path. The message/
        ''' bubble were already added by
        ''' SendAsync before this is called; this only adds the assistant
        ''' side.
        ''' </summary>
        Private Async Function SendImageGenerationTurnAsync(prompt As String, cancellationToken As CancellationToken) As Task
            Dim assistantBubble As New ChatMessageViewModel(isUser:=False, initialText:="")
            Messages.Add(assistantBubble)
            SetDateDividerIfNeeded(assistantBubble)

            Dim sizeChoice = ImageSizeDialog.Show(prompt, _imageGenSettings.DefaultWidth, _imageGenSettings.DefaultHeight)
            If Not sizeChoice.Confirmed Then
                assistantBubble.Text = "(Cancelled - no image was generated.)"
            Else
                Try
                    Dim imageUri = Await _imageGenerationService.GenerateAsync(
                        prompt, SelectedModel, sizeChoice.Width, sizeChoice.Height,
                        _imageGenSettings.Steps, _imageGenSettings.CfgScale, _imageGenSettings.Seed,
                        cancellationToken)

                    Dim altText = If(prompt.Length > 80, prompt.Substring(0, 80) & "...", prompt)
                    assistantBubble.Text = $"![{altText}]({imageUri})"

                    ' Auto-tagging - see SendAsync's matching hook
                    ' for the tool-based path. RefreshSessions() right after
                    ' for the same reason that one needs it too - see its
                    ' own comment.
                    _sessionRepository.AddTagToSession(_currentSessionId, "image")
                    RefreshSessions()
                Catch ex As OperationCanceledException
                    assistantBubble.Text = "[Stopped]"
                Catch ex As Exception
                    assistantBubble.Text = $"⚠️ Couldn't generate that image: {ex.Message}"
                End Try
            End If

            _history.Add(New ChatMessage(ChatRole.Assistant, assistantBubble.Text))
            _sessionRepository.SaveMessage(_currentSessionId, ChatRole.Assistant.Value, assistantBubble.Text, reasoningContent:="")
        End Function

        ''' <summary>
        ''' Strips image content from every message except the newest one
        ''' before sending to the model - old images cost real tokens for
        ''' no benefit once they're not the current turn, rather than
        ''' resending every attached image on every subsequent turn forever.
        ''' Non-mutating: _history itself keeps every image permanently
        ''' (LoadSession needs the DataContent still reconstructable if the
        ''' session is ever reloaded and that turn happens to still be the
        ''' newest one with an image) - only this one outgoing request's copy
        ''' has the older ones' DataContent dropped, keeping each stripped
        ''' message's TextContent (its "[Attached image: ...]" marker plus
        ''' whatever was actually asked - see SendAsync's comment) so the
        ''' conversation still reads coherently without the real bytes.
        ''' </summary>
        Private Shared Function StripOldImageContent(history As IReadOnlyList(Of ChatMessage)) As List(Of ChatMessage)
            Dim newestImageIndex = -1
            For i = 0 To history.Count - 1
                If history(i).Contents.Any(Function(c) TypeOf c Is DataContent) Then
                    newestImageIndex = i
                End If
            Next
            If newestImageIndex = -1 Then Return history.ToList()

            Return history.Select(
                Function(message, index)
                    If index = newestImageIndex OrElse Not message.Contents.Any(Function(c) TypeOf c Is DataContent) Then
                        Return message
                    End If
                    Dim textOnlyContents = message.Contents.Where(Function(c) Not (TypeOf c Is DataContent)).ToList()
                    Return New ChatMessage(message.Role, textOnlyContents)
                End Function).ToList()
        End Function

        ''' <summary>
        ''' This turn's relevant-memories/knowledge-base search results get
        ''' folded into a COPY of the newest message only, for this one
        ''' outgoing request - never _history itself (which would resend
        ''' stale search results on every future turn forever) and never the
        ''' database (SaveMessage already only ever persisted textForModel,
        ''' which never included this). Prepended before the message's own
        ''' existing content (grounding context first, the actual question
        ''' last), not appended as a separate trailing message - appending
        ''' would need a turn-order spacer pass to avoid two same-role
        ''' messages in a row; folding into the existing message's Contents
        ''' sidesteps that problem entirely since no new message is added.
        ''' </summary>
        Private Shared Function AppendVolatileContext(messages As List(Of ChatMessage), volatileContextText As String) As List(Of ChatMessage)
            If String.IsNullOrEmpty(volatileContextText) OrElse messages.Count = 0 Then Return messages

            Dim lastMessage = messages(messages.Count - 1)
            Dim augmentedContents As New List(Of AIContent) From {New TextContent(volatileContextText)}
            augmentedContents.AddRange(lastMessage.Contents)

            Dim result As New List(Of ChatMessage)(messages)
            result(result.Count - 1) = New ChatMessage(lastMessage.Role, augmentedContents)
            Return result
        End Function

        ''' <summary>
        ''' Sets return_progress on the outgoing request via
        ''' ChatCompletionOptions.Patch - return_progress isn't part of the
        ''' OpenAI schema at all, it's a llama.cpp/Lemonade-specific field;
        ''' adding it turns on periodic prompt_progress chunks during the
        ''' streaming response. The .NET SDK's own source
        ''' (OpenAIChatClient.ToOpenAIOptions) uses this exact JsonPatch
        ''' mechanism internally for ModelId, and merges
        ''' Tools/ModelId/Temperature/etc onto whatever this factory returns
        ''' afterward (via ??=), so a near-empty options object here loses
        ''' nothing else. SCME0001 is JsonPatch's own experimental
        ''' diagnostic ID, not a real risk - the SDK suppresses the exact same
        ''' warning internally for the exact same feature.
        ''' </summary>
        Private Function BuildRawChatCompletionOptions(client As IChatClient) As Object
            Dim native As New OpenAI.Chat.ChatCompletionOptions()
#Disable Warning SCME0001
            native.Patch.Set(Encoding.UTF8.GetBytes("$.return_progress"), True)
#Enable Warning SCME0001
            Return native
        End Function

        ''' <summary>
        ''' One streaming attempt against _history: mutates bubble's
        ''' Text/ReasoningText/ToolCalls live as the reply comes in, and
        ''' returns the plain accumulated answer text once the stream ends
        ''' (or is cancelled). Pulled out of SendAsync so the retry-on-empty
        ''' loop there can call this more than once without duplicating the
        ''' whole streaming/parsing block.
        ''' </summary>
        Private Async Function StreamOneReplyAsync(bubble As ChatMessageViewModel, options As ChatOptions, volatileContextText As String, cancellationToken As CancellationToken) As Task(Of (Text As String, WasCancelled As Boolean, ErrorMessage As String, FinishReason As ChatFinishReason?))
            ' VB has no "await foreach" and no "Await" inside a Finally
            ' block, so the IAsyncEnumerator is walked and disposed by hand,
            ' outside any Try/Finally, rather than the C#-style "await
            ' foreach" or a Finally-based cleanup.
            Dim fullReply As New Text.StringBuilder()
            Dim pendingReasoning As New Text.StringBuilder()
            Dim wasCancelled = False
            Dim errorMessage As String = Nothing
            Dim finishReason As ChatFinishReason? = Nothing
            Dim hasStartedAnswering = False
            Dim thinkingStopwatch = Stopwatch.StartNew()

            ' Assigning bubble.Text on every single streamed chunk would
            ' mean a full WPF TextBox layout/measure pass per chunk - for a
            ' long reply that's thousands of full-content re-layouts,
            ' freezing the whole window for stretches (worse the longer the
            ' reply gets). Batching UI updates to a few times a second -
            ' standard practice for any UI streaming high-frequency text -
            ' cuts that to a few dozen layout passes, with no visible loss
            ' of "live streaming" feel.
            Dim lastUiFlushTicks = Environment.TickCount64
            Const UiFlushIntervalMs = 75L

            Dim outgoingMessages = AppendVolatileContext(StripOldImageContent(_history), volatileContextText)
            Dim response = _chatClient.GetStreamingResponseAsync(outgoingMessages, options, cancellationToken)
            Dim enumerator = response.GetAsyncEnumerator(cancellationToken)
            Try
                While Await enumerator.MoveNextAsync()
                    Dim update = enumerator.Current
                    fullReply.Append(update.Text)
                    If update.FinishReason IsNot Nothing Then finishReason = update.FinishReason

                    ' A request Lemonade rejects outright (e.g. the prompt
                    ' itself already exceeds the model's context size) comes
                    ' back as HTTP 200 with Content-Type text/event-stream -
                    ' a genuinely successful-looking stream - whose one and
                    ' only SSE payload is {"error": {...}} instead of a real
                    ' completion chunk. Confirmed live (via a raw repro
                    ' against this exact failure) that neither the OpenAI SDK
                    ' nor Microsoft.Extensions.AI treats this as an error at
                    ' all - no exception is thrown, and update.Text/
                    ' FinishReason both come back empty, indistinguishable
                    ' from the model just producing nothing. The one place
                    ' the real error message survives is this same
                    ' RawRepresentation/JsonPatch mechanism prompt_progress
                    ' already uses below, at its own top-level "$.error" path
                    ' - checked unconditionally (not gated on
                    ' hasStartedAnswering) since an error chunk is always the
                    ' one and only chunk in the stream.
                    Dim errorNativeUpdate = TryCast(update.RawRepresentation, OpenAI.Chat.StreamingChatCompletionUpdate)
                    If errorNativeUpdate IsNot Nothing Then
                        Dim errorPath = Encoding.UTF8.GetBytes("$.error")
#Disable Warning SCME0001
                        If errorNativeUpdate.Patch.Contains(errorPath) Then
                            Dim errorJson = errorNativeUpdate.Patch.GetJson(errorPath)
#Enable Warning SCME0001
                            Using doc = JsonDocument.Parse(errorJson)
                                Dim root = doc.RootElement
                                Dim messageProp As JsonElement = Nothing
                                errorMessage = If(root.TryGetProperty("message", messageProp), messageProp.GetString(), "Lemonade rejected the request.")
                            End Using
                            Exit While
                        End If
                    End If

                    ' Prompt-processing progress ("62% (ETA: 8s)") - only
                    ' meaningful before any real generation has started;
                    ' llama.cpp stops sending prompt_progress the moment
                    ' token generation begins, so this only actually shows
                    ' anything for genuinely large prompts. See
                    ' BuildRawChatCompletionOptions for the request-side half
                    ' of this (return_progress).
                    '
                    ' The deserializer only stores an UNKNOWN top-level
                    ' property as ONE opaque JSON blob at its own path
                    ' ("$.prompt_progress") - it does NOT break it into
                    ' individually addressable sub-paths, so querying a
                    ' nested path like "$.prompt_progress.processed" directly
                    ' silently never matches anything. Patch.Contains/GetJson
                    ' has to target the PARENT path only, then parse that
                    ' JSON blob normally.
                    If Not hasStartedAnswering Then
                        Dim nativeUpdate = TryCast(update.RawRepresentation, OpenAI.Chat.StreamingChatCompletionUpdate)
                        If nativeUpdate IsNot Nothing Then
                            Dim progressPath = Encoding.UTF8.GetBytes("$.prompt_progress")
#Disable Warning SCME0001
                            If nativeUpdate.Patch.Contains(progressPath) Then
                                Dim progressJson = nativeUpdate.Patch.GetJson(progressPath)
#Enable Warning SCME0001
                                Using doc = JsonDocument.Parse(progressJson)
                                    Dim root = doc.RootElement
                                    Dim processed = root.GetProperty("processed").GetInt32()
                                    Dim total = root.GetProperty("total").GetInt32()
                                    Dim cache = root.GetProperty("cache").GetInt32()
                                    Dim timeMs = root.GetProperty("time_ms").GetDouble()

                                    ' Cached (KV-cache-reused) prefix tokens are excluded
                                    ' from both processed and total, since those
                                    ' tokens never actually needed re-processing.
                                    Dim actualProcessed = processed - cache
                                    Dim actualTotal = total - cache
                                    If actualTotal > 0 Then
                                        Dim percent = CInt(Math.Round(100.0 * actualProcessed / actualTotal))
                                        Dim etaText = ""
                                        If actualProcessed > 0 AndAlso timeMs >= 500 Then
                                            Dim etaSecs = (timeMs / 1000.0) * (CDbl(actualTotal) / actualProcessed - 1.0)
                                            etaText = $" (ETA: {Math.Max(0, CInt(Math.Round(etaSecs)))}s)"
                                        End If
                                        bubble.PromptProgressText = $"Processing prompt: {percent}%{etaText}"
                                    End If
                                End Using
                            End If
                        End If
                    End If

                    ' The first real answer token marks the end of "thinking" -
                    ' matches the mockup's "Thought for Ns" collapsed-header
                    ' convention instead of a static "Thinking" label.
                    If Not hasStartedAnswering AndAlso Not String.IsNullOrEmpty(update.Text) Then
                        hasStartedAnswering = True
                        bubble.ThinkingHeaderText = $"Thought for {thinkingStopwatch.Elapsed.TotalSeconds:0.0}s"
                        bubble.PromptProgressText = Nothing
                    End If

                    ' update.Text only concatenates TextContent - reasoning
                    ' arrives as separate TextReasoningContent items in
                    ' update.Contents, so it's pulled out here rather than
                    ' relying on update.Text to include it.
                    For Each content In update.Contents
                        Dim reasoning = TryCast(content, TextReasoningContent)
                        If reasoning IsNot Nothing Then
                            pendingReasoning.Append(reasoning.Text)
                            ' Reasoning tokens don't flip hasStartedAnswering
                            ' (that's reserved for real answer text - see
                            ' above), but they're still real generation, so
                            ' prompt processing is just as over as if the
                            ' answer itself had started.
                            bubble.PromptProgressText = Nothing
                        End If

                        ' UseFunctionInvocation() (see LemonadeChatClientFactory)
                        ' handles actually calling the tool automatically, but
                        ' still surfaces a FunctionCallContent (the call) and
                        ' a matching FunctionResultContent (the real outcome,
                        ' correlated by CallId) in the stream - that's the
                        ' hook used here, not a separate tracking mechanism.
                        ' Succeeded starts Nothing (see ToolCallResult's own
                        ' comment) rather than defaulting to True, since an
                        ' always-true checkmark before the real result exists
                        ' would be misleading, not just wrong once a result
                        ' eventually arrives.
                        Dim functionCall = TryCast(content, FunctionCallContent)
                        If functionCall IsNot Nothing AndAlso Not bubble.ToolCalls.Any(Function(t) t.CallId = functionCall.CallId) Then
                            bubble.ToolCalls.Add(New ToolCallResult With {.Name = functionCall.Name, .CallId = functionCall.CallId})
                        End If

                        Dim functionResult = TryCast(content, FunctionResultContent)
                        If functionResult IsNot Nothing Then
                            Dim matchingCall = bubble.ToolCalls.FirstOrDefault(Function(t) t.CallId = functionResult.CallId)
                            If matchingCall IsNot Nothing Then
                                matchingCall.Succeeded = functionResult.Exception Is Nothing
                                matchingCall.ErrorMessage = functionResult.Exception?.Message
                            End If
                        End If
                    Next

                    Dim nowTicks = Environment.TickCount64
                    If nowTicks - lastUiFlushTicks >= UiFlushIntervalMs Then
                        FlushBubbleText(bubble, fullReply, pendingReasoning)
                        lastUiFlushTicks = nowTicks
                    End If
                End While
            Catch ex As OperationCanceledException
                ' StopCommand was clicked - not an error, just an early exit.
                wasCancelled = True
            Catch ex As Exception
                ' Without a general Catch here, selecting a non-chat model
                ' (e.g. the embedding model) in the model dropdown and
                ' sending a message would send a chat request Lemonade can't
                ' fulfil, throw, and propagate all the way up through
                ' SendAsync (which also has no Catch, only a Finally),
                ' crashing the whole app - or any other unexpected
                ' Lemonade/network failure mid-stream would do the same.
                ' Surfaced as a normal-looking error reply instead of a crash.
                errorMessage = ex.Message
            End Try
            Await enumerator.DisposeAsync()

            ' Guaranteed final flush - the throttle above means the very last
            ' chunk(s) before the stream ended may not have been shown yet.
            FlushBubbleText(bubble, fullReply, pendingReasoning)

            ' Covers the edge case where the stream ends (cancelled, or a
            ' genuine error) while still mid-prompt-processing - without
            ' this, a stuck "Processing prompt: 43%" would linger on the
            ' bubble forever, since the two clear points above only fire
            ' once real generation actually starts.
            bubble.PromptProgressText = Nothing

            Return (Text:=fullReply.ToString(), WasCancelled:=wasCancelled, ErrorMessage:=errorMessage, FinishReason:=finishReason)
        End Function

        ''' <summary>Pushes accumulated text/reasoning to the bubble's bound properties and clears the reasoning buffer - see StreamOneReplyAsync's throttling comment.</summary>
        Private Shared Sub FlushBubbleText(bubble As ChatMessageViewModel, fullReply As Text.StringBuilder, pendingReasoning As Text.StringBuilder)
            bubble.Text = fullReply.ToString()
            If pendingReasoning.Length > 0 Then
                bubble.ReasoningText &= pendingReasoning.ToString()
                pendingReasoning.Clear()
            End If
        End Sub

    End Class

End Namespace
