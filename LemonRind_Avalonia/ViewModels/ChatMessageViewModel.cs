using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One chat bubble. Ported from the VB.NET/WPF LemonRind app's
/// ViewModels\ChatMessageViewModel.vb. Holds IsUser/Text, ReasoningText/HasReasoning for the
/// thinking panel, the tool-call chips with their success/failure state,
/// and the other per-bubble state (dates, attached and generated images).
/// </summary>
public partial class ChatMessageViewModel : ObservableObject
{
    public bool IsUser { get; }

    [ObservableProperty]
    private string _text = "";

    // Reasoning/"thinking" content - Lemonade returns this in a separate
    // stream field from the answer text, kept in its own property so the UI
    // can show it in a separate collapsible panel rather than mixed into
    // Text.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    private string _reasoningText = "";

    public bool HasReasoning => !string.IsNullOrEmpty(ReasoningText);

    // Initial state comes from AppSettings.Ui.ThinkingPanelOpenByDefault
    // (set by MainViewModel when the bubble is created); TwoWay-bound to the
    // Expander so a manual toggle on one reply sticks for that bubble.
    [ObservableProperty]
    private bool _isReasoningExpanded;

    // "Processing prompt: 62% (ETA: 8s)", read off llama.cpp's own
    // prompt_progress streaming field (see MainViewModel's own comment on
    // BuildRawChatCompletionOptions/the streaming loop for the full
    // request/response mechanism). Only ever non-null before real generation
    // starts - cleared the moment answer or reasoning text arrives, and as a
    // final safety net if the stream ends (cancelled/errored) while still
    // mid-prompt-processing, so a stuck "43%" can never linger on the bubble.
    [ObservableProperty]
    private string? _promptProgressText;

    // Each entry's real success/failure (via CallId-correlated
    // FunctionCallContent/FunctionResultContent pairs in MainViewModel's
    // streaming loop), not just a plain tool name - see ToolCallResult's
    // own doc comment.
    public ObservableCollection<ToolCallResult> ToolCalls { get; } = [];

    // ObservableCollection<T> doesn't feed its own Count changes into a
    // separately-bound IsVisible (Avalonia's binding engine has no built-in
    // int-to-bool conversion for "ToolCalls.Count" either) - this listens
    // for CollectionChanged and republishes it as a real bool property the
    // chip row's IsVisible can bind to directly.
    public bool HasToolCalls => ToolCalls.Count > 0;

    // Compaction-visibility follow-up - app-level housekeeping notices
    // attached to this reply (currently just "compacting.../compacted...",
    // see MainViewModel.CompactHistoryIfNeededAsync). Kept separate from
    // ToolCalls (rendered with its own, visually distinct chip style) since
    // these aren't something the model did, and labeling them as a "tool
    // call" would misrepresent that. Same "silent behavior needs a visible
    // indicator" reasoning as the tool-call chips themselves.
    public ObservableCollection<string> SystemNotes { get; } = [];
    public bool HasSystemNotes => SystemNotes.Count > 0;

    // Set only on a user bubble that had a document attached this
    // turn; shown as a small filename chip rather than dumping the full
    // extracted text into the visible transcript (that goes to the model
    // only, via SendAsync's textForModel). Live-session-only, same as
    // ToolCalls - a reloaded session shows the combined text in the bubble
    // instead, which is still honest (it matches what was actually sent).
    [ObservableProperty]
    private string? _attachedFileName;

    // Set only on a user bubble that had an image attached this
    // turn (never together with AttachedFileName - a single attachment slot
    // holds one or the other). Unlike a document's filename chip, there's no
    // text-extraction equivalent for an image, so this is restored on
    // LoadSession too (see MainViewModel.LoadSession/
    // ImageAttachmentService.TryParseAttachedImageMarker), not
    // live-session-only - losing it on reload would mean losing it
    // entirely, not just a cosmetic label.
    [ObservableProperty]
    private string? _attachedImagePath;

    // A generated image's local path, parsed out of the
    // assistant's own reply text (ImageGenModule's tool result tells the
    // model to include real Markdown image syntax). NOT used to render the
    // image directly any more - Markdown.Avalonia already does that from
    // the reply text itself (the Markdown-rendering follow-up below); this
    // exists purely to power the Save/Copy image buttons, which need a real
    // local file path Markdown's own inline rendering doesn't expose.
    [ObservableProperty]
    private string? _generatedImagePath;

    // Plain live text while a reply is actively streaming, swapped for the
    // Markdown-rendered control once it's done (or loaded from history,
    // which never streams and so never sets this true at all). Re-parsing
    // the whole string into a fresh Markdown control tree on every one of a
    // long reply's many streamed chunks would be needlessly heavy compared
    // to the plain TextBox text assignment it replaces - the WPF
    // app makes the same trade-off with Markdig.Wpf.
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>When this message was actually sent/received (local time) - used by date dividers and the hover-timestamp. Set once at construction, not observable - a message's own send time never changes after the fact.</summary>
    public DateTime CreatedAt { get; }

    /// <summary>Non-null only on the first message of a chat, or the first one after the calendar day changed since the previous message - MainViewModel.SetDateDividerIfNeeded computes this once, right after the message is added, rather than the XAML template trying to compare against "the previous item" itself (an ItemsControl's DataTemplate has no clean way to do that).</summary>
    [ObservableProperty]
    private string? _dateDividerText;

    public ChatMessageViewModel(bool isUser, string initialText, DateTime? createdAt = null)
    {
        IsUser = isUser;
        Text = initialText;
        CreatedAt = createdAt ?? DateTime.Now;
        ToolCalls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasToolCalls));
        SystemNotes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSystemNotes));
    }
}
