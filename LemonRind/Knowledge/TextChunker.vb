Namespace Knowledge

    ''' <summary>
    ''' Word-count chunking with overlap (1000 words/200 overlap), not
    ''' token-aware - good enough for now, revisit (e.g. with
    ''' Microsoft.ML.Tokenizers) if chunk-size-vs-real-token-count ever
    ''' actually causes a problem.
    ''' </summary>
    Public Module TextChunker

        Private Const WordsPerChunk = 1000
        Private Const OverlapWords = 200

        ''' <summary>Splits text into overlapping chunks. A short text (under one chunk's worth of words) comes back as a single chunk, unsplit.</summary>
        Public Function ChunkText(text As String) As List(Of String)
            Dim words = text.Split({" "c, Environment.NewLine, vbTab}, StringSplitOptions.RemoveEmptyEntries)
            If words.Length = 0 Then Return New List(Of String)

            Dim chunks As New List(Of String)
            Dim startIndex = 0
            Dim stepSize = WordsPerChunk - OverlapWords

            While startIndex < words.Length
                Dim chunkWords = words.Skip(startIndex).Take(WordsPerChunk)
                chunks.Add(String.Join(" ", chunkWords))

                If startIndex + WordsPerChunk >= words.Length Then Exit While
                startIndex += stepSize
            End While

            Return chunks
        End Function

    End Module

End Namespace
