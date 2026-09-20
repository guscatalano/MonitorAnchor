using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAnchor;

/// <summary>
/// Remembers where every window sits while the layout is intact, and after a monitor comes back moves the
/// windows that Windows shoved elsewhere back to their remembered place. Windows 11 does this itself for many
/// cases; this covers the rest (windows it forgot, windows opened while a monitor was away, older Windows).
/// </summary>
public sealed class WindowMemory
{
    private sealed record Placement(string Process, string Title, string MonitorId, WINDOWPLACEMENT Wp);

    private readonly Dictionary<IntPtr, Placement> _last = new();
    public DateTime LastSnapshot { get; private set; } = DateTime.MinValue;
    public int Remembered => _last.Count;
    public int ActiveMonitorCount { get; private set; }

    /// <summary>Bookkeeping for diagnostics.</summary>
    public void Note(int activeMonitors) => ActiveMonitorCount = activeMonitors;

    public string Status => Remembered == 0 ? "nothing remembered yet" : $"{Remembered} window(s) remembered at {LastSnapshot:T}";

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length; public int flags; public int showCmd; public POINT ptMinPosition; public POINT ptMaxPosition; public RECT rcNormalPosition;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3;

    /// <summary>Maps \\.\DISPLAYn device names to monitor identities for the monitors active right now.</summary>
    private static (Dictionary<string, string> ByDevice, HashSet<string> Ids) ActiveMonitors()
    {
        var live = DisplayManager.Capture().Monitors.Where(m => m.IsActive).ToList();
        return (live.ToDictionary(m => m.AdapterName, m => m.MonitorId, StringComparer.OrdinalIgnoreCase),
                live.Select(m => m.MonitorId).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Records every window's placement, but only while all of the active layout's monitors are present.</summary>
    public bool Snapshot(DisplayProfile active)
    {
        var (byDevice, ids) = ActiveMonitors();
        if (!active.Monitors.All(m => ids.Contains(m.MonitorId))) return false; // collapsed layout: keep the old memory

        var fresh = new Dictionary<IntPtr, Placement>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                long ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(256);
                GetWindowTextW(h, title, title.Capacity);
                if (title.Length == 0) return true;

                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(h, ref wp)) return true;
                if (wp.showCmd == SW_SHOWMINIMIZED) return true; // where a minimised window "is" says nothing useful

                string device = Screen.FromHandle(h).DeviceName;
                if (!byDevice.TryGetValue(device, out var monitorId)) return true;

                GetWindowThreadProcessId(h, out uint pid);
                string proc;
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
                fresh[h] = new Placement(proc, title.ToString(), monitorId, wp);
            }
            catch { /* window vanished */ }
            return true;
        }, IntPtr.Zero);

        _last.Clear();
        foreach (var (k, v) in fresh) _last[k] = v;
        LastSnapshot = DateTime.Now;
        return true;
    }

    /// <summary>Moves windows back onto their remembered monitor if that monitor is present and they are not on it. Returns one line per moved window.</summary>
    public List<string> Restore()
    {
        var lines = new List<string>();
        if (_last.Count == 0) return lines;
        var (byDevice, ids) = ActiveMonitors();

        foreach (var (h, p) in _last.ToList())
        {
            try
            {
                if (!IsWindow(h) || !IsWindowVisible(h)) { _last.Remove(h); continue; }
                if (!ids.Contains(p.MonitorId)) continue;              // its monitor is still away
                if (IsIconic(h)) continue;                               // leave minimised windows alone

                byDevice.TryGetValue(Screen.FromHandle(h).DeviceName, out var currentId);
                if (string.Equals(currentId, p.MonitorId, StringComparison.OrdinalIgnoreCase)) continue; // already where it belongs

                // Move at normal size first so Windows picks the right monitor, then re-maximise if it was maximised.
                var wp = p.Wp;
                wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
                bool wasMax = wp.showCmd == SW_SHOWMAXIMIZED;
                wp.showCmd = SW_SHOWNORMAL;
                bool ok = SetWindowPlacement(h, ref wp);
                if (ok && wasMax) ShowWindow(h, SW_SHOWMAXIMIZED);

                string t = p.Title.Length > 40 ? p.Title[..40] + "…" : p.Title;
                lines.Add($"{p.Process}: \"{t}\" -> {(wasMax ? "maximised on" : "back to")} its monitor ({p.Wp.rcNormalPosition.Left},{p.Wp.rcNormalPosition.Top}): {(ok ? "moved" : "failed")}");
            }
            catch (Exception ex)
            {
                lines.Add($"{p.Process}: failed: {ex.Message}");
            }
        }
        return lines;
    }
}
