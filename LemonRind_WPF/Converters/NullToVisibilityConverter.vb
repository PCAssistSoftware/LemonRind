Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

Namespace Converters

    ''' <summary>
    ''' Visible when the bound value is a non-null, non-empty string (or any
    ''' non-null object), Collapsed otherwise - drives the attached-file chip
    ''' (MainWindow.xaml), which should only show once a file's actually been
    ''' picked (MainViewModel.AttachedFileName is Nothing until then).
    ''' </summary>
    Public Class NullToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing Then Return Visibility.Collapsed
            If TypeOf value Is String AndAlso String.IsNullOrEmpty(CStr(value)) Then Return Visibility.Collapsed
            Return Visibility.Visible
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
