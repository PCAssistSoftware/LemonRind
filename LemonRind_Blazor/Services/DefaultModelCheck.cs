namespace LemonRindBlazor.Services;

/// <summary>Which model a scheduled job should use, and anything the person should be told about it. Model null means "leave the default as it is".</summary>
public sealed record JobModelChoice(string? Model, string Note, string Problem);

/// <summary>
/// Checks the default chat model (Settings > Lemonade) against what Lemonade really has. A model's name can change when
/// Lemonade is updated; the setting then names a model that is gone, and everything that relies on the default fails
/// with "model not found". The Settings page warns about it, and a scheduled job with no model of its own falls back to
/// the chat model Lemonade has loaded instead of failing.
/// </summary>
public static class DefaultModelCheck
{
    private static bool IsChat(LemonadeModelInfo model) =>
        model.Labels.Any(label => string.Equals(label, "chat", StringComparison.OrdinalIgnoreCase));

    public static bool IsListed(string model, IReadOnlyList<LemonadeModelInfo> models) =>
        models.Any(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>A sentence for Settings when the default chat model is not one Lemonade has, else "" (also "" when the list could not be fetched).</summary>
    public static string WarningFor(string? model, IReadOnlyList<LemonadeModelInfo> models)
    {
        if (string.IsNullOrWhiteSpace(model) || models.Count == 0 || IsListed(model.Trim(), models)) return "";

        var examples = models.Where(IsChat).Select(m => m.Id).Take(5).ToList();
        var hint = examples.Count > 0 ? $" Chat models on Lemonade include: {string.Join(", ", examples)}." : "";
        return $"'{model.Trim()}' is not one of the models Lemonade has now. A scheduled job that uses the default model will use whichever chat model is loaded instead, and fail if none is.{hint}";
    }

    /// <summary>
    /// The model a scheduled job should use. A model chosen for the job is always used as it is. With none chosen the
    /// default chat model is used when Lemonade has it; if not, the chat model that is loaded takes its place (with a note
    /// saying so), and with no chat model loaded the job is told why it cannot run.
    /// </summary>
    public static JobModelChoice ChooseForJob(string? jobModel, string defaultModel, IReadOnlyList<LemonadeModelInfo> models, LemonadeHealthInfo? health)
    {
        if (!string.IsNullOrWhiteSpace(jobModel)) return new JobModelChoice(jobModel, "", "");
        if (models.Count == 0) return new JobModelChoice(null, "", ""); // could not ask Lemonade: carry on as before
        if (!string.IsNullOrWhiteSpace(defaultModel) && IsListed(defaultModel, models)) return new JobModelChoice(null, "", "");

        var loaded = (health?.AllModelsLoaded.Select(m => m.ModelName) ?? [])
            .Append(health?.ModelLoaded)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => models.FirstOrDefault(m => string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null && IsChat(m));

        var shown = string.IsNullOrWhiteSpace(defaultModel) ? "(none set)" : defaultModel;
        if (loaded is not null)
        {
            return new JobModelChoice(loaded.Id,
                $"Note: the default chat model '{shown}' is not on Lemonade now, so this run used '{loaded.Id}', the chat model that was loaded. Update the default in Settings > Lemonade.", "");
        }
        return new JobModelChoice(null, "",
            $"The default chat model '{shown}' is not on Lemonade now, and no chat model is loaded. Set the default in Settings > Lemonade, or choose a model for this job.");
    }
}
