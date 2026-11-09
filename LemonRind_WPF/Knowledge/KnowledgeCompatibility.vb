Imports System.Threading

Namespace Knowledge

    ''' <summary>Raised when a knowledge base was built with a different embedding model than the one in use now.</summary>
    Public Class KnowledgeModelMismatchException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Whether a knowledge base can be used with the embedding model in use now. A knowledge base is a pile of
    ''' vectors (the numbers an embedding model makes from each piece of text), and vectors from two different
    ''' models cannot be compared: searching one with the other gives meaningless results, or none. So each
    ''' knowledge base remembers the model that built it, and a different model is refused until the knowledge
    ''' base is re-embedded (its stored text is embedded again with the current model).
    ''' </summary>
    Public Module KnowledgeCompatibility

        ' The knowledge bases being re-embedded right now (by id) - searching or adding to one meanwhile is refused.
        Private ReadOnly Reembedding As New HashSet(Of String)()
        Private ReadOnly Gate As New Object()

        Public Function TryStartReembedding(knowledgeBaseId As String) As Boolean
            SyncLock Gate
                Return Reembedding.Add(knowledgeBaseId)
            End SyncLock
        End Function

        Public Sub FinishReembedding(knowledgeBaseId As String)
            SyncLock Gate
                Reembedding.Remove(knowledgeBaseId)
            End SyncLock
        End Sub

        Public Function IsReembedding(knowledgeBaseId As String) As Boolean
            SyncLock Gate
                Return Reembedding.Contains(knowledgeBaseId)
            End SyncLock
        End Function

        Public Function MismatchMessage(kb As KnowledgeBaseSummary, currentModel As String) As String
            Return $"The knowledge base '{kb.Name}' was built with the embedding model '{kb.EmbeddingModel}', but '{currentModel}' is in use now. " &
                "Their numbers cannot be compared. Re-embed the knowledge base (Settings > Knowledge Bases) to use it with the current model."
        End Function

        ''' <summary>
        ''' Why this knowledge base cannot be used with the current embedding model, or "" if it can. A knowledge base whose
        ''' model is not known (built before it was recorded) is allowed: the size of its vectors is compared instead, when
        ''' something is searched or added, which catches the usual mismatch.
        ''' </summary>
        Public Function Check(kb As KnowledgeBaseSummary, currentModel As String) As String
            If IsReembedding(kb.Id) Then Return "It is being re-embedded at the moment, which takes a while."
            If Not String.IsNullOrWhiteSpace(currentModel) AndAlso
               Not String.IsNullOrWhiteSpace(kb.EmbeddingModel) AndAlso
               Not String.Equals(kb.EmbeddingModel, currentModel, StringComparison.OrdinalIgnoreCase) Then
                Return MismatchMessage(kb, currentModel)
            End If
            Return ""
        End Function

        Public Function DimensionMessage(kb As KnowledgeBaseSummary, storedSize As Integer, currentSize As Integer) As String
            Return $"The vectors in the knowledge base '{kb.Name}' have {storedSize} numbers each, but the current embedding model makes {currentSize}. " &
                "Re-embed the knowledge base (Settings > Knowledge Bases) to use it with the current model."
        End Function

    End Module

End Namespace
