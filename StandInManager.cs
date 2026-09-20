namespace MonitorAnchor;

/// <summary>
/// Keeps a virtual monitor standing in for every saved monitor that is currently unplugged, so the
/// desktop keeps its shape and windows stay where they were. Virtual displays are fungible: each pass
/// pairs whichever virtual monitors exist with whichever saved monitors are missing and pushes the
/// saved mode onto them. Requires the Parsec Virtual Display Driver (see <see cref="ParsecVdd"/>).
/// </summary>
public sealed class StandInManager : IDisposable
{
    private ParsecVdd? _vdd;
    private readonly List<int> _owned = new();
    private bool _warnedMissingDriver;

    public bool DriverPresent => ParsecVdd.IsDriverPresent();
    public int ActiveStandIns => _owned.Count;

    private ParsecVdd? Driver()
    {
        if (_vdd != null) return _vdd;
        _vdd = ParsecVdd.Open();
        if (_vdd == null && !_warnedMissingDriver)
        {
            _warnedMissingDriver = true;
            Log.Write("Virtual stand-ins enabled but the Parsec Virtual Display Driver is not installed.");
        }
        return _vdd;
    }

    /// <summary>Saved (real) monitors that are not physically connected right now.</summary>
    private static List<MonitorSettings> Missing(DisplayProfile saved)
    {
        var connected = Topology.ConnectedMonitors()
            .Select(t => t.MonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return saved.Monitors
            .Where(m => !ParsecVdd.IsVirtualMonitorId(m.MonitorId))
            .Where(m => !connected.Contains(m.MonitorId))
            .ToList();
    }

    /// <summary>
    /// Step 1 of a pass, before the normal apply: if a real monitor has come back while its stand-in still
    /// occupies its spot, retire the surplus stand-ins first. Returns true when it changed the topology,
    /// in which case the caller should wait for the next display-change event instead of continuing.
    /// </summary>
    public bool RemoveSurplus(DisplayProfile saved)
    {
        if (_owned.Count == 0) return false;
        var vdd = Driver();
        if (vdd == null) return false;

        int needed = Missing(saved).Count;
        bool changed = false;
        while (_owned.Count > needed)
        {
            int idx = _owned[^1];
            _owned.RemoveAt(_owned.Count - 1);
            vdd.RemoveDisplay(idx);
            Log.Write($"Stand-in: removed virtual display #{idx} ({_owned.Count} left, {needed} needed)");
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Step 2 of a pass, after the normal apply: create stand-ins for missing monitors, then push each
    /// missing monitor's saved mode onto a virtual monitor. Returns a result when it did something.
    /// </summary>
    public ApplyResult? AddAndPosition(DisplayProfile saved)
    {
        var missing = Missing(saved);
        if (missing.Count == 0 && _owned.Count == 0) return null;

        var vdd = Driver();
        if (vdd == null) return null;

        var lines = new List<string>();
        int changed = 0, failed = 0;

        // Plug in as many virtual displays as we are short. Windows raises a display-change event when each
        // one arrives, and the next pass positions it.
        while (_owned.Count < Math.Min(missing.Count, ParsecVdd.MaxDisplays))
        {
            int idx = vdd.AddDisplay();
            if (idx < 0)
            {
                string err = "Stand-in: driver refused to add a virtual display";
                Log.Write(err); lines.Add(err); failed++;
                break;
            }
            _owned.Add(idx);
            string line = $"Stand-in: added virtual display #{idx} for {missing[_owned.Count - 1].MonitorName}";
            Log.Write(line); lines.Add(line); changed++;
        }
        if (changed > 0 || failed > 0)
            return new ApplyResult(changed, failed, 0, string.Join(Environment.NewLine, lines));

        // Pair the virtual monitors that exist with the missing monitors, in a stable order, and push modes.
        var virtuals = DisplayManager.Capture().Monitors
            .Where(m => ParsecVdd.IsVirtualMonitorId(m.MonitorId))
            .OrderBy(m => m.AdapterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pending = new List<(MonitorSettings Live, MonitorSettings Want)>();
        for (int i = 0; i < Math.Min(virtuals.Count, missing.Count); i++)
        {
            if (missing[i].SameModeAs(virtuals[i])) continue;
            pending.Add((virtuals[i], missing[i]));
        }
        if (pending.Count == 0) return null;

        var (c, f, pushLines) = DisplayManager.PushModes(pending, "stand-in for ");
        return new ApplyResult(c, f, 0, string.Join(Environment.NewLine, pushLines));
    }

    /// <summary>Retires every stand-in (mode switched off, or app exiting).</summary>
    public void RemoveAll()
    {
        if (_vdd == null || _owned.Count == 0) { _owned.Clear(); return; }
        foreach (int idx in _owned) _vdd.RemoveDisplay(idx);
        Log.Write($"Stand-in: removed all {_owned.Count} virtual display(s)");
        _owned.Clear();
    }

    public void Dispose()
    {
        try { RemoveAll(); } catch { /* best effort */ }
        _vdd?.Dispose();
        _vdd = null;
    }
}
