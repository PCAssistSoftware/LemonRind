Imports LemonRind.Services

Namespace ViewModels

    ''' <summary>
    ''' One reading of how busy Lemonade's machine is, worked out for display (the "Lemonade server" card in the Stats tab).
    ''' Immutable: a new one replaces the old on every poll.
    ''' </summary>
    Public Class ServerLoadViewModel

        Public ReadOnly Property CpuPercent As Double
        Public ReadOnly Property CpuText As String
        Public ReadOnly Property GraphicsPercent As Double
        Public ReadOnly Property GraphicsText As String
        Public ReadOnly Property HasNpu As Boolean
        Public ReadOnly Property NpuPercent As Double
        Public ReadOnly Property NpuText As String
        Public ReadOnly Property HasGraphicsMemory As Boolean
        Public ReadOnly Property GraphicsMemoryLabel As String = ""
        Public ReadOnly Property GraphicsMemoryPercent As Double
        Public ReadOnly Property GraphicsMemoryText As String = ""
        Public ReadOnly Property HasMemoryNote As Boolean
        Public ReadOnly Property MemoryNote As String = ""

        Public Sub New(stats As LemonadeSystemStats)
            Dim gpu = stats.PrimaryGpu

            CpuPercent = Clamp(stats.CpuPercent)
            CpuText = PercentText(stats.CpuPercent)

            Dim graphics = If(stats.GpuPercent, gpu?.GpuPercent)
            GraphicsPercent = Clamp(graphics)
            GraphicsText = PercentText(graphics)

            HasNpu = stats.NpuPercent.HasValue
            NpuPercent = Clamp(stats.NpuPercent)
            NpuText = PercentText(stats.NpuPercent)

            If gpu IsNot Nothing AndAlso gpu.MemoryTotalGb.HasValue AndAlso gpu.MemoryTotalGb.Value > 0 Then
                HasGraphicsMemory = True
                Dim used = If(gpu.MemoryUsedGb, 0.0)
                GraphicsMemoryLabel = If(gpu.MemoryType = "shared", "Graphics memory (shared)", "Graphics memory")
                GraphicsMemoryPercent = Clamp(100 * used / gpu.MemoryTotalGb.Value)
                GraphicsMemoryText = $"{used:0.0} / {gpu.MemoryTotalGb.Value:0} GB"
            End If

            If stats.MemoryGb.HasValue Then
                HasMemoryNote = True
                MemoryNote = $"Main memory in use: {stats.MemoryGb.Value:0.0} GB"
            End If
        End Sub

        Private Shared Function Clamp(percent As Double?) As Double
            Return Math.Min(Math.Max(If(percent, 0.0), 0.0), 100.0)
        End Function

        Private Shared Function PercentText(percent As Double?) As String
            Return If(percent.HasValue, $"{percent.Value:0}%", "n/a")
        End Function

    End Class

End Namespace
