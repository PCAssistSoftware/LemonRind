using System.Text;
using Microsoft.Extensions.AI;
using LemonRindBlazor.Data;

namespace LemonRindBlazor.Services;

/// <summary>
/// Turns one session's full stored history into a single Markdown document.
/// Static, like FileTextExtractor - reads straight from StoredChatMessage
/// rows rather than the live Messages collection, so a sidebar session can
/// be exported even if it isn't the one currently open.
///
/// Ported from the VB.NET/WPF LemonRind app's Services\ChatMarkdownExporter.vb.
/// </summary>
public static class ChatMarkdownExporter
{
    /// <summary>Reasoning and tool-call chips are included, each clearly labeled and visually separated from the actual reply - collapsible &lt;details&gt; for thinking (present, but out of the way by default), a bold tool-use line right before the answer.</summary>
    public static string BuildMarkdown(string sessionTitle, IEnumerable<StoredChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {sessionTitle}");
        builder.AppendLine();
        builder.AppendLine($"*Exported {DateTime.Now:dd/MM/yyyy HH:mm}*");
        builder.AppendLine();

        foreach (var message in messages)
        {
            var roleLabel = message.Role == ChatRole.User.Value ? "You" : "Assistant";

            builder.AppendLine("---");
            builder.AppendLine();
            builder.AppendLine($"**{roleLabel}** · {message.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
            builder.AppendLine();

            if (!string.IsNullOrEmpty(message.ToolCalls))
            {
                builder.AppendLine($"🔧 *Tools used: {message.ToolCalls.Replace(",", ", ")}*");
                builder.AppendLine();
            }

            if (!string.IsNullOrEmpty(message.ReasoningContent))
            {
                builder.AppendLine("<details>");
                builder.AppendLine("<summary>Thinking</summary>");
                builder.AppendLine();
                builder.AppendLine(message.ReasoningContent);
                builder.AppendLine();
                builder.AppendLine("</details>");
                builder.AppendLine();
            }

            // An attached image's path marker becomes a real Markdown image
            // link (same file:// convention generated images already use)
            // rather than showing the raw "[Attached image: ...]" text - a
            // Markdown viewer that resolves local file links (VS Code,
            // Obsidian, ...) will actually display the photo inline.
            var content = message.Content;
            var (imagePath, remainingText) = ImageAttachmentService.TryParseAttachedImageMarker(content);
            if (imagePath is not null)
            {
                builder.AppendLine($"![attached image]({new Uri(imagePath).AbsoluteUri})");
                builder.AppendLine();
                content = remainingText;
            }

            builder.AppendLine(content);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
