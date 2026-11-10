Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' Turns Persona's writing-style choices into the short "Writing style:" block of the system prompt. Each choice is one
    ''' line, and anything left at Default or switched off adds nothing, so an unconfigured persona costs no tokens. A bare
    ''' word like "Warm" is open to interpretation, so each choice carries a short description for the model. A scheduled
    ''' job does not use this: its prompt is fixed, so a report never comes back in chat slang.
    ''' </summary>
    Public NotInheritable Class WritingStyle

        Private Sub New()
        End Sub

        Private Shared ReadOnly ToneHints As New Dictionary(Of String, String) From {
            {"Casual", "relaxed and conversational, like talking to a friend"},
            {"Formal", "professional, polite and precise"},
            {"Warm", "friendly, kind and encouraging"},
            {"Concise", "brisk and businesslike, with no small talk"},
            {"Direct", "straightforward: say what matters first, with no padding or hedging"},
            {"Chat-like", "short, informal messages, as if texting on a messaging app"}
        }

        Private Shared ReadOnly LengthHints As New Dictionary(Of String, String) From {
            {"Concise", "keep answers short; give the essential answer and stop"},
            {"Detailed", "give thorough answers with the reasoning and relevant detail"},
            {"One paragraph", "answer in a single paragraph"},
            {"One sentence", "answer in one sentence"}
        }

        Private Shared ReadOnly EmojiHints As New Dictionary(Of String, String) From {
            {"None", "do not use emoji"},
            {"Sparing", "use an emoji only occasionally, where it adds something"},
            {"Frequent", "use emoji freely to add personality"}
        }

        Private Shared ReadOnly VocabularyHints As New Dictionary(Of String, String) From {
            {"Simple", "use simple, everyday words and short sentences"},
            {"Academic", "use precise, academic vocabulary"}
        }

        ''' <summary>The "Writing style:" block, or Nothing when every choice is at its default (so the section is left out).</summary>
        Public Shared Function Build(persona As PersonaSettings) As String
            Dim lines As New List(Of String)
            Dim hint As String = Nothing

            If ToneHints.TryGetValue(If(persona.Tone, ""), hint) Then lines.Add($"- Tone: {persona.Tone}: {hint}.")
            If LengthHints.TryGetValue(If(persona.Verbosity, ""), hint) Then lines.Add($"- Length: {persona.Verbosity}: {hint}.")
            If EmojiHints.TryGetValue(If(persona.EmojiUsage, ""), hint) Then lines.Add($"- Emoji: {persona.EmojiUsage}: {hint}.")
            If VocabularyHints.TryGetValue(If(persona.Vocabulary, ""), hint) Then lines.Add($"- Vocabulary: {persona.Vocabulary}: {hint}.")

            Dim formatting = FormattingRule(persona)
            If formatting.Length > 0 Then lines.Add($"- Formatting: {formatting}.")
            If persona.NoFlattery Then lines.Add("- Do not open with praise or agreement such as ""You're absolutely right"" or ""Great question"".")
            If persona.NoEmDashes Then lines.Add("- Do not use em dashes (the long dash); use commas or full stops instead.")
            If Not String.IsNullOrWhiteSpace(persona.CustomInstructions) Then lines.Add($"- Also: {persona.CustomInstructions.Trim()}")

            If lines.Count = 0 Then Return Nothing
            Return "Writing style:" & vbLf & String.Join(vbLf, lines)
        End Function

        ''' <summary>One sentence about formatting, or "". "No Markdown" already covers headers, tables and lists.</summary>
        Private Shared Function FormattingRule(persona As PersonaSettings) As String
            If persona.NoMarkdown Then Return "do not use Markdown (no bold, headers, tables or lists); write plain text"

            Dim parts As New List(Of String)
            If persona.NoHeaders Then parts.Add("do not use headers")
            If persona.NoTables Then parts.Add("do not use tables")
            If persona.NoLists Then parts.Add("avoid bullet and numbered lists; write in sentences")
            Return String.Join("; ", parts)
        End Function

    End Class

End Namespace
