Imports System.Collections.ObjectModel
Imports CommunityToolkit.Mvvm.ComponentModel

Namespace ViewModels

    ''' <summary>
    ''' One chat bubble. Properties are manually-implemented observables
    ''' (not the &lt;ObservableProperty&gt; attribute - CommunityToolkit.Mvvm's
    ''' source generator doesn't run for VB.NET projects at all.
    ''' ObservableObject.SetProperty itself is plain runtime code, not
    ''' generated, so it works fine - only the attribute-based codegen is
    ''' C#-only). When the assistant's reply streams in token-by-token,
    ''' MainViewModel just keeps mutating this same object's Text/ReasoningText
    ''' - the XAML bindings pick up each change automatically via
    ''' INotifyPropertyChanged, no manual UI refresh call needed.
    ''' </summary>
    Public Class ChatMessageViewModel
        Inherits ObservableObject

        Public ReadOnly Property IsUser As Boolean

        ''' <summary>
        ''' When this message was actually sent/received (local time) - used
        ''' by date dividers and the hover-timestamp. Set once at
        ''' construction, not observable - a message's own send time never
        ''' changes after the fact.
        ''' </summary>
        Public ReadOnly Property CreatedAt As DateTime

        ''' <summary>
        ''' Non-Nothing only on the first message of a chat, or the first one
        ''' after the calendar day changed since the previous message -
        ''' MainViewModel.SetDateDividerIfNeeded computes this once, right
        ''' after the message is added, rather than the XAML template trying
        ''' to compare against "the previous item" itself (an ItemsControl's
        ''' DataTemplate has no clean way to do that).
        ''' </summary>
        Private _dateDividerText As String
        Public Property DateDividerText As String
            Get
                Return _dateDividerText
            End Get
            Set(value As String)
                SetProperty(_dateDividerText, value)
            End Set
        End Property

        Private _text As String
        Public Property Text As String
            Get
                Return _text
            End Get
            Set(value As String)
                SetProperty(_text, value)
            End Set
        End Property

        ' Reasoning/"thinking" content - Lemonade returns this in a separate
        ' field from the answer text, so it's kept in its own property here
        ' rather than mixed into Text, letting the UI show it in a separate
        ' collapsible panel.
        Private _reasoningText As String = ""
        Public Property ReasoningText As String
            Get
                Return _reasoningText
            End Get
            Set(value As String)
                If SetProperty(_reasoningText, value) Then
                    ' HasReasoning is derived from ReasoningText, not its own
                    ' backing field, so it needs an explicit notification -
                    ' SetProperty only raises PropertyChanged for the property
                    ' whose own backing field it was given.
                    OnPropertyChanged(NameOf(HasReasoning))
                End If
            End Set
        End Property

        ''' <summary>Whether there's any reasoning content to show the collapsible toggle for.</summary>
        Public ReadOnly Property HasReasoning As Boolean
            Get
                Return Not String.IsNullOrEmpty(ReasoningText)
            End Get
        End Property

        ' Starts collapsed, matching the standard "Thoughts" panel convention
        ' used by chat UIs generally. Bound two-way to an Expander in
        ' MainWindow.xaml, so the user can toggle it per message.
        Private _isReasoningExpanded As Boolean = False
        Public Property IsReasoningExpanded As Boolean
            Get
                Return _isReasoningExpanded
            End Get
            Set(value As Boolean)
                SetProperty(_isReasoningExpanded, value)
            End Set
        End Property

        ' "Thinking" while still streaming reasoning, then "Thought for Ns"
        ' once the model starts producing its actual answer text - matches
        ' the mockup's collapsed-thinking-header convention. Timed in
        ' MainViewModel.StreamOneReplyAsync (a Stopwatch started when the turn
        ' begins, stopped the first time real answer text arrives), not
        ' tracked here - this property is just where the result lands.
        Private _thinkingHeaderText As String = "Thinking"
        Public Property ThinkingHeaderText As String
            Get
                Return _thinkingHeaderText
            End Get
            Set(value As String)
                SetProperty(_thinkingHeaderText, value)
            End Set
        End Property

        ' Prompt-processing progress ("62% (ETA: 8s)") - only populated while
        ' Lemonade is still processing the prompt itself, before the first
        ' real token arrives (see MainViewModel.StreamOneReplyAsync's own
        ' comment on StreamingChatCompletionUpdate.Patch for where this comes
        ' from). Nothing/blank for the entire rest of a normal short reply,
        ' where prompt processing finishes before the UI could ever show it -
        ' this only becomes visible for genuinely large prompts.
        Private _promptProgressText As String = Nothing
        Public Property PromptProgressText As String
            Get
                Return _promptProgressText
            End Get
            Set(value As String)
                SetProperty(_promptProgressText, value)
            End Set
        End Property

        ' Filename shown as a chip on a user message that had a file
        ' attached - live-session-only, same deliberate scope cut as
        ' ToolCalls below: the full extracted
        ' text is what's actually persisted as part of this message's
        ' Content, this is just a UI decoration so the bubble itself stays
        ' clean rather than showing the whole extracted document inline.
        Private _attachedFileName As String
        Public Property AttachedFileName As String
            Get
                Return _attachedFileName
            End Get
            Set(value As String)
                SetProperty(_attachedFileName, value)
            End Set
        End Property

        ''' <summary>
        ''' Local path to a resized copy of a user-attached image, shown as
        ''' a real thumbnail (see MainWindow.xaml) rather than a
        ''' filename chip - unlike AttachedFileName, this one IS restored on
        ''' reload (LoadSession parses the "[Attached image: ...]" marker
        ''' MainViewModel.SendAsync persists), since there's no text-
        ''' extraction equivalent for an image the way there is for a
        ''' document - losing it on reload would mean losing the image
        ''' entirely, not just a UI decoration.
        ''' </summary>
        Private _attachedImagePath As String
        Public Property AttachedImagePath As String
            Get
                Return _attachedImagePath
            End Get
            Set(value As String)
                SetProperty(_attachedImagePath, value)
            End Set
        End Property

        ''' <summary>
        ''' Names of tools called while producing this reply - shown as small
        ''' chips (see MainWindow.xaml). Populated from FunctionCallContent/
        ''' FunctionResultContent items seen during streaming
        ''' (MainViewModel.StreamOneReplyAsync) - each entry's real
        ''' success/failure, not just its name, persisted to the
        ''' database (Messages.ToolCalls) and restored on reload.
        ''' </summary>
        Public ReadOnly Property ToolCalls As New ObservableCollection(Of ToolCallResult)()

        ''' <summary>
        ''' App-level housekeeping notices attached to this reply - currently
        ''' just "compacted older messages" (see
        ''' MainViewModel.CompactHistoryIfNeededAsync). Kept separate from
        ''' ToolCalls (rendered with its own, visually distinct chip style in
        ''' MainWindow.xaml) since these aren't something the model did, and
        ''' labeling them as a "tool call" would misrepresent that. Same
        ''' "silent behavior needs a visible indicator" reasoning as the
        ''' tool-call chips themselves.
        ''' </summary>
        Public ReadOnly Property SystemNotes As New ObservableCollection(Of String)()

        ' True only while this specific bubble is actively being streamed
        ' into. Assistant replies are shown as plain live text while this is
        ' True and re-rendered as formatted Markdown once it flips to False -
        ' deliberately NOT rendering Markdown live during streaming, since a
        ' MarkdownViewer re-parses its whole input into a new FlowDocument on
        ' every change - anything heavier than a plain string assignment on
        ' every one of a long reply's many streamed chunks freezes the UI
        ' thread. Always False for a user bubble and for
        ' anything rebuilt from stored history (LoadSession never streams).
        Private _isStreaming As Boolean = False
        Public Property IsStreaming As Boolean
            Get
                Return _isStreaming
            End Get
            Set(value As Boolean)
                SetProperty(_isStreaming, value)
            End Set
        End Property

        Public Sub New(isUser As Boolean, initialText As String, Optional createdAt As DateTime? = Nothing)
            Me.IsUser = isUser
            _text = initialText
            Me.CreatedAt = If(createdAt, DateTime.Now)
        End Sub

    End Class

End Namespace
