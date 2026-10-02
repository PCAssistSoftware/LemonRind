Imports System.Globalization
Imports System.Windows.Data

Namespace Converters

    ''' <summary>
    ''' Flips a bound Boolean - used for "IsEnabled = NOT IsBusy" style
    ''' bindings, since WPF bindings can't negate a value inline in XAML.
    ''' </summary>
    Public Class InverseBooleanConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Return Not CBool(value)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return Not CBool(value)
        End Function

    End Class

End Namespace
