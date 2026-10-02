Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

Namespace Converters

    ''' <summary>
    ''' Visible if the bound value (e.g. SettingsViewModel.SelectedSection)
    ''' equals the ConverterParameter (a section name), Collapsed otherwise -
    ''' drives which content panel shows in the Settings screen's nav+content
    ''' layout without a separate bool property per section.
    ''' </summary>
    Public Class SectionVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            If TypeOf value Is String AndAlso TypeOf parameter Is String AndAlso CStr(value) = CStr(parameter) Then
                Return Visibility.Visible
            End If
            Return Visibility.Collapsed
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

    End Class

End Namespace
