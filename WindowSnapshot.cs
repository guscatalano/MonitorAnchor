using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAnchor;

/// <summary>
/// Diagnostic: lists every visible top-level window with its position, size, state and the monitor it is on,
/// so the log shows what Windows did to windows around a display change.
/// </summary>
public static class WindowSnapshot
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static string Describe(string reason)
    {
        var sb = new StringBuilder();
        sb.Append("Windows (").Append(reason).Append("):");
        int count = 0;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    long ex = GetWindowLongPtrW(hWnd, GWL_EXSTYLE).ToInt64();
                    if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                    if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                    var title = new StringBuilder(256);
                    GetWindowTextW(hWnd, title, title.Capacity);
                    if (title.Length == 0) return true;
                    if (!GetWindowRect(hWnd, out var r)) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    string proc;
                    try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "pid" + pid; }

                    string state = IsIconic(hWnd) ? "minimized" : IsZoomed(hWnd) ? "maximized" : "normal";
                    string screen;
                    try { screen = Screen.FromHandle(hWnd).DeviceName; } catch { screen = "?"; }

                    string t = title.ToString();
                    if (t.Length > 40) t = t[..40] + "…";

                    sb.AppendLine().Append("    ")
                      .Append(screen.PadRight(14)).Append(' ')
                      .Append(state.PadRight(9)).Append(' ')
                      .Append($"({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}".PadRight(26)).Append(' ')
                      .Append(proc).Append(": ").Append(t);
                    count++;
                }
                catch
                {
                    // ignore a window that vanished mid-enumeration
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            sb.AppendLine().Append("    enumeration failed: ").Append(ex.Message);
        }
        sb.Append(count == 0 ? " none" : "");
        return sb.ToString();
    }

    public static void LogNow(string reason) => Log.Write(Describe(reason));
}
