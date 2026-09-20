using System.Runtime.InteropServices;

namespace MonitorAnchor;

/// <summary>
/// Idle keep-alive. Every 30 s, when there has been no keyboard or mouse input for <see cref="IdleThreshold"/>:
/// with <see cref="KeepRdpAlive"/>, every Remote Desktop window (full-screen or not) is brought to the front in
/// turn and sent a harmless F15 so each remote session sees activity; otherwise, with <see cref="JiggleMouse"/>, the mouse is
/// nudged one pixel and back so the local session, screen saver and presence indicators keep seeing input.
/// </summary>
public sealed class Jiggler : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _jiggleMouse, _keepRdpAlive;

    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(60);
    public int JiggleCount { get; private set; }
    public int RdpPokeCount { get; private set; }

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

    public bool JiggleMouse
    {
        get => _jiggleMouse;
        set { _jiggleMouse = value; UpdateTimer(); }
    }

    public bool KeepRdpAlive
    {
        get => _keepRdpAlive;
        set { _keepRdpAlive = value; UpdateTimer(); }
    }

    private void UpdateTimer()
    {
        if (_jiggleMouse || _keepRdpAlive) _timer.Start(); else _timer.Stop();
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

        if (_keepRdpAlive)
        {
            var sessions = RemoteDesktop.FindSessions();
            if (sessions.Count > 0)
            {
                List<string> lines;
                try { lines = RemoteDesktop.PokeAll(sessions); }
                catch (Exception ex) { lines = new List<string> { "failed: " + ex.Message }; }
                RdpPokeCount++;
                if (RdpPokeCount <= 3 || RdpPokeCount % 20 == 0)
                    Log.Write($"RDP keep-alive: idle {idle.TotalSeconds:F0} s, {sessions.Count} session(s) poked (round {RdpPokeCount}):" +
                              string.Concat(lines.Select(l => Environment.NewLine + "    " + l)));
                return; // the key presses count as local input too, so no mouse nudge is needed
            }
        }

        if (_jiggleMouse)
        {
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
    }

    public void Dispose() => _timer.Dispose();
}
