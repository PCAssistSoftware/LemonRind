using Microsoft.Extensions.AI;

namespace LemonRindBlazor.Services;

/// <summary>A picture could not be read. The message is fit to show the person.</summary>
public class PictureReadException(string message) : Exception(message);

/// <summary>No loaded model can read pictures right now, so no picture can be read until one is loaded.</summary>
public class PictureModelException(string message) : PictureReadException(message);

/// <summary>
/// Reads a picture for a knowledge base: a vision model writes out any text in it and describes what it shows, and that
/// description is what gets searched (a knowledge base can only search text).
///
/// Which model? Whatever chat model Lemonade has loaded right now, provided it can see pictures (Lemonade labels those
/// "vision"). Nothing is loaded or swapped for the purpose: loading another model could push out the one a chat or a
/// scheduled job is using. If the loaded model cannot see pictures the person is told which ones can.
/// </summary>
public class PictureReader(LemonadeManagementClient management, IChatClient chatClient, ImageAttachmentService images)
{
    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    public const string Prompt =
        "Describe this picture so that someone who cannot see it could find it by searching later. " +
        "First write out, word for word, any text that appears in it. " +
        "Then describe what it shows: the objects, people, layout, and any charts, tables or numbers. " +
        "Be factual and specific, and do not guess at anything that is not visible.";

    private const int MaxDescriptionTokens = 4096; // a thinking model's thinking counts towards this, so it needs room

    public static bool IsPicture(string path) =>
        PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool HasLabel(LemonadeModelInfo model, string label) =>
        model.Labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name of the loaded chat model that can see pictures; throws PictureReadException saying what to do if there is none.</summary>
    public async Task<string> LoadedVisionModelAsync(CancellationToken cancellationToken)
    {
        List<LemonadeModelInfo> models;
        LemonadeHealthInfo? health;
        try
        {
            models = await management.ListDownloadedModelsAsync(cancellationToken);
            health = await management.GetHealthAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PictureModelException($"Could not ask Lemonade which model is loaded: {ex.Message}");
        }

        var byName = models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var chatLoaded = (health?.AllModelsLoaded.Select(m => m.ModelName) ?? [])
            .Append(health?.ModelLoaded)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => byName.TryGetValue(name, out var info) && HasLabel(info, "chat"))
            .ToList();

        foreach (var name in chatLoaded)
        {
            if (HasLabel(byName[name], "vision")) return name;
        }

        var canSee = models.Where(m => HasLabel(m, "vision")).Select(m => m.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        var examples = canSee.Count > 0 ? $" Models that can: {string.Join(", ", canSee)}." : "";
        if (chatLoaded.Count > 0)
        {
            throw new PictureModelException($"The loaded model '{chatLoaded[0]}' cannot read pictures. Load one that can and add the picture again.{examples}");
        }
        throw new PictureModelException($"No chat model is loaded. Load one that can read pictures and add the picture again.{examples}");
    }

    /// <summary>The picture as a JPEG of at most 1568 pixels on its longest side (what vision models read well), as bytes.</summary>
    public byte[] ShrinkPicture(string path)
    {
        string? saved = null;
        try
        {
            saved = images.SaveResizedCopy(path);
            return File.ReadAllBytes(saved);
        }
        catch (Exception ex)
        {
            throw new PictureReadException($"'{Path.GetFileName(path)}' could not be read as a picture: {ex.Message}");
        }
        finally
        {
            if (saved is not null)
            {
                try { File.Delete(saved); } catch { /* a leftover temporary copy is harmless */ }
            }
        }
    }

    /// <summary>Asks the loaded vision model to read the picture. Returns the description and the model's name.</summary>
    public async Task<(string Text, string Model)> DescribeAsync(byte[] image, string name, CancellationToken cancellationToken)
    {
        var model = await LoadedVisionModelAsync(cancellationToken);
        var message = new ChatMessage(ChatRole.User, [new TextContent(Prompt), new DataContent(image, "image/jpeg")]);
        var options = new ChatOptions { ModelId = model, MaxOutputTokens = MaxDescriptionTokens };

        ChatResponse response;
        try
        {
            response = await chatClient.GetResponseAsync([message], options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PictureReadException(ex.Message);
        }

        var text = response.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new PictureReadException($"'{model}' did not write a description of '{name}' (a model that thinks may have used its whole allowance thinking). Try again, or load a model that thinks less.");
        }
        return (text, model);
    }
}
