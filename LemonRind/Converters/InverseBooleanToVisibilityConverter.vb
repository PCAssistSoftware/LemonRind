Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

Namespace Converters

    ''' <summary>
    ''' True -&gt; Collapsed, False -&gt; Visible - the opposite of WPF's built-in
    ''' BooleanToVisibilityConverter. Used for "show this element only while
    ''' NOT editing" bindings (see the sessions sidebar's rename UI).
    ''' </summary>
    Public Class InverseBooleanToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is Boolean AndAlso CBool(value) Then
                Return Visibility.Collapsed
            End If
            Return Visibility.Visible
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
