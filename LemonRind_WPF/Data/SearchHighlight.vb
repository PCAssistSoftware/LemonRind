Imports System.Text.RegularExpressions

Namespace Data

    ''' <summary>One piece of a text, and whether it is a part the search matched.</summary>
    Public Structure HighlightPart

        Public ReadOnly Property Text As String
        Public ReadOnly Property IsMatch As Boolean

        Public Sub New(text As String, isMatch As Boolean)
            Me.Text = text
            Me.IsMatch = isMatch
        End Sub

    End Structure

    ''' <summary>One tag chip in the sidebar, and whether the current search matched it.</summary>
    Public Class TagChip

        Public ReadOnly Property Text As String
        Public ReadOnly Property IsMatch As Boolean

        Public Sub New(text As String, isMatch As Boolean)
            Me.Text = text
            Me.IsMatch = isMatch
        End Sub

    End Class

    ''' <summary>
    ''' Helpers for showing what a chat search matched: the title, a tag, or a few words of a message with the matched
    ''' words picked out. Matching is by the typed words, ignoring upper/lower case, the same as the Python edition.
    ''' </summary>
    Public NotInheritable Class SearchHighlight

        Private Sub New()
        End Sub

        ''' <summary>The characters SQLite's snippet() is told to put round each matched word.</summary>
        Public Shared ReadOnly SnippetStart As Char = ChrW(2)
        Public Shared ReadOnly SnippetEnd As Char = ChrW(3)

        Private Shared Function TypedWords(query As String) As List(Of String)
            ' longest first, so "hiking" wins over "hik"
            Return (If(query, "")).Split(CType(Nothing, Char()), StringSplitOptions.RemoveEmptyEntries).Distinct().OrderByDescending(Function(w) w.Length).ToList()
        End Function

        ''' <summary>
        ''' Splits text into pieces, flagging the ones that match a word the person typed. "Plan a hike" searched for
        ''' "hik" gives "Plan a ", "hik" (a match), "e". An empty query highlights nothing.
        ''' </summary>
        Public Shared Function HighlightParts(text As String, query As String) As List(Of HighlightPart)
            Dim result As New List(Of HighlightPart)
            If String.IsNullOrEmpty(text) Then Return result

            Dim words = TypedWords(query)
            If words.Count = 0 Then
                result.Add(New HighlightPart(text, False))
                Return result
            End If

            Dim pattern As New Regex(String.Join("|", words.Select(Function(w) Regex.Escape(w))), RegexOptions.IgnoreCase)
            Dim position = 0
            For Each found As Match In pattern.Matches(text)
                If found.Length = 0 Then Continue For
                If found.Index > position Then result.Add(New HighlightPart(text.Substring(position, found.Index - position), False))
                result.Add(New HighlightPart(found.Value, True))
                position = found.Index + found.Length
            Next
            If position < text.Length Then result.Add(New HighlightPart(text.Substring(position), False))
            Return result
        End Function

        ''' <summary>
        ''' A snippet of the text around the first word that starts with a typed word, in the same marker format SQLite's
        ''' snippet() gives. Used for the one case the database's version cannot be used: a message whose stored text
        ''' begins with an attached-picture marker (a file path) that must not be shown. Nothing when no word matches.
        ''' </summary>
        Public Shared Function SnippetFromText(text As String, query As String, Optional wordsAround As Integer = 8) As String
            Dim words = TypedWords(query)
            If words.Count = 0 OrElse String.IsNullOrEmpty(text) Then Return Nothing

            ' (?<!\w) means "not directly after a letter or digit"; a plain word boundary would not work for a typed
            ' word that starts with punctuation, such as "(5".
            Dim pattern As New Regex("(?<!\w)(?:" & String.Join("|", words.Select(Function(w) Regex.Escape(w))) & ")", RegexOptions.IgnoreCase)
            Dim tokens = text.Split(CType(Nothing, Char()), StringSplitOptions.RemoveEmptyEntries)
            Dim first = Array.FindIndex(tokens, Function(t) pattern.IsMatch(t))
            If first < 0 Then Return Nothing

            Dim startAt = Math.Max(0, first - wordsAround)
            Dim endAt = Math.Min(tokens.Length, first + wordsAround + 1)
            Dim window = String.Join(" ", tokens.Skip(startAt).Take(endAt - startAt))
            Dim marked = pattern.Replace(window, Function(m As Match) SnippetStart & m.Value & SnippetEnd)
            Return If(startAt > 0, "...", "") & marked & If(endAt < tokens.Length, "...", "")
        End Function

        ''' <summary>Turns a snippet from the database (matches wrapped in the start/end characters) into flagged pieces.</summary>
        Public Shared Function ParseSnippet(raw As String) As List(Of HighlightPart)
            Dim parts As New List(Of HighlightPart)
            If String.IsNullOrEmpty(raw) Then Return parts

            Dim chunks = raw.Split(SnippetStart)
            For i = 0 To chunks.Length - 1
                Dim chunk = chunks(i)
                Dim endIndex = If(i = 0, -1, chunk.IndexOf(SnippetEnd))
                If endIndex >= 0 Then
                    ' this chunk began at a start marker: the text before the end marker is the match
                    If endIndex > 0 Then parts.Add(New HighlightPart(chunk.Substring(0, endIndex), True))
                    If endIndex + 1 < chunk.Length Then parts.Add(New HighlightPart(chunk.Substring(endIndex + 1), False))
                ElseIf chunk.Length > 0 Then
                    parts.Add(New HighlightPart(chunk, False))
                End If
            Next
            Return parts
        End Function

        ''' <summary>Whether any typed word is found in the text (used to mark a tag chip the search matched).</summary>
        Public Shared Function Matches(text As String, query As String) As Boolean
            Return HighlightParts(text, query).Any(Function(p) p.IsMatch)
        End Function

    End Class

End Namespace
