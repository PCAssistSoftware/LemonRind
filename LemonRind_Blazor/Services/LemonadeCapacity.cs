using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LemonRindBlazor.Services;

/// <summary>One graphics chip in GET /system-stats.</summary>
public class LemonadeGpuStats
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("gpu_percent")]
    public double? GpuPercent { get; set; }

    [JsonPropertyName("memory_total_gb")]
    public double? MemoryTotalGb { get; set; }

    [JsonPropertyName("memory_used_gb")]
    public double? MemoryUsedGb { get; set; }

    /// <summary>"shared" when the chip uses the computer's main memory.</summary>
    [JsonPropertyName("memory_type")]
    public string? MemoryType { get; set; }
}

/// <summary>GET /system-stats: how busy the machine Lemonade runs on is right now. Any figure can be missing (null).</summary>
public class LemonadeSystemStats
{
    [JsonPropertyName("cpu_percent")]
    public double? CpuPercent { get; set; }

    [JsonPropertyName("gpu_percent")]
    public double? GpuPercent { get; set; }

    [JsonPropertyName("npu_percent")]
    public double? NpuPercent { get; set; }

    /// <summary>Main memory in use.</summary>
    [JsonPropertyName("memory_gb")]
    public double? MemoryGb { get; set; }

    [JsonPropertyName("primary_gpu_id")]
    public string? PrimaryGpuId { get; set; }

    [JsonPropertyName("gpus")]
    public List<LemonadeGpuStats> Gpus { get; set; } = [];

    /// <summary>The graphics chip Lemonade reports as its main one (else the first), or null when there is none.</summary>
    public LemonadeGpuStats? PrimaryGpu =>
        Gpus.FirstOrDefault(g => g.Id is not null && g.Id == PrimaryGpuId) ?? Gpus.FirstOrDefault();
}

public enum MemoryFitVerdict { Fits, Tight, TooBig, Unknown }

public sealed record MemoryFit(MemoryFitVerdict Verdict, string Message);

/// <summary>
/// Will a model fit in the memory of the machine Lemonade runs on? A rough answer from the model's file size and the
/// memory the server reports. It does not count the working space a long conversation needs on top (the context
/// window), so "fits" means the weights fit, not that every context size will.
/// </summary>
public static class MemoryFitCheck
{
    /// <summary>The share of memory a model's file may take and still leave room for the context and everything else on the machine. A rule of thumb, not a measurement.</summary>
    public const double ComfortableShare = 0.7;

    /// <summary>The machine's main memory in GB, from the "Physical Memory" text in /system-info ("122.20 GB"), or null.</summary>
    public static double? PhysicalMemoryGb(JsonElement systemInfo)
    {
        if (systemInfo.ValueKind != JsonValueKind.Object || !systemInfo.TryGetProperty("Physical Memory", out var text) || text.ValueKind != JsonValueKind.String) return null;
        var match = Regex.Match(text.GetString() ?? "", @"(\d+(?:\.\d+)?)\s*(GB|MB|TB)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToUpperInvariant() switch { "MB" => value / 1024, "TB" => value * 1024, _ => value };
    }

    /// <summary>Compares a model's file size with the graphics memory (when the server has a graphics chip) or main memory.</summary>
    public static MemoryFit Assess(double? sizeGb, LemonadeSystemStats? stats, double? ramGb)
    {
        if (sizeGb is not > 0) return new(MemoryFitVerdict.Unknown, "The model's size is not known.");

        double capacity;
        string where;
        var gpu = stats?.PrimaryGpu;
        if (gpu?.MemoryTotalGb is > 0)
        {
            capacity = gpu.MemoryTotalGb.Value;
            where = gpu.MemoryType == "shared" ? "graphics memory (shared with the main memory)" : "graphics memory";
        }
        else if (ramGb is > 0)
        {
            capacity = ramGb.Value;
            where = "main memory";
        }
        else
        {
            return new(MemoryFitVerdict.Unknown, "The server did not say how much memory it has.");
        }

        var share = sizeGb.Value / capacity;
        var summary = $"{sizeGb.Value:0.0} GB of {capacity:0} GB of {where} ({share:0%})";
        if (share <= ComfortableShare) return new(MemoryFitVerdict.Fits, $"Fits: {summary}. That is the model file alone: the context (KV cache) and working space need more.");
        if (share <= 1.0) return new(MemoryFitVerdict.Tight, $"Tight: {summary}. That is the model file alone, and the context (KV cache) and working space need more, so there is little room.");
        return new(MemoryFitVerdict.TooBig, $"Too big: {summary}. It could only run partly from slower memory, if at all.");
    }
}
