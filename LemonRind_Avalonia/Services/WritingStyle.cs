using LemonRindAvalonia.Configuration;

namespace LemonRindAvalonia.Services;

/// <summary>
/// Turns Persona's writing-style choices into the short "Writing style:" block of the system prompt. Each choice is one
/// line, and anything left at Default or switched off adds nothing, so an unconfigured persona costs no tokens. A bare
/// word like "Warm" is open to interpretation, so each choice carries a short description for the model. A scheduled
/// job does not use this: its prompt is fixed, so a report never comes back in chat slang.
/// </summary>
public static class WritingStyle
{
    private static readonly Dictionary<string, string> ToneHints = new()
    {
        ["Casual"] = "relaxed and conversational, like talking to a friend",
        ["Formal"] = "professional, polite and precise",
        ["Warm"] = "friendly, kind and encouraging",
        ["Concise"] = "brisk and businesslike, with no small talk",
        ["Direct"] = "straightforward: say what matters first, with no padding or hedging",
        ["Chat-like"] = "short, informal messages, as if texting on a messaging app",
    };

    private static readonly Dictionary<string, string> LengthHints = new()
    {
        ["Concise"] = "keep answers short; give the essential answer and stop",
        ["Detailed"] = "give thorough answers with the reasoning and relevant detail",
        ["One paragraph"] = "answer in a single paragraph",
        ["One sentence"] = "answer in one sentence",
    };

    private static readonly Dictionary<string, string> EmojiHints = new()
    {
        ["None"] = "do not use emoji",
        ["Sparing"] = "use an emoji only occasionally, where it adds something",
        ["Frequent"] = "use emoji freely to add personality",
    };

    private static readonly Dictionary<string, string> VocabularyHints = new()
    {
        ["Simple"] = "use simple, everyday words and short sentences",
        ["Academic"] = "use precise, academic vocabulary",
    };

    /// <summary>The "Writing style:" block, or null when every choice is at its default (so the section is left out).</summary>
    public static string? Build(PersonaSettings persona)
    {
        var lines = new List<string>();
        if (ToneHints.TryGetValue(persona.Tone, out var tone)) lines.Add($"- Tone: {persona.Tone}: {tone}.");
        if (LengthHints.TryGetValue(persona.Verbosity, out var length)) lines.Add($"- Length: {persona.Verbosity}: {length}.");
        if (EmojiHints.TryGetValue(persona.EmojiUsage, out var emoji)) lines.Add($"- Emoji: {persona.EmojiUsage}: {emoji}.");
        if (VocabularyHints.TryGetValue(persona.Vocabulary, out var vocabulary)) lines.Add($"- Vocabulary: {persona.Vocabulary}: {vocabulary}.");

        var formatting = FormattingRule(persona);
        if (formatting.Length > 0) lines.Add($"- Formatting: {formatting}.");
        if (persona.NoFlattery) lines.Add("- Do not open with praise or agreement such as \"You're absolutely right\" or \"Great question\".");
        if (persona.NoEmDashes) lines.Add("- Do not use em dashes (the long dash); use commas or full stops instead.");
        if (!string.IsNullOrWhiteSpace(persona.CustomInstructions)) lines.Add($"- Also: {persona.CustomInstructions.Trim()}");

        return lines.Count == 0 ? null : "Writing style:\n" + string.Join("\n", lines);
    }

    /// <summary>One sentence about formatting, or "". "No Markdown" already covers headers, tables and lists.</summary>
    private static string FormattingRule(PersonaSettings persona)
    {
        if (persona.NoMarkdown) return "do not use Markdown (no bold, headers, tables or lists); write plain text";

        var parts = new List<string>();
        if (persona.NoHeaders) parts.Add("do not use headers");
        if (persona.NoTables) parts.Add("do not use tables");
        if (persona.NoLists) parts.Add("avoid bullet and numbered lists; write in sentences");
        return string.Join("; ", parts);
    }
}
