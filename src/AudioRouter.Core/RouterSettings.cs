using System.Text.Json;

namespace AudioRouter.Core;

public sealed record RouterSettings
{
    public string? MicrophoneId { get; init; }
    public string? CableId { get; init; }
    public string? ApplicationPath { get; init; }
    public string[]? ApplicationPaths { get; init; }
    public float MicrophoneGain { get; init; } = 1;
    public float ApplicationGain { get; init; } = .25f;
    public bool ShowLiveMeters { get; init; }

    public RouterSettings Validated()
    {
        var paths = (ApplicationPaths ?? (string.IsNullOrWhiteSpace(ApplicationPath) ? [] : [ApplicationPath]))
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return this with
        {
            // Keep the first path readable by older single-source releases.
            ApplicationPath = paths.FirstOrDefault(), ApplicationPaths = paths,
            MicrophoneGain = float.IsFinite(MicrophoneGain) ? Math.Clamp(MicrophoneGain, 0, 2) : 1,
            ApplicationGain = float.IsFinite(ApplicationGain) ? Math.Clamp(ApplicationGain, 0, 2) : .25f
        };
    }
}

public static class SettingsStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioRouter");
    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    public static RouterSettings Load(string? path = null, string? legacyPath = null)
    {
        if (path is null)
        {
            path = DefaultPath;
            legacyPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceRouter", "settings.json");
        }
        // Migrate preferences on first use without overwriting an existing Audio Router setup.
        if (!File.Exists(path) && legacyPath is not null && File.Exists(legacyPath)) path = legacyPath;
        try { return (JsonSerializer.Deserialize<RouterSettings>(File.ReadAllText(path)) ?? new()).Validated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static void Save(RouterSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Validated(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
