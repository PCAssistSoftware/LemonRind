Imports System.Linq

Namespace ViewModels

    ''' <summary>
    ''' One entry in the main model selector dropdown. Id is the real model
    ''' name sent to Lemonade; Category groups the dropdown by capability,
    ''' derived from Lemonade's own /v1/models "labels" field
    ''' ("chat"/"image"/"embeddings" are real label values it returns).
    ''' This distinction matters, not just cosmetically: selecting an
    ''' image-only model as the active chat model produces a confusing HTTP
    ''' 400 from Lemonade ("This model does not support chat completion")
    ''' with no indication in the UI beforehand that the selected model
    ''' couldn't do that. CategorySortOrder controls which group appears
    ''' first in the dropdown - AvailableModels is populated already sorted
    ''' by this, since WPF's PropertyGroupDescription groups in
    ''' first-encountered order rather than alphabetically.
    ''' </summary>
    Public Class ModelSelectorItem
        Public Const ChatCategory As String = "Chat models"
        Public Const ImageCategory As String = "Image models"
        Public Const EmbeddingCategory As String = "Embedding models"
        Public Const OtherCategory As String = "Other models"

        Public Property Id As String
        Public Property Category As String
        Public Property CategorySortOrder As Integer

        Public Shared Function FromLabels(id As String, labels As IEnumerable(Of String)) As ModelSelectorItem
            Dim labelSet As New HashSet(Of String)(If(labels, Enumerable.Empty(Of String)()), StringComparer.OrdinalIgnoreCase)

            Dim category As String
            Dim sortOrder As Integer
            If labelSet.Contains("chat") Then
                category = ChatCategory : sortOrder = 0
            ElseIf labelSet.Contains("image") Then
                category = ImageCategory : sortOrder = 1
            ElseIf labelSet.Contains("embeddings") Then
                category = EmbeddingCategory : sortOrder = 2
            Else
                category = OtherCategory : sortOrder = 3
            End If

            Return New ModelSelectorItem With {.Id = id, .Category = category, .CategorySortOrder = sortOrder}
        End Function

    End Class

End Namespace
