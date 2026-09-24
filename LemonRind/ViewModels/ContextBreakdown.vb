Namespace ViewModels

    ''' <summary>
    ''' The context-usage hover popup's content (MainWindow.xaml,
    ''' MainViewModel.RefreshContextBreakdownAsync) - deliberately only ONE
    ''' "total" concept (Context now, built from MainViewModel.
    ''' ContextUsageCurrent/Max, the real number Lemonade itself last
    ''' reported), not two. An earlier version also showed a second,
    ''' independently-estimated "next turn total" next to it, which could
    ''' visibly disagree with the real one (Lemonade's chat-template
    ''' rendering - per-message role markers, the tool-calling JSON
    ''' wrapper - adds real tokens that a plain-text tokenize call can
    ''' never see, so the estimate structurally ran low) - confirmed live
    ''' this read as a straightforward contradiction no amount of
    ''' explaining fixed, and a forced-to-agree fudge number was worse.
    ''' "If you send now" here is deliberately an ADDITION on top of the
    ''' one real total, not a competing total of its own - PendingTokens is
    ''' the real, tokenized cost of the live turn-context preview (time-
    ''' awareness/memory/knowledge for the message box's current text) plus
    ''' the message box's own text, which is genuinely knowable without
    ''' sending, unlike the full templated request size.
    ''' </summary>
    Public Class ContextBreakdown

        ''' <summary>The composition breakdown below is proportionally scaled to sum to exactly this - MainViewModel.ContextUsageCurrent at the moment of refresh, always fresh (never stale), so this is never a fudge, just normalizing independently-tokenized parts against the one number known to be real.</summary>
        Public Property ContextNowTokens As Integer

        ''' <summary>Rounded whole percent, ContextNowTokens / max context - identical to MainViewModel.ContextUsageText's own percent, shown here too since the popup doesn't otherwise repeat that text.</summary>
        Public Property PercentFull As Integer

        ''' <summary>False only if Lemonade's POST /v1/tokenize failed this refresh (older Lemonade version, or a transient network issue) and the composition breakdown fell back to a chars/4 guess - drives the popup's caption text.</summary>
        Public Property UsedRealTokenizer As Boolean

        Public Property SystemPromptTokens As Integer
        Public Property SystemPromptWidth As Double

        Public Property ToolsActiveCount As Integer
        Public Property ToolsTokens As Integer
        Public Property ToolsWidth As Double

        Public Property HistoryTokens As Integer
        Public Property HistoryWidth As Double

        ''' <summary>Whatever's left of the bar's fixed pixel width once the three segments above are drawn - always ~0, since they're scaled to sum to ContextNowTokens exactly, but kept as a real computed value (not assumed 0) in case of rounding.</summary>
        Public Property FreeWidth As Double

        ''' <summary>Pixel offset (from the bar's left edge) of the compaction-trigger line - AppSettings.Memory.CompactionTriggerPercent translated into this popup's fixed bar width.</summary>
        Public Property CompactionThresholdLeft As Double

        ''' <summary>How many times this session has actually been compacted - 0 for a fresh/short chat, shown as a small badge only when &gt; 0.</summary>
        Public Property CompactionCount As Integer

        ''' <summary>CompactionCount &gt; 0, precomputed so the popup's badge Visibility binding doesn't need a dedicated "count to visibility" converter for a single use.</summary>
        Public Property HasCompactions As Boolean

        ''' <summary>Real tokenized cost of the live turn-context preview (time-awareness/memory/knowledge for the message box's CURRENT text - not stale leftover data from the last send) plus the message box's own text. 0 with an empty message box (nothing to preview yet).</summary>
        Public Property PendingTokens As Integer

        ''' <summary>ContextNowTokens + PendingTokens &gt;= max context - a direct, practical "this won't fit" warning, the actual reason this preview exists.</summary>
        Public Property WouldExceedLimit As Boolean

    End Class

End Namespace
