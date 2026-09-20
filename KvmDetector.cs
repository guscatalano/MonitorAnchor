using System.Text.Json;

namespace MonitorAnchor;

/// <summary>Counters that survive restarts, kept in state.json.</summary>
public sealed class AppState
{
    public int KvmSwitchesAway { get; set; }
    public int KvmSwitchesBack { get; set; }
    public int DockEvents { get; set; }
    /// <summary>How many of the KVM switches took only the USB side while the monitors stayed connected.</summary>
    public int KvmUsbOnly { get; set; }
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
    // Kind: monitor-, monitor+, hid-, hid+, other-, other+. Key identifies the device so a flapping hub counts once.
    private readonly List<(DateTime At, string Kind, string Key)> _events = new();
    private int _monitorSeq;

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
        for (int i = 0; i < removed; i++) _events.Add((at, "monitor-", "m" + (++_monitorSeq)));
        for (int i = 0; i < added; i++) _events.Add((at, "monitor+", "m" + (++_monitorSeq)));
        Log.Write($"Monitor topology: {now.Count} connected ({removed} removed, {added} added)");
        if (removed + added > 0) Restart();
    }

    /// <summary>Call for every device interface arrival/removal (from <see cref="DeviceWatcher"/>).</summary>
    public void OnDevice(bool arrived, string path)
    {
        if (path.Contains("#DISPLAY#", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase))
            return; // monitors are counted through the topology diff
        bool hid = path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase);
        _events.Add((DateTime.Now, (hid ? "hid" : "other") + (arrived ? "+" : "-"), path.ToUpperInvariant()));
        Log.Write($"Device {(arrived ? "arrived" : "removed")}: {(hid ? "input" : "other")} {Shorten(path)}");
        Restart();
    }

    private void Restart() { _settle.Stop(); _settle.Start(); }

    // Test hooks: feed events without real hardware and evaluate immediately.
    internal void FeedMonitors(int removed, int added, DateTime? at = null)
    {
        var t = at ?? DateTime.Now;
        for (int i = 0; i < removed; i++) _events.Add((t, "monitor-", "m" + (++_monitorSeq)));
        for (int i = 0; i < added; i++) _events.Add((t, "monitor+", "m" + (++_monitorSeq)));
    }
    internal void FeedDevice(bool arrived, string path, DateTime? at = null)
    {
        bool hid = path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase);
        _events.Add((at ?? DateTime.Now, (hid ? "hid" : "other") + (arrived ? "+" : "-"), path.ToUpperInvariant()));
    }
    internal string? LastKind { get; private set; }

    /// <summary>\\?\HID#VID_046D&amp;PID_C52B&amp;MI_00#7&amp;1a2b3c4d&amp;0&amp;0000#{guid} → HID#VID_046D&amp;PID_C52B&amp;MI_00</summary>
    private static string Shorten(string path)
    {
        var parts = path.Split('#');
        return parts.Length >= 3 ? parts[1] + "#" + parts[2] : path;
    }

    internal void Evaluate()
    {
        var cutoff = DateTime.Now - Window - TimeSpan.FromMilliseconds(_settle.Interval);
        _events.RemoveAll(e => e.At < cutoff);
        if (_events.Count == 0) return;

        int Count(string kind) => _events.Where(e => e.Kind == kind).Select(e => e.Key).Distinct().Count();
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
        else if (hidOff > 0 && otherOff > 0 && otherOff < DockThreshold && monOff == 0)
        {
            // Some KVMs switch USB immediately and leave video connected (or drop it later). Input devices
            // plus their hub vanishing together, without a dock-sized burst, is still the KVM signature.
            _state.KvmSwitchesAway++; _state.KvmUsbOnly++; _state.LastKvmSwitch = DateTime.Now; _state.Save();
            Raise("KVM switch away (USB only)", $"{hidOff} input device(s) and {otherOff} hub/other device(s) disconnected within {span:F1} s; monitors stayed connected");
        }
        else if (hidOn > 0 && otherOn > 0 && otherOn < DockThreshold && monOn == 0)
        {
            _state.KvmSwitchesBack++; _state.KvmUsbOnly++; _state.LastKvmSwitch = DateTime.Now; _state.Save();
            Raise("KVM switch back (USB only)", $"{hidOn} input device(s) and {otherOn} hub/other device(s) connected within {span:F1} s; monitors were already connected");
        }
        else if (monOff > 0 || monOn > 0)
        {
            Log.Write($"Monitor change without input-device change ({monOff} off, {monOn} on, hid {hidOff}/{hidOn}, other {otherOff}/{otherOn}): plain plug/unplug, not a KVM");
        }
        else if (DateTime.Now - _lastRaised < TimeSpan.FromSeconds(15))
        {
            // A hub flapping in the wake of a switch we already reported; not worth a line of its own.
        }
        else
        {
            Log.Write($"Device change without monitor change (hid {hidOff}/{hidOn}, other {otherOff}/{otherOn}): ordinary USB plug/unplug");
        }
    }

    private DateTime _lastRaised = DateTime.MinValue;

    private void Raise(string kind, string detail)
    {
        _lastRaised = DateTime.Now;
        LastKind = kind;
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
            parts.Add($"KVM suspected: {_state.KvmSwitchesAway} switch(es) away, {_state.KvmSwitchesBack} back, last {_state.LastKvmSwitch:g}" +
                      (_state.KvmUsbOnly > 0 ? $" ({_state.KvmUsbOnly} switched USB only; the KVM keeps video connected)" : ""));
        if (_state.DockEvents > 0)
            parts.Add($"dock events: {_state.DockEvents}, last {_state.LastDockEvent:g}");
        return string.Join("; ", parts);
    }

    public void Dispose() => _settle.Dispose();
}
