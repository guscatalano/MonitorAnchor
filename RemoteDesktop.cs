using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAnchor;

public sealed record RdpSession(IntPtr Handle, string Title, string Process, string Screen, bool FullScreen);

/// <summary>Finds Remote Desktop windows (mstsc / msrdc / Windows App) and pokes them with a harmless key.</summary>
public static class RemoteDesktop
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public KEYBDINPUT ki; private readonly long _pad; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_F15 = 0x7E;

    private static readonly string[] RdpProcesses = { "mstsc", "msrdc", "msrdcw", "windowsapp" };

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>True when a descendant window belongs to the Remote Desktop ActiveX control (IHWindowClass / OPWindowClass / UIMainClass).</summary>
    private static bool HasRdpChild(IntPtr top)
    {
        bool found = false;
        var cls = new StringBuilder(64);
        EnumChildWindows(top, (c, _) =>
        {
            cls.Clear();
            GetClassNameW(c, cls, cls.Capacity);
            string n = cls.ToString();
            if (n.StartsWith("IHWindowClass", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("OPWindowClass", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("UIMainClass", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("TscShellContainer", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false; // stop
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>All visible, non-minimised Remote Desktop session windows.</summary>
    public static List<RdpSession> FindSessions()
    {
        var result = new List<RdpSession>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                var cls = new StringBuilder(64);
                GetClassNameW(h, cls, cls.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                string proc;
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }

                bool rdpClass = cls.ToString().Contains("TscShellContainer", StringComparison.OrdinalIgnoreCase);
                bool rdpProcess = RdpProcesses.Contains(proc, StringComparer.OrdinalIgnoreCase);
                // Hosts like mRemoteNG or Royal TS embed the Remote Desktop control inside their own window;
                // the control's input window class gives it away.
                if (!rdpClass && !rdpProcess && !HasRdpChild(h)) return true;

                var title = new StringBuilder(256);
                GetWindowTextW(h, title, title.Capacity);
                if (title.Length == 0) return true;
                if (!GetWindowRect(h, out var r)) return true;

                var screen = System.Windows.Forms.Screen.FromHandle(h);
                long winArea = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
                long scrArea = (long)screen.Bounds.Width * screen.Bounds.Height;
                bool full = winArea >= scrArea * 0.95; // full-screen (or /multimon spanning more than one screen)

                result.Add(new RdpSession(h, title.ToString(), proc, screen.DeviceName, full));
            }
            catch { /* window vanished mid-enumeration */ }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Gives each session window focus in turn and presses F15, a key no application uses, so every remote
    /// side registers input. The window that was in front beforehand is put back afterwards.
    /// Returns one line per session, for the log.
    /// </summary>
    public static List<string> PokeAll(IReadOnlyList<RdpSession> sessions)
    {
        var lines = new List<string>();
        if (sessions.Count == 0) return lines;

        IntPtr previous = GetForegroundWindow();
        GetCursorPos(out var cursor);
        // Windows refuses SetForegroundWindow to a process that has not received input recently; a no-op key
        // press first makes us the "last input" process.
        PressF15();

        foreach (var s in sessions)
        {
            bool ok = true;
            if (GetForegroundWindow() != s.Handle)
            {
                ok = SetForegroundWindow(s.Handle);
                Thread.Sleep(150);
            }
            // Remote Desktop only forwards mouse movement while the cursor is over the session, so park it in
            // the middle of the window, nudge it, and press the key as well.
            if (GetWindowRect(s.Handle, out var r))
            {
                SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                Thread.Sleep(30);
                NudgeMouse();
            }
            PressF15();
            lines.Add($"\"{Trim(s.Title)}\" ({(s.FullScreen ? "full-screen" : "windowed")} on {s.Screen}): {(ok ? "mouse nudged, F15 sent" : "could not focus; mouse nudged, F15 sent anyway")}");
        }

        SetCursorPos(cursor.X, cursor.Y);
        if (previous != IntPtr.Zero && sessions.All(s => s.Handle != previous))
        {
            SetForegroundWindow(previous);
        }
        return lines;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MINPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")] private static extern uint SendMouseInput(uint nInputs, MINPUT[] pInputs, int cbSize);

    private static void NudgeMouse()
    {
        var inputs = new[]
        {
            new MINPUT { type = 0, mi = new MOUSEINPUT { dx = 3, dy = 0, dwFlags = 0x0001 } },
            new MINPUT { type = 0, mi = new MOUSEINPUT { dx = -3, dy = 0, dwFlags = 0x0001 } },
        };
        SendMouseInput((uint)inputs.Length, inputs, Marshal.SizeOf<MINPUT>());
    }

    /// <summary>Diagnostic: top-level windows whose title contains <paramref name="titlePart"/>, with their child window classes.</summary>
    public static string DescribeWindowClasses(string titlePart)
    {
        var sb = new StringBuilder();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = new StringBuilder(256);
            GetWindowTextW(h, title, title.Capacity);
            if (!title.ToString().Contains(titlePart, StringComparison.OrdinalIgnoreCase)) return true;
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            sb.AppendLine($"{title} [{cls}]");
            var seen = new HashSet<string>();
            EnumChildWindows(h, (c, _) =>
            {
                var cc = new StringBuilder(64);
                GetClassNameW(c, cc, cc.Capacity);
                if (seen.Add(cc.ToString())) sb.AppendLine("    child class: " + cc);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return sb.Length == 0 ? "no window matches " + titlePart : sb.ToString();
    }

    private static string Trim(string s) => s.Length > 50 ? s[..50] + "…" : s;

    private static void PressF15()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_F15 } },
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_F15, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
