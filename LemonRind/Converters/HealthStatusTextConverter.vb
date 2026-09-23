Imports System.Globalization
Imports System.Windows.Data

Namespace Converters

    ''' <summary>True/False -&gt; "healthy"/"unreachable", for the header's Lemonade status label next to the health dot.</summary>
    Public Class HealthStatusTextConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Boolean AndAlso CBool(value) Then
                Return "healthy"
            End If
            Return "unreachable"
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
