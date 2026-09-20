using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAnchor;

/// <summary>
/// Moves the cursor over every visible top-level window in turn and nudges it, so any application that
/// watches for hover or mouse movement (VNC/Citrix/browser-based remote sessions, chat presence, players)
/// sees activity. Windows are not focused; hovering is enough for mouse events. The cursor goes back afterwards.
/// </summary>
public static class MouseTour
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const uint GA_ROOT = 2;

    public sealed record Result(int Visited, int Hidden, List<string> Names);

    /// <summary>Hovers and nudges over each window whose surface is actually reachable by the cursor.</summary>
    public static Result Run()
    {
        var windows = new List<(IntPtr Handle, string Title, RECT Rect)>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                long ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(256);
                GetWindowTextW(h, title, title.Capacity);
                if (title.Length == 0) return true;
                if (!GetWindowRect(h, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return true;
                windows.Add((h, title.ToString(), r));
            }
            catch { /* window vanished */ }
            return true;
        }, IntPtr.Zero);

        GetCursorPos(out var origin);
        int visited = 0, hidden = 0;
        var names = new List<string>();
        try
        {
            foreach (var (h, title, r) in windows)
            {
                // Try the centre and a few other spots; an occluded window has no reachable surface and is skipped.
                var candidates = new[]
                {
                    Pt(r, 0.5, 0.5), Pt(r, 0.25, 0.25), Pt(r, 0.75, 0.25), Pt(r, 0.25, 0.75), Pt(r, 0.75, 0.75), Pt(r, 0.5, 0.1),
                };
                bool found = false;
                foreach (var p in candidates)
                {
                    if (GetAncestor(WindowFromPoint(p), GA_ROOT) != h) continue;
                    SetCursorPos(p.X, p.Y);
                    Thread.Sleep(25);
                    Nudge();
                    Thread.Sleep(25);
                    found = true;
                    break;
                }
                if (found) { visited++; names.Add(title.Length > 40 ? title[..40] + "…" : title); } else hidden++;
            }
        }
        finally
        {
            SetCursorPos(origin.X, origin.Y);
        }
        return new Result(visited, hidden, names);
    }

    private static POINT Pt(RECT r, double fx, double fy) =>
        new() { X = r.Left + (int)((r.Right - r.Left) * fx), Y = r.Top + (int)((r.Bottom - r.Top) * fy) };

    private static void Nudge()
    {
        var inputs = new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = 4, dy = 2, dwFlags = 0x0001 } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = -4, dy = -2, dwFlags = 0x0001 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
