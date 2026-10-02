using LemonRindBlazor.Configuration;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace LemonRindBlazor.Services;

/// <summary>
/// Takes a user-picked image file, downscales it and saves a local copy
/// under Data\Workspace\AttachedImages (same "Workspace" root/naming
/// convention as ImageGenerationService's own GeneratedImages folder), and
/// builds the Microsoft.Extensions.AI DataContent a chat request actually
/// sends. Downscaling before saving - not just before sending - means a
/// re-opened session's persisted copy is already the small version, so
/// nothing needs to reprocess it again on every reload.
///
/// Uses SixLabors.ImageSharp (pure managed, genuinely cross-platform) rather
/// than Avalonia's own Bitmap (the Avalonia port's own choice, not usable
/// here with no Avalonia reference at all) or System.Drawing.Common
/// (Windows-only as of .NET 6+, explicitly the wrong choice for an ASP.NET
/// Core server app that needs to keep running on Linux too).
/// </summary>
public class ImageAttachmentService
{
    public string[] SupportedExtensions { get; } = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    // 1568px longest edge - the point past which most vision models stop
    // extracting additional useful detail from an image (a widely-cited
    // rule of thumb for vision-model inputs generally, not a Lemonade-
    // specific documented limit), so resizing beyond this trades context/
    // token cost for detail the model wouldn't use anyway.
    private const int MaxLongestEdge = 1568;
    private const int JpegQuality = 85;

    private readonly string _imagesFolder;

    public ImageAttachmentService(AppSettings settings)
    {
        _imagesFolder = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "AttachedImages");
    }

    public bool IsSupported(string filePath) => SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    /// <summary>Downscales (if needed) and re-encodes as JPEG, saving a fresh copy under AttachedImages. Returns the new copy's full local path.</summary>
    public string SaveResizedCopy(string sourceFilePath)
    {
        using var image = Image.Load(sourceFilePath);

        var longestEdge = Math.Max(image.Width, image.Height);
        if (longestEdge > MaxLongestEdge)
        {
            var scale = MaxLongestEdge / (double)longestEdge;
            var newWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
            var newHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(newWidth, newHeight));
        }

        Directory.CreateDirectory(_imagesFolder);
        var fileName = $"img_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.jpg";
        var fullPath = Path.Combine(_imagesFolder, fileName);

        using var outputStream = File.Create(fullPath);
        image.Save(outputStream, new JpegEncoder { Quality = JpegQuality });

        return fullPath;
    }

    /// <summary>Reads a previously-saved copy's bytes and wraps them as the multi-modal content a chat message actually sends - shared by the live-send path and by LoadSession restoring an older turn's image.</summary>
    public DataContent BuildDataContent(string imagePath) => new(File.ReadAllBytes(imagePath), "image/jpeg");

    /// <summary>Parses the "[Attached image: {path}]" + blank line + question marker MainViewModel.SendAsync persists back into the image's local path and the remaining question text. Returns (null, content unchanged) when content doesn't start with the marker at all.</summary>
    public static (string? ImagePath, string RemainingText) TryParseAttachedImageMarker(string content)
    {
        const string prefix = "[Attached image: ";
        if (!content.StartsWith(prefix, StringComparison.Ordinal)) return (null, content);

        var closingBracketIndex = content.IndexOf(']');
        if (closingBracketIndex < 0) return (null, content);

        var imagePath = content.Substring(prefix.Length, closingBracketIndex - prefix.Length);
        var remainingText = content[(closingBracketIndex + 1)..].TrimStart('\r', '\n');
        return (imagePath, remainingText);
    }
}
