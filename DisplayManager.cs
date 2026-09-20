namespace MonitorAnchor;

public sealed record ApplyResult(int Changed, int Failed, int Unmatched, string Summary)
{
    public bool MadeChanges => Changed > 0;
    public bool HadFailures => Failed > 0;
}

/// <summary>Captures the current per-monitor display modes and re-applies a saved profile.</summary>
public static class DisplayManager
{
    /// <summary>Enumerates monitors attached to the desktop with their active mode and HDR state.</summary>
    public static DisplayProfile Capture()
    {
        var profile = new DisplayProfile { CapturedAt = DateTime.Now };
        var hdr = HdrManager.QueryAll();
        var dpi = DpiScaling.QueryAll();
        // DisplayConfig knows the monitor's real model name ("VG259QM"); the legacy API only says "Generic PnP Monitor".
        var friendly = Topology.ConnectedMonitors()
            .GroupBy(t => t.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FriendlyName, StringComparer.OrdinalIgnoreCase);

        for (uint i = 0; ; i++)
        {
            var adapter = Native.DISPLAY_DEVICE.Create();
            if (!Native.EnumDisplayDevices(null, i, ref adapter, 0)) break;

            if ((adapter.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((adapter.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            // The first monitor hanging off this adapter output gives us a stable identity.
            var monitor = Native.DISPLAY_DEVICE.Create();
            string monitorId = string.Empty, monitorName = adapter.DeviceString;
            if (Native.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, Native.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                monitorId = monitor.DeviceID;
                if (!string.IsNullOrWhiteSpace(monitor.DeviceString)) monitorName = monitor.DeviceString;
            }
            if (string.IsNullOrEmpty(monitorId)) monitorId = "ADAPTER:" + adapter.DeviceName;
            if (friendly.TryGetValue(monitorId, out var better) && !string.IsNullOrWhiteSpace(better)) monitorName = better;

            var dm = Native.DEVMODE.Create();
            if (!Native.EnumDisplaySettingsEx(adapter.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                Log.Write($"EnumDisplaySettingsEx failed for {adapter.DeviceName}");
                continue;
            }

            var entry = new MonitorSettings
            {
                MonitorId = monitorId,
                MonitorName = monitorName,
                AdapterName = adapter.DeviceName,
                IsActive = true,
                Width = (int)dm.dmPelsWidth,
                Height = (int)dm.dmPelsHeight,
                RefreshRate = (int)dm.dmDisplayFrequency,
                BitsPerPixel = (int)dm.dmBitsPerPel,
                PositionX = dm.dmPositionX,
                PositionY = dm.dmPositionY,
                Orientation = (int)dm.dmDisplayOrientation,
                IsPrimary = (adapter.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
            };
            if (hdr.TryGetValue(adapter.DeviceName, out var h))
            {
                entry.HdrSupported = h.Supported;
                entry.HdrEnabled = h.Enabled;
            }
            if (dpi.TryGetValue(adapter.DeviceName, out var d)) entry.DpiScale = d.Percent;
            profile.Monitors.Add(entry);
        }

        return profile;
    }

    /// <summary>What gets saved: the active layout minus any virtual stand-in monitors.</summary>
    public static DisplayProfile CaptureForProfile()
    {
        var profile = Capture();
        profile.Monitors.RemoveAll(m => ParsecVdd.IsVirtualMonitorId(m.MonitorId));
        return profile;
    }

    /// <summary>Like <see cref="Capture"/> plus monitors that are physically connected but not part of the desktop (IsActive = false).</summary>
    public static DisplayProfile CaptureAll()
    {
        var profile = Capture();
        foreach (var t in Topology.ConnectedMonitors().Where(t => !t.IsActive))
        {
            if (profile.Monitors.Any(m => string.Equals(m.MonitorId, t.MonitorId, StringComparison.OrdinalIgnoreCase))) continue;
            profile.Monitors.Add(new MonitorSettings { MonitorId = t.MonitorId, MonitorName = t.FriendlyName, IsActive = false });
        }
        return profile;
    }

    /// <summary>
    /// Compares the live layout against <paramref name="saved"/> and pushes the saved mode to every
    /// connected monitor that differs, re-enabling saved monitors that Windows left disabled.
    /// All mode changes are staged with CDS_NORESET and committed in one call so multi-monitor
    /// position changes do not fight each other. HDR state is then reconciled through DisplayConfig.
    /// </summary>
    public static ApplyResult Apply(DisplayProfile saved)
    {
        int changed = 0, failed = 0, unmatched = 0;
        var lines = new List<string>();

        var current = CaptureAll();

        // Saved monitors that are plugged in but switched off in the topology: bring them back first.
        var disabled = current.Monitors.Where(m => !m.IsActive && saved.FindFor(m) != null).ToList();
        if (disabled.Count > 0)
        {
            string status = Topology.Enable(disabled.Select(d => d.MonitorId).ToList());
            string line = $"Re-enabling {string.Join(", ", disabled.Select(d => d.MonitorName))}: {status}";
            Log.Write(line);
            lines.Add(line);
            if (status.StartsWith("success")) { changed++; current = CaptureAll(); } else failed++;
        }

        var pending = new List<(MonitorSettings Live, MonitorSettings Want)>();
        foreach (var live in current.Monitors.Where(m => m.IsActive))
        {
            if (ParsecVdd.IsVirtualMonitorId(live.MonitorId)) continue; // stand-ins are handled by StandInManager
            var want = saved.FindFor(live);
            if (want == null) { unmatched++; Log.Write($"No saved settings for {live}"); continue; }
            if (want.SameModeAs(live)) continue;
            pending.Add((live, want));
        }

        if (pending.Count > 0)
        {
            var (c, f, pushLines) = PushModes(pending);
            changed += c; failed += f; lines.AddRange(pushLines);
        }

        // HDR and scaling: adapter names and targets can shift after a topology change, so re-read before reconciling.
        var after = pending.Count > 0 ? Capture() : current;
        foreach (var live in after.Monitors.Where(m => m.IsActive))
        {
            var want = saved.FindFor(live);
            if (want == null) continue;

            if (want.HdrSupported && live.HdrSupported && want.HdrEnabled != live.HdrEnabled)
            {
                string status = HdrManager.Set(live.AdapterName, want.HdrEnabled);
                string line = $"{live.MonitorName} ({live.AdapterName}): HDR {(live.HdrEnabled ? "on" : "off")} -> {(want.HdrEnabled ? "on" : "off")} : {status}";
                Log.Write(line);
                lines.Add(line);
                if (status.StartsWith("success")) changed++; else failed++;
            }

            if (want.DpiScale > 0 && live.DpiScale > 0 && want.DpiScale != live.DpiScale)
            {
                string status = DpiScaling.Set(live.AdapterName, want.DpiScale);
                string line = $"{live.MonitorName} ({live.AdapterName}): scale {live.DpiScale}% -> {want.DpiScale}% : {status}";
                Log.Write(line);
                lines.Add(line);
                if (status.StartsWith("success")) changed++; else failed++;
            }
        }

        if (lines.Count == 0)
            return new ApplyResult(0, 0, unmatched, "Layout already matches the saved profile.");

        string summary = $"{changed} change(s) applied, {failed} failed." + Environment.NewLine + string.Join(Environment.NewLine, lines);
        return new ApplyResult(changed, failed, unmatched, summary);
    }

    /// <summary>
    /// Stages the wanted mode on each live adapter with CDS_NORESET (primary first, so other positions are
    /// relative to it) and commits once. If a driver rejects the exact refresh rate, retries without it.
    /// </summary>
    internal static (int Changed, int Failed, List<string> Lines) PushModes(
        List<(MonitorSettings Live, MonitorSettings Want)> pending, string labelPrefix = "")
    {
        int changed = 0, failed = 0;
        var lines = new List<string>();
        if (pending.Count == 0) return (0, 0, lines);

        foreach (var (live, want) in pending.OrderBy(p => p.Want.IsPrimary ? 0 : 1))
        {
            int rc = Stage(live.AdapterName, want, includeRefresh: true);
            string note = "";
            if (rc == Native.DISP_CHANGE_BADMODE && want.RefreshRate > 0)
            {
                rc = Stage(live.AdapterName, want, includeRefresh: false);
                note = " (refresh rate not available, kept resolution/position)";
            }
            string line = $"{labelPrefix}{want.MonitorName} ({live.AdapterName}): {live.Width}x{live.Height}@{live.RefreshRate} -> " +
                          $"{want.Width}x{want.Height}@{want.RefreshRate} pos({want.PositionX},{want.PositionY}) : {Native.DescribeDispChange(rc)}{note}";
            Log.Write(line);
            lines.Add(line);
            if (rc == Native.DISP_CHANGE_SUCCESSFUL) changed++; else failed++;
        }

        int commit = Native.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Write($"Commit: {Native.DescribeDispChange(commit)}");
        if (commit != Native.DISP_CHANGE_SUCCESSFUL) { failed++; changed = 0; }
        return (changed, failed, lines);
    }

    private static int Stage(string adapterName, MonitorSettings want, bool includeRefresh)
    {
        var dm = Native.DEVMODE.Create();
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_BITSPERPEL | Native.DM_POSITION | Native.DM_DISPLAYORIENTATION;
        if (includeRefresh) dm.dmFields |= Native.DM_DISPLAYFREQUENCY;
        dm.dmPelsWidth = (uint)want.Width;
        dm.dmPelsHeight = (uint)want.Height;
        dm.dmDisplayFrequency = (uint)want.RefreshRate;
        dm.dmBitsPerPel = (uint)want.BitsPerPixel;
        dm.dmPositionX = want.IsPrimary ? 0 : want.PositionX;
        dm.dmPositionY = want.IsPrimary ? 0 : want.PositionY;
        dm.dmDisplayOrientation = (uint)want.Orientation;

        uint flags = Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET;
        if (want.IsPrimary) flags |= Native.CDS_SET_PRIMARY;
        return Native.ChangeDisplaySettingsEx(adapterName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
    }
}
