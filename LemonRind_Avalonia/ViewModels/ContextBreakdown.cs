namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// The context-usage hover popup's content (MainWindow.axaml,
/// MainViewModel.RefreshContextBreakdownAsync) - deliberately only ONE
/// "total" concept (Context now, built from MainViewModel.
/// ContextUsageCurrent/Max, the real number Lemonade itself last reported),
/// not two. The real WPF app's own earlier version also showed a second,
/// independently-estimated "next turn total" next to it, which could
/// visibly disagree with the real one (Lemonade's chat-template rendering -
/// per-message role markers, the tool-calling JSON wrapper - adds real
/// tokens a plain-text tokenize call can never see, so the estimate
/// structurally ran low) - confirmed there this read as a straightforward
/// contradiction no amount of explaining fixed, and a forced-to-agree fudge
/// number was worse. "If you send now" here is deliberately an ADDITION on
/// top of the one real total, not a competing total of its own -
/// PendingTokens is the real, tokenized cost of the live turn-context
/// preview (time-awareness/memory/knowledge for the message box's current
/// text) plus the message box's own text, which is genuinely knowable
/// without sending, unlike the full templated request size.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// ViewModels\ContextBreakdown.vb.
/// </summary>
public class ContextBreakdown
{
    /// <summary>The composition breakdown below is proportionally scaled to sum to exactly this - MainViewModel.ContextUsageCurrent at the moment of refresh, always fresh (never stale), so this is never a fudge, just normalizing independently-tokenized parts against the one number known to be real.</summary>
    public int ContextNowTokens { get; set; }

    /// <summary>Rounded whole percent, ContextNowTokens / max context - identical to MainViewModel.ContextUsageText's own percent, shown here too since the popup doesn't otherwise repeat that text.</summary>
    public int PercentFull { get; set; }

    /// <summary>False only if Lemonade's POST /v1/tokenize failed this refresh (older Lemonade version, or a transient network issue) and the composition breakdown fell back to a chars/4 guess - drives the popup's caption text.</summary>
    public bool UsedRealTokenizer { get; set; }

    public int SystemPromptTokens { get; set; }
    public double SystemPromptWidth { get; set; }

    public int ToolsActiveCount { get; set; }
    public int ToolsTokens { get; set; }
    public double ToolsWidth { get; set; }

    public int HistoryTokens { get; set; }
    public double HistoryWidth { get; set; }

    /// <summary>Whatever's left of the bar's fixed pixel width once the three segments above are drawn - always ~0, since they're scaled to sum to ContextNowTokens exactly, but kept as a real computed value (not assumed 0) in case of rounding.</summary>
    public double FreeWidth { get; set; }

    /// <summary>Pixel offset (from the bar's left edge) of the compaction-trigger line - AppSettings.Memory.CompactionTriggerPercent translated into this popup's fixed bar width.</summary>
    public double CompactionThresholdLeft { get; set; }

    /// <summary>How many times this session has actually been compacted - 0 for a fresh/short chat, shown as a small badge only when > 0.</summary>
    public int CompactionCount { get; set; }

    /// <summary>CompactionCount > 0, precomputed so the popup's badge IsVisible binding doesn't need a dedicated "count to visible" converter for a single use.</summary>
    public bool HasCompactions { get; set; }

    /// <summary>Real tokenized cost of the live turn-context preview (time-awareness/memory/knowledge for the message box's CURRENT text - not stale leftover data from the last send) plus the message box's own text. 0 with an empty message box (nothing to preview yet).</summary>
    public int PendingTokens { get; set; }

    /// <summary>ContextNowTokens + PendingTokens >= max context - a direct, practical "this won't fit" warning, the actual reason this preview exists.</summary>
    public bool WouldExceedLimit { get; set; }
}
