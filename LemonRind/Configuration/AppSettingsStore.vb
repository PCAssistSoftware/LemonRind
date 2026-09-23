Imports System.IO
Imports System.Text.Json

Namespace Configuration

    ''' <summary>
    ''' Reads/writes the app's real settings file - separate from the
    ''' AppSettings singleton the rest of the app already has via DI (see
    ''' Application.xaml.vb), because that singleton is shared by
    ''' already-constructed services (the Lemonade HttpClient's BaseAddress,
    ''' the loaded module list's IsEnabled flags, ...) that captured its
    ''' values once at startup and won't notice an in-place mutation.
    '''
    ''' The Settings screen intentionally works on its own freshly-loaded copy
    ''' and asks for a restart after saving, rather than trying to hot-reload
    ''' those already-built services - a real "apply without restarting" would
    ''' mean rebuilding the chat client, module list, etc. live, which is a
    ''' meaningfully bigger feature than "a basic Settings screen" and isn't
    ''' being attempted here.
    '''
    ''' Deliberately lives at "data\appsettings.json", not loose next to the
    ''' .exe - real user settings (API keys, Persona, ...) belong in the same
    ''' portable data folder as the database/workspace, not somewhere a
    ''' bin/obj clean can destroy independently of it. This is NOT resolved
    ''' via AppData.DataFolder (which is itself a field *inside* this file) -
    ''' that would be a bootstrap chicken-and-egg problem. A user who
    ''' redirects DataFolder elsewhere via Settings still gets their
    ''' database/workspace moved there; this file itself stays at the fixed,
    ''' always-known "data\" next to the exe so there's a guaranteed place to
    ''' find it on startup.
    ''' </summary>
    Public Class AppSettingsStore

        Private ReadOnly _filePath As String

        Public Sub New()
            _filePath = Path.Combine(AppContext.BaseDirectory, "data", "appsettings.json")
        End Sub

        ''' <summary>
        ''' First run (or a fresh clone with no data folder yet): no file
        ''' exists, so a brand new AppSettings() - whose own property
        ''' defaults are already generic and safe (see AppSettings.vb) - is
        ''' created, saved immediately so it exists on disk from here on, and
        ''' returned. No separate checked-in template file is needed.
        ''' </summary>
        Public Function LoadFresh() As AppSettings
            If Not File.Exists(_filePath) Then
                Dim fresh As New AppSettings()
                Save(fresh)
                Return fresh
            End If

            Dim json = File.ReadAllText(_filePath)
            Dim settings = JsonSerializer.Deserialize(Of AppSettings)(json)
            Return If(settings, New AppSettings())
        End Function

        Public Sub Save(settings As AppSettings)
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath))
            Dim options As New JsonSerializerOptions With {.WriteIndented = True}
            Dim json = JsonSerializer.Serialize(settings, options)
            File.WriteAllText(_filePath, json)
        End Sub

    End Class

End Namespace
