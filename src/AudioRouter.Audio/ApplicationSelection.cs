namespace AudioRouter.Audio;

public static class ApplicationSelection
{
    public static ApplicationSource[] Normalize(IEnumerable<ApplicationSource> sources) => sources
        .Where(source => !string.IsNullOrWhiteSpace(source.ExecutablePath))
        .GroupBy(source => source.ExecutablePath, StringComparer.OrdinalIgnoreCase)
        .Select(group => new ApplicationSource(group.First().Name, group.First().ExecutablePath,
            group.SelectMany(source => source.Processes).Distinct().OrderBy(process => process.Id).ToArray(), group.Any(source => source.HasAudio)))
        .ToArray();
}
