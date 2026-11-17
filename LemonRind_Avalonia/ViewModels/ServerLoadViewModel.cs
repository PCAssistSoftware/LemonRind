using LemonRindAvalonia.Services;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One reading of how busy Lemonade's machine is, worked out for display (the "Lemonade server" card in the Stats tab).
/// Immutable: a new one replaces the old on every poll.
/// </summary>
public sealed class ServerLoadViewModel
{
    public double CpuPercent { get; }
    public string CpuText { get; }
    public double GraphicsPercent { get; }
    public string GraphicsText { get; }
    public bool HasNpu { get; }
    public double NpuPercent { get; }
    public string NpuText { get; }
    public bool HasGraphicsMemory { get; }
    public string GraphicsMemoryLabel { get; }
    public double GraphicsMemoryPercent { get; }
    public string GraphicsMemoryText { get; }
    public bool HasMemoryNote { get; }
    public string MemoryNote { get; }

    public ServerLoadViewModel(LemonadeSystemStats stats)
    {
        var gpu = stats.PrimaryGpu;

        CpuPercent = Clamp(stats.CpuPercent);
        CpuText = PercentText(stats.CpuPercent);

        var graphics = stats.GpuPercent ?? gpu?.GpuPercent;
        GraphicsPercent = Clamp(graphics);
        GraphicsText = PercentText(graphics);

        HasNpu = stats.NpuPercent is not null;
        NpuPercent = Clamp(stats.NpuPercent);
        NpuText = PercentText(stats.NpuPercent);

        HasGraphicsMemory = gpu?.MemoryTotalGb is > 0;
        if (HasGraphicsMemory)
        {
            var used = gpu!.MemoryUsedGb ?? 0;
            GraphicsMemoryLabel = gpu.MemoryType == "shared" ? "Graphics memory (shared)" : "Graphics memory";
            GraphicsMemoryPercent = Clamp(100 * used / gpu.MemoryTotalGb!.Value);
            GraphicsMemoryText = $"{used:0.0} / {gpu.MemoryTotalGb.Value:0} GB";
        }
        else
        {
            GraphicsMemoryLabel = "";
            GraphicsMemoryText = "";
        }

        HasMemoryNote = stats.MemoryGb is not null;
        MemoryNote = HasMemoryNote ? $"Main memory in use: {stats.MemoryGb!.Value:0.0} GB" : "";
    }

    private static double Clamp(double? percent) => Math.Clamp(percent ?? 0, 0, 100);

    private static string PercentText(double? percent) => percent is null ? "n/a" : $"{percent.Value:0}%";
}
