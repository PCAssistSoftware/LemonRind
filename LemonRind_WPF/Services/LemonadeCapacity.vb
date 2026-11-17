Imports System.Globalization
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>One graphics chip in GET /system-stats.</summary>
    Public Class LemonadeGpuStats
        <JsonPropertyName("id")>
        Public Property Id As String

        <JsonPropertyName("name")>
        Public Property Name As String

        <JsonPropertyName("gpu_percent")>
        Public Property GpuPercent As Double?

        <JsonPropertyName("memory_total_gb")>
        Public Property MemoryTotalGb As Double?

        <JsonPropertyName("memory_used_gb")>
        Public Property MemoryUsedGb As Double?

        ''' <summary>"shared" when the chip uses the computer's main memory.</summary>
        <JsonPropertyName("memory_type")>
        Public Property MemoryType As String
    End Class

    ''' <summary>GET /system-stats: how busy the machine Lemonade runs on is right now. Any figure can be missing (Nothing).</summary>
    Public Class LemonadeSystemStats
        <JsonPropertyName("cpu_percent")>
        Public Property CpuPercent As Double?

        <JsonPropertyName("gpu_percent")>
        Public Property GpuPercent As Double?

        <JsonPropertyName("npu_percent")>
        Public Property NpuPercent As Double?

        ''' <summary>Main memory in use.</summary>
        <JsonPropertyName("memory_gb")>
        Public Property MemoryGb As Double?

        <JsonPropertyName("primary_gpu_id")>
        Public Property PrimaryGpuId As String

        <JsonPropertyName("gpus")>
        Public Property Gpus As List(Of LemonadeGpuStats) = New List(Of LemonadeGpuStats)

        ''' <summary>The graphics chip Lemonade reports as its main one (else the first), or Nothing when there is none.</summary>
        Public ReadOnly Property PrimaryGpu As LemonadeGpuStats
            Get
                If Gpus Is Nothing OrElse Gpus.Count = 0 Then Return Nothing
                Dim match = Gpus.FirstOrDefault(Function(g) g.Id IsNot Nothing AndAlso g.Id = PrimaryGpuId)
                Return If(match, Gpus(0))
            End Get
        End Property
    End Class

    Public Enum MemoryFitVerdict
        Fits
        Tight
        TooBig
        Unknown
    End Enum

    Public Class MemoryFit
        Public Property Verdict As MemoryFitVerdict
        Public Property Message As String
    End Class

    ''' <summary>
    ''' Will a model fit in the memory of the machine Lemonade runs on? A rough answer from the model's file size and the
    ''' memory the server reports. It does not count the working space a long conversation needs on top (the context
    ''' window), so "fits" means the weights fit, not that every context size will.
    ''' </summary>
    Public Module MemoryFitCheck

        ''' <summary>The share of memory a model's file may take and still leave room for the context and everything else on the machine. A rule of thumb, not a measurement.</summary>
        Public Const ComfortableShare As Double = 0.7

        ''' <summary>The machine's main memory in GB, from the "Physical Memory" text in /system-info ("122.20 GB"), or Nothing.</summary>
        Public Function PhysicalMemoryGb(systemInfo As JsonElement) As Double?
            Dim text As JsonElement = Nothing
            If systemInfo.ValueKind <> JsonValueKind.Object OrElse Not systemInfo.TryGetProperty("Physical Memory", text) OrElse text.ValueKind <> JsonValueKind.String Then Return Nothing
            Dim match = Regex.Match(If(text.GetString(), ""), "(\d+(?:\.\d+)?)\s*(GB|MB|TB)", RegexOptions.IgnoreCase)
            If Not match.Success Then Return Nothing
            Dim value = Double.Parse(match.Groups(1).Value, CultureInfo.InvariantCulture)
            Select Case match.Groups(2).Value.ToUpperInvariant()
                Case "MB" : Return value / 1024
                Case "TB" : Return value * 1024
                Case Else : Return value
            End Select
        End Function

        ''' <summary>Compares a model's file size with the graphics memory (when the server has a graphics chip) or main memory.</summary>
        Public Function Assess(sizeGb As Double?, stats As LemonadeSystemStats, ramGb As Double?) As MemoryFit
            If Not sizeGb.HasValue OrElse sizeGb.Value <= 0 Then
                Return New MemoryFit With {.Verdict = MemoryFitVerdict.Unknown, .Message = "The model's size is not known."}
            End If

            Dim capacity As Double
            Dim where As String
            Dim gpu = stats?.PrimaryGpu
            If gpu IsNot Nothing AndAlso gpu.MemoryTotalGb.HasValue AndAlso gpu.MemoryTotalGb.Value > 0 Then
                capacity = gpu.MemoryTotalGb.Value
                where = If(gpu.MemoryType = "shared", "graphics memory (shared with the main memory)", "graphics memory")
            ElseIf ramGb.HasValue AndAlso ramGb.Value > 0 Then
                capacity = ramGb.Value
                where = "main memory"
            Else
                Return New MemoryFit With {.Verdict = MemoryFitVerdict.Unknown, .Message = "The server did not say how much memory it has."}
            End If

            Dim share = sizeGb.Value / capacity
            Dim summary = $"{sizeGb.Value:0.0} GB of {capacity:0} GB of {where} ({share:0%})"
            If share <= ComfortableShare Then Return New MemoryFit With {.Verdict = MemoryFitVerdict.Fits, .Message = $"Fits: {summary}. That is the model file alone: the context (KV cache) and working space need more."}
            If share <= 1.0 Then Return New MemoryFit With {.Verdict = MemoryFitVerdict.Tight, .Message = $"Tight: {summary}. That is the model file alone, and the context (KV cache) and working space need more, so there is little room."}
            Return New MemoryFit With {.Verdict = MemoryFitVerdict.TooBig, .Message = $"Too big: {summary}. It could only run partly from slower memory, if at all."}
        End Function

    End Module

End Namespace
