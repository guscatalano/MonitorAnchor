using System.Runtime.InteropServices;

namespace MonitorAnchor;

/// <summary>
/// Nudges the mouse by one pixel and back when there has been no keyboard or mouse input for a while,
/// so the session, screen saver and presence indicators keep seeing activity. Checks every 30 s.
/// </summary>
public sealed class Jiggler : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(60);
    public int JiggleCount { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public Jiggler()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled
    {
        get => _timer.Enabled;
        set { if (value) _timer.Start(); else _timer.Stop(); }
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    private void Tick()
    {
        var idle = IdleTime();
        if (idle < IdleThreshold) return;

        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = MOUSEEVENTF_MOVE } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = -1, dy = 0, dwFlags = MOUSEEVENTF_MOVE } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        JiggleCount++;
        if (JiggleCount == 1 || JiggleCount % 20 == 0)
            Log.Write($"Jiggle: idle {idle.TotalSeconds:F0} s, nudged mouse ({sent}/2 inputs sent, {JiggleCount} total)");
    }

    public void Dispose() => _timer.Dispose();
}
