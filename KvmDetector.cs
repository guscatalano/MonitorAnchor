using System.Text.Json;

namespace MonitorAnchor;

/// <summary>Counters that survive restarts, kept in state.json.</summary>
public sealed class AppState
{
    public int KvmSwitchesAway { get; set; }
    public int KvmSwitchesBack { get; set; }
    public int DockEvents { get; set; }
    public DateTime? LastKvmSwitch { get; set; }
    public DateTime? LastDockEvent { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public static string Path => System.IO.Path.Combine(DisplayProfile.ConfigDir, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(Path)) return JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path), JsonOpts) ?? new AppState();
        }
        catch (Exception ex) { Log.Write("Failed to load state: " + ex.Message); }
        return new AppState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DisplayProfile.ConfigDir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { Log.Write("Failed to save state: " + ex.Message); }
    }
}

/// <summary>
/// Infers KVM switches. Nothing on the machine says "KVM", but a KVM has a signature: every monitor behind it
/// and the USB keyboard/mouse disappear in the same instant, and come back together. A dock does the same but
/// also takes hubs, network and storage with it, so a burst with many non-input devices is labelled a dock event.
/// </summary>
public sealed class KvmDetector : IDisposable
{
    public event Action<string /*kind*/, string /*detail*/>? Detected;

    private readonly AppState _state;
    private readonly System.Windows.Forms.Timer _settle;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private HashSet<string> _connected;
    private readonly List<(DateTime At, string Kind)> _events = new(); // Kind: monitor-, monitor+, hid-, hid+, other-, other+

    public KvmDetector(AppState state)
    {
        _state = state;
        _connected = CurrentMonitors();
        _settle = new System.Windows.Forms.Timer { Interval = 2000 };
        _settle.Tick += (_, _) => { _settle.Stop(); Evaluate(); };
    }

    public AppState State => _state;

    private static HashSet<string> CurrentMonitors() =>
        Topology.ConnectedMonitors().Select(t => t.MonitorId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call on every display-change event: diffs the connected monitor set against the last one.</summary>
    public void OnDisplayChange()
    {
        var now = CurrentMonitors();
        int removed = _connected.Count(id => !now.Contains(id));
        int added = now.Count(id => !_connected.Contains(id));
        _connected = now;
        var at = DateTime.Now;
        for (int i = 0; i < removed; i++) _events.Add((at, "monitor-"));
        for (int i = 0; i < added; i++) _events.Add((at, "monitor+"));
        if (removed + added > 0) Restart();
    }

    /// <summary>Call for every device interface arrival/removal (from <see cref="DeviceWatcher"/>).</summary>
    public void OnDevice(bool arrived, string path)
    {
        if (path.Contains("#DISPLAY#", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase))
            return; // monitors are counted through the topology diff
        bool hid = path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase);
        _events.Add((DateTime.Now, (hid ? "hid" : "other") + (arrived ? "+" : "-")));
        Restart();
    }

    private void Restart() { _settle.Stop(); _settle.Start(); }

    private void Evaluate()
    {
        var cutoff = DateTime.Now - Window - TimeSpan.FromMilliseconds(_settle.Interval);
        _events.RemoveAll(e => e.At < cutoff);
        if (_events.Count == 0) return;

        int Count(string kind) => _events.Count(e => e.Kind == kind);
        int monOff = Count("monitor-"), monOn = Count("monitor+");
        int hidOff = Count("hid-"), hidOn = Count("hid+");
        int otherOff = Count("other-"), otherOn = Count("other+");
        double span = (_events.Max(e => e.At) - _events.Min(e => e.At)).TotalSeconds;
        _events.Clear();

        // A dock takes a whole tree of devices with it; a KVM takes only displays and input.
        const int DockThreshold = 4;

        if (monOff > 0 && hidOff > 0)
        {
            if (otherOff >= DockThreshold)
            {
                _state.DockEvents++; _state.LastDockEvent = DateTime.Now; _state.Save();
                Raise("Undocked", $"{monOff} monitor(s), {hidOff} input device(s) and {otherOff} other device(s) disconnected within {span:F1} s");
            }
            else
            {
                _state.KvmSwitchesAway++; _state.LastKvmSwitch = DateTime.Now; _state.Save();
                Raise("KVM switch away", $"{monOff} monitor(s) and {hidOff} input device(s) disconnected within {span:F1} s");
            }
        }
        else if (monOn > 0 && hidOn > 0)
        {
            if (otherOn >= DockThreshold)
            {
                _state.DockEvents++; _state.LastDockEvent = DateTime.Now; _state.Save();
                Raise("Docked", $"{monOn} monitor(s), {hidOn} input device(s) and {otherOn} other device(s) connected within {span:F1} s");
            }
            else
            {
                _state.KvmSwitchesBack++; _state.LastKvmSwitch = DateTime.Now; _state.Save();
                Raise("KVM switch back", $"{monOn} monitor(s) and {hidOn} input device(s) connected within {span:F1} s");
            }
        }
        else if (monOff > 0 || monOn > 0)
        {
            Log.Write($"Monitor change without input-device change ({monOff} off, {monOn} on, hid {hidOff}/{hidOn}, other {otherOff}/{otherOn}): plain plug/unplug, not a KVM");
        }
    }

    private void Raise(string kind, string detail)
    {
        Log.Write($"{kind}: {detail}");
        try { Detected?.Invoke(kind, detail); } catch { /* ignore */ }
    }

    /// <summary>One-paragraph verdict for the diagnostics report.</summary>
    public string Verdict()
    {
        int switches = _state.KvmSwitchesAway + _state.KvmSwitchesBack;
        if (switches == 0 && _state.DockEvents == 0)
            return "no KVM pattern seen yet (monitors and input devices have never disconnected together while the app was running)";
        var parts = new List<string>();
        if (switches > 0)
            parts.Add($"KVM suspected: {_state.KvmSwitchesAway} switch(es) away, {_state.KvmSwitchesBack} back, last {_state.LastKvmSwitch:g}");
        if (_state.DockEvents > 0)
            parts.Add($"dock events: {_state.DockEvents}, last {_state.LastDockEvent:g}");
        return string.Join("; ", parts);
    }

    public void Dispose() => _settle.Dispose();
}
