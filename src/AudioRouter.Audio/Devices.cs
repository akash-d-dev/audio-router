using NAudio.CoreAudioApi;

namespace AudioRouter.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool IsCable)
{
    public string DisplayName => Name + (IsDefault ? "  ·  Windows default" : "");
    public string RecordingEndpointName => Name.Replace(" Input", " Output", StringComparison.OrdinalIgnoreCase)
        .Replace(" In ", " Out ", StringComparison.OrdinalIgnoreCase);
}

public sealed record DeviceInventory(IReadOnlyList<AudioDevice> Microphones, IReadOnlyList<AudioDevice> CableInputs);

public static class Devices
{
    public static DeviceInventory GetInventory()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultMic = null;
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var device))
        {
            using (device) defaultMic = device.ID;
        }
        var inputs = Enumerate(enumerator, DataFlow.Capture, defaultMic);
        var outputs = Enumerate(enumerator, DataFlow.Render, null);
        // The cable's recording side must never be used as our microphone (feedback loop).
        return new(inputs.Where(x => !x.IsCable).OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToArray(),
            outputs.Where(x => x.IsCable).OrderBy(x => x.Name).ToArray());
    }

    private static List<AudioDevice> Enumerate(MMDeviceEnumerator enumerator, DataFlow flow, string? defaultId)
    {
        var result = new List<AudioDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (endpoint)
            {
                result.Add(new(endpoint.ID, endpoint.FriendlyName, endpoint.ID == defaultId, IsCableName(endpoint.FriendlyName)));
            }
        }
        return result;
    }

    public static bool IsCableName(string name) =>
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    // New VB-CABLE versions also expose a separate 16-channel pair. Prefer the regular
    // pair our setup guide uses, while preserving any explicit saved device choice.
    public static AudioDevice? PickCableInput(IReadOnlyList<AudioDevice> inputs, string? preferredId) =>
        inputs.FirstOrDefault(x => x.Id == preferredId)
        ?? inputs.FirstOrDefault(x => x.Name.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase))
        ?? inputs.FirstOrDefault();
}
