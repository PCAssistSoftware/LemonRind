Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

Namespace Converters

    ''' <summary>
    ''' Visible when the bound count is 0, Collapsed otherwise - drives an
    ''' "empty list" placeholder message (e.g. "No pinned facts yet.") without
    ''' a separate IsEmpty bool property to keep in sync with the collection.
    ''' </summary>
    Public Class CountToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Integer AndAlso CInt(value) = 0 Then
                Return Visibility.Visible
            End If
            Return Visibility.Collapsed
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
