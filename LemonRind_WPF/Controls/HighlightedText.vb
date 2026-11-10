Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Documents
Imports System.Windows.Media
Imports LemonRind.Data

Namespace Controls

    ''' <summary>
    ''' Lets a TextBlock draw a list of pieces of text, the ones the chat search matched with a coloured background. A
    ''' TextBlock's own Inlines cannot be bound to, so this attached property builds them whenever it changes.
    ''' Usage: &lt;TextBlock controls:HighlightedText.Parts="{Binding TitleParts}" /&gt;.
    ''' </summary>
    Public NotInheritable Class HighlightedText

        Private Sub New()
        End Sub

        Private Shared ReadOnly MatchBrush As Brush = CreateMatchBrush()

        Private Shared Function CreateMatchBrush() As Brush
            Dim brush As New SolidColorBrush(Color.FromRgb(&HFF, &HE5, &H8F))
            brush.Freeze()
            Return brush
        End Function

        Public Shared ReadOnly PartsProperty As DependencyProperty =
            DependencyProperty.RegisterAttached("Parts", GetType(IEnumerable(Of HighlightPart)), GetType(HighlightedText),
                                                New PropertyMetadata(Nothing, AddressOf OnPartsChanged))

        Public Shared Function GetParts(element As DependencyObject) As IEnumerable(Of HighlightPart)
            Return CType(element.GetValue(PartsProperty), IEnumerable(Of HighlightPart))
        End Function

        Public Shared Sub SetParts(element As DependencyObject, value As IEnumerable(Of HighlightPart))
            element.SetValue(PartsProperty, value)
        End Sub

        Private Shared Sub OnPartsChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            Dim block = TryCast(d, TextBlock)
            If block Is Nothing Then Return

            block.Inlines.Clear()
            Dim parts = TryCast(e.NewValue, IEnumerable(Of HighlightPart))
            If parts Is Nothing Then Return

            For Each part In parts
                Dim run As New Run(part.Text)
                If part.IsMatch Then run.Background = MatchBrush
                block.Inlines.Add(run)
            Next
        End Sub

    End Class

End Namespace
