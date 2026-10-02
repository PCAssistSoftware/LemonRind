Imports System.Globalization
Imports System.Windows.Data
Imports System.Windows.Media

Namespace Converters

    ''' <summary>
    ''' True/False -&gt; green/red brush, for the Lemonade health-status dot.
    ''' </summary>
    Public Class BooleanToHealthBrushConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Boolean AndAlso CBool(value) Then
                Return Brushes.MediumSeaGreen
            End If
            Return Brushes.IndianRed
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
