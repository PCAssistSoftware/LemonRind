Imports System.Globalization
Imports System.Windows
Imports System.Windows.Media
Imports LemonRind.Data

Namespace Controls

    ''' <summary>
    ''' The usage screen's chart, drawn directly (no chart package): stacked bars of tokens in and out per day, week or
    ''' month, or - with ShowSpeed - a line of tokens per second. A period with no replies leaves a gap in the bars and is
    ''' bridged by the speed line.
    ''' </summary>
    Public Class UsageChart
        Inherits FrameworkElement

        Public Shared ReadOnly BucketsProperty As DependencyProperty =
            DependencyProperty.Register("Buckets", GetType(IReadOnlyList(Of UsageBucket)), GetType(UsageChart),
                                        New FrameworkPropertyMetadata(Nothing, FrameworkPropertyMetadataOptions.AffectsRender))

        Public Shared ReadOnly ShowSpeedProperty As DependencyProperty =
            DependencyProperty.Register("ShowSpeed", GetType(Boolean), GetType(UsageChart),
                                        New FrameworkPropertyMetadata(False, FrameworkPropertyMetadataOptions.AffectsRender))

        Public Property Buckets As IReadOnlyList(Of UsageBucket)
            Get
                Return CType(GetValue(BucketsProperty), IReadOnlyList(Of UsageBucket))
            End Get
            Set(value As IReadOnlyList(Of UsageBucket))
                SetValue(BucketsProperty, value)
            End Set
        End Property

        ''' <summary>False: stacked token bars. True: the speed line.</summary>
        Public Property ShowSpeed As Boolean
            Get
                Return CBool(GetValue(ShowSpeedProperty))
            End Get
            Set(value As Boolean)
                SetValue(ShowSpeedProperty, value)
            End Set
        End Property

        Private Shared ReadOnly InBrush As Brush = Frozen(Color.FromRgb(&H19, &H76, &HD2))
        Private Shared ReadOnly OutBrush As Brush = Frozen(Color.FromRgb(&H26, &HA6, &H9A))
        Private Shared ReadOnly SpeedBrush As Brush = Frozen(Color.FromRgb(&HF4, &HA3, &H0))
        Private Shared ReadOnly MutedBrush As Brush = Frozen(Color.FromRgb(&H5B, &H64, &H72))

        Private Shared Function Frozen(color As Color) As Brush
            Dim brush As New SolidColorBrush(color)
            brush.Freeze()
            Return brush
        End Function

        Protected Overrides Sub OnRender(drawingContext As DrawingContext)
            MyBase.OnRender(drawingContext)

            Dim data = Buckets
            If data Is Nothing OrElse data.Count = 0 OrElse ActualWidth < 120 OrElse ActualHeight < 60 Then Return

            Const left As Double = 56, right As Double = 12, top As Double = 10, bottom As Double = 24
            Dim plotWidth = ActualWidth - left - right
            Dim plotHeight = ActualHeight - top - bottom
            Dim gridPen As New Pen(New SolidColorBrush(Color.FromArgb(70, 127, 127, 127)), 1)
            gridPen.Freeze()

            Dim maximum = NiceMax(If(ShowSpeed, data.Max(Function(b) b.TokensPerSecond), data.Max(Function(b) CDbl(b.InputTokens + b.OutputTokens))))

            For Each tick In {0.0, 0.5, 1.0}
                Dim y = top + plotHeight * (1 - tick)
                drawingContext.DrawLine(gridPen, New Point(left, y), New Point(left + plotWidth, y))
                Dim label = If(ShowSpeed, (maximum * tick).ToString("0", CultureInfo.CurrentCulture), UsageReports.Compact(maximum * tick))
                DrawLabel(drawingContext, label, left - 6, y - 7, rightAligned:=True)
            Next

            Dim slot = plotWidth / data.Count
            Dim labelStep = Math.Max(1, CInt(Math.Ceiling(data.Count / 8.0)))
            Dim points As New List(Of Point)

            For i = 0 To data.Count - 1
                Dim bucket = data(i)
                Dim centre = left + slot * i + slot / 2

                If ShowSpeed Then
                    If bucket.TokensPerSecond > 0 Then points.Add(New Point(centre, top + plotHeight * (1 - bucket.TokensPerSecond / maximum)))
                Else
                    Dim barWidth = Math.Max(2.0, slot * 0.7)
                    Dim x = centre - barWidth / 2
                    Dim inHeight = plotHeight * bucket.InputTokens / maximum
                    Dim outHeight = plotHeight * bucket.OutputTokens / maximum
                    If inHeight > 0 Then drawingContext.DrawRectangle(InBrush, Nothing, New Rect(x, top + plotHeight - inHeight, barWidth, inHeight))
                    If outHeight > 0 Then drawingContext.DrawRectangle(OutBrush, Nothing, New Rect(x, top + plotHeight - inHeight - outHeight, barWidth, outHeight))
                End If

                If i Mod labelStep = 0 Then DrawLabel(drawingContext, bucket.Label, centre, top + plotHeight + 5, centred:=True)
            Next

            If ShowSpeed Then
                Dim linePen As New Pen(SpeedBrush, 2)
                For i = 1 To points.Count - 1
                    drawingContext.DrawLine(linePen, points(i - 1), points(i))
                Next
                For Each point In points
                    drawingContext.DrawEllipse(SpeedBrush, Nothing, point, 3.5, 3.5)
                Next
            End If
        End Sub

        Private Sub DrawLabel(context As DrawingContext, text As String, x As Double, y As Double, Optional rightAligned As Boolean = False, Optional centred As Boolean = False)
            Dim formatted As New FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, New Typeface("Segoe UI"), 11, MutedBrush, VisualTreeHelper.GetDpi(Me).PixelsPerDip)
            Dim originX = If(rightAligned, x - formatted.Width, If(centred, x - formatted.Width / 2, x))
            context.DrawText(formatted, New Point(originX, y))
        End Sub

        ''' <summary>A round number at or above the largest value, so the chart's top line reads sensibly.</summary>
        Private Shared Function NiceMax(value As Double) As Double
            If value <= 0 Then Return 1
            Dim magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)))
            For Each stepSize In {1.0, 2.0, 2.5, 5.0, 10.0}
                If value <= stepSize * magnitude Then Return stepSize * magnitude
            Next
            Return 10 * magnitude
        End Function

    End Class

End Namespace
