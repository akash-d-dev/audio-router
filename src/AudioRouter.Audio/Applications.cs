using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace AudioRouter.Audio;

public sealed record ProcessTarget(int Id, long StartedUtcTicks);
public sealed record ApplicationSource(string Name, string ExecutablePath, IReadOnlyList<ProcessTarget> Processes, bool HasAudio)
{
    public string Detail => HasAudio ? "Audio session available" : "Waiting for audio";
}

public static class ProcessTree
{
    // Only climb through the same executable. Climbing to Explorer would capture unrelated apps.
    public static int FindApplicationRoot(int id, IReadOnlyDictionary<int, int> parents,
        Func<int, string?> getPath, Func<int, long> getStarted)
    {
        string? path = getPath(id);
        if (path is null) return id;
        var seen = new HashSet<int> { id };
        while (parents.TryGetValue(id, out int parent) && seen.Add(parent)
            && string.Equals(path, getPath(parent), StringComparison.OrdinalIgnoreCase)
            && getStarted(parent) <= getStarted(id)) id = parent;
        return id;
    }
}

public sealed class Applications
{
    private readonly ConcurrentDictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ApplicationSource> Discover()
    {
        var parents = ReadParents();
        var candidates = new HashSet<int>();
        Native.EnumWindows((window, _) =>
        {
            if (Native.IsWindowVisible(window) && Native.GetWindow(window, 4) == IntPtr.Zero
                && Native.GetWindowTextLength(window) > 0)
            {
                Native.GetWindowThreadProcessId(window, out uint id);
                candidates.Add((int)id);
            }
            return true;
        }, IntPtr.Zero);

        var audioProcesses = new HashSet<int>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (endpoint)
                {
                    try
                    {
                        var sessions = endpoint.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            if (!session.IsSystemSoundsSession && session.GetProcessID > 0)
                            {
                                int id = (int)session.GetProcessID;
                                candidates.Add(id);
                                audioProcesses.Add(id);
                            }
                        }
                    }
                    catch (COMException) { /* An output can disappear during enumeration. */ }
                }
            }
        }

        var infos = new Dictionary<int, (string? Path, long Started)>();
        (string? Path, long Started) GetInfo(int id)
        {
            if (!infos.TryGetValue(id, out var info)) infos[id] = info = GetProcessInfo(id);
            return info;
        }
        string? ownPath = Environment.ProcessPath;
        var groups = new Dictionary<string, (List<ProcessTarget> Roots, bool Audio)>(StringComparer.OrdinalIgnoreCase);
        foreach (int id in candidates)
        {
            var info = GetInfo(id);
            if (info.Path is null || id == Environment.ProcessId || string.Equals(ownPath, info.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsShellInfrastructure(info.Path)) continue;
            int root = ProcessTree.FindApplicationRoot(id, parents, p => GetInfo(p).Path, p => GetInfo(p).Started);
            var rootInfo = GetInfo(root);
            if (!groups.TryGetValue(info.Path, out var group)) group = ([], false);
            if (group.Roots.All(x => x.Id != root)) group.Roots.Add(new(root, rootInfo.Started));
            group.Audio |= audioProcesses.Contains(id);
            groups[info.Path] = group;
        }
        return groups.Select(g => new ApplicationSource(names.GetOrAdd(g.Key, FriendlyName), g.Key,
                g.Value.Roots.OrderBy(p => p.Id).ToArray(), g.Value.Audio))
            .OrderByDescending(x => x.HasAudio).ThenBy(x => x.Name).ToArray();
    }

    private static bool IsShellInfrastructure(string path) => Path.GetFileName(path).ToLowerInvariant() is
        "applicationframehost.exe" or "textinputhost.exe" or "shellexperiencehost.exe" or "startmenuexperiencehost.exe"
        or "searchhost.exe" or "lockapp.exe" or "explorer.exe";

    private static string FriendlyName(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public static bool IsSameProcess(ProcessTarget target, string expectedPath)
    {
        var info = GetProcessInfo(target.Id);
        return info.Started == target.StartedUtcTicks && string.Equals(info.Path, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Path, long Started) GetProcessInfo(int id)
    {
        using var process = Native.OpenProcess(0x1000, false, id);
        if (process.IsInvalid) return (null, 0);
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, path, ref size)
            || !Native.GetProcessTimes(process, out long created, out _, out _, out _)) return (null, 0);
        return (path.ToString(), created);
    }

    private static Dictionary<int, int> ReadParents()
    {
        var parents = new Dictionary<int, int>();
        using var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return parents;
        var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>(), ExeFile = "" };
        if (Native.Process32First(snapshot, ref entry))
            do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Native.Process32Next(snapshot, ref entry));
        return parents;
    }

    private static class Native
    {
        internal delegate bool WindowCallback(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowCallback callback, IntPtr param);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] internal static extern SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeFileHandle process, int flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeFileHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }
    }
}
