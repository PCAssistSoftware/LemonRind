Imports System.IO

Namespace Services

    ''' <summary>
    ''' What is using the space in the data folder, for Settings > Storage: the
    ''' database (with its two side files), the generated and attached pictures,
    ''' other workspace files, the settings file, and anything else, adding up to
    ''' the folder's total.
    ''' </summary>
    Public Module StorageUsage

        ''' <summary>One line per part, then the total: "Database (chats, memories, knowledge bases, jobs): 2.1 MB".</summary>
        Public Function BuildLines(dataFolder As String) As List(Of String)
            Dim lines As New List(Of String)()
            If Not Directory.Exists(dataFolder) Then
                lines.Add("The data folder does not exist yet.")
                Return lines
            End If

            Dim database = FileSize(Path.Combine(dataFolder, "lemonrind.db")) +
                FileSize(Path.Combine(dataFolder, "lemonrind.db-wal")) +
                FileSize(Path.Combine(dataFolder, "lemonrind.db-shm"))
            Dim settings = FileSize(Path.Combine(dataFolder, "appsettings.json"))
            Dim workspace = Path.Combine(dataFolder, "Workspace")
            Dim generated = FolderSize(Path.Combine(workspace, "GeneratedImages"))
            Dim attached = FolderSize(Path.Combine(workspace, "AttachedImages"))
            Dim workspaceFiles = Math.Max(0L, FolderSize(workspace) - generated - attached)
            Dim total = FolderSize(dataFolder)
            Dim other = Math.Max(0L, total - database - settings - generated - attached - workspaceFiles)

            ' The database is always listed; the others only when there is something in them.
            lines.Add($"Database (chats, memories, knowledge bases, jobs): {FormatSize(database)}")
            If generated > 0 Then lines.Add($"Generated pictures: {FormatSize(generated)}")
            If attached > 0 Then lines.Add($"Attached pictures: {FormatSize(attached)}")
            If workspaceFiles > 0 Then lines.Add($"Other workspace files: {FormatSize(workspaceFiles)}")
            If settings > 0 Then lines.Add($"Settings file: {FormatSize(settings)}")
            If other > 0 Then lines.Add($"Other: {FormatSize(other)}")
            lines.Add($"Total: {FormatSize(total)}")
            Return lines
        End Function

        ''' <summary>
        ''' A size the way the computer's own file manager shows it: Windows counts
        ''' 1 KB as 1,024 bytes, other systems as 1,000, so the numbers match what
        ''' you see there.
        ''' </summary>
        Public Function FormatSize(bytes As Long) As String
            Dim [step] = If(OperatingSystem.IsWindows(), 1024.0, 1000.0)
            Dim units = {"B", "KB", "MB", "GB", "TB"}
            Dim value = CDbl(bytes)
            Dim unit = 0
            While value >= [step] AndAlso unit < units.Length - 1
                value /= [step]
                unit += 1
            End While
            Return If(unit = 0, $"{bytes} B", $"{value:0.#} {units(unit)}")
        End Function

        Private Function FileSize(path As String) As Long
            Try
                Return If(File.Exists(path), New FileInfo(path).Length, 0L)
            Catch
                Return 0L
            End Try
        End Function

        Private Function FolderSize(folder As String) As Long
            Try
                If Not Directory.Exists(folder) Then Return 0L
                Dim sum As Long = 0
                For Each file In Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    sum += FileSize(file)
                Next
                Return sum
            Catch
                Return 0L
            End Try
        End Function

    End Module

End Namespace
