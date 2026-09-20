using System.Runtime.InteropServices;

namespace MonitorAnchor;

/// <summary>
/// Tells human input apart from injected input. Windows' own idle timer (GetLastInputInfo) resets on our
/// SendInput calls too, so a mouse jiggle would look like a person. Low-level keyboard and mouse hooks see the
/// "injected" flag on synthetic events and ignore them; if the hooks cannot be installed, a fallback compares
/// the system idle timer against the moments we injected input ourselves.
/// </summary>
public static class InputTracker
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static HookProc? _kbProc, _mouseProc; // kept alive for the hook's lifetime
    private static IntPtr _kbHook, _mouseHook;

    private static long _lastHumanTick = Environment.TickCount64;
    private static long _lastOwnInjectionTick = long.MinValue;
    private static long _lastForeignInjectionTick = long.MinValue;
    private static long _humanEvents, _ownInjected, _foreignInjected;

    public static bool HooksActive => _kbHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    /// <summary>Install the hooks. Must run on a thread with a message loop (the UI thread).</summary>
    public static void Start()
    {
        if (HooksActive) return;
        try
        {
            _kbProc = KeyboardHook;
            _mouseProc = MouseHook;
            IntPtr module = GetModuleHandleW(null);
            _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, module, 0);
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
            Log.Write(HooksActive ? "Input tracking: low-level hooks active (injected input is told apart from human input)"
                                  : $"Input tracking: hooks unavailable (error {Marshal.GetLastWin32Error()}); using the timing fallback");
        }
        catch (Exception ex)
        {
            Log.Write("Input tracking: hook install failed: " + ex.Message);
        }
    }

    public static void Stop()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    /// <summary>Call right after every SendInput this app makes, so the fallback can attribute the resulting idle reset.</summary>
    public static void MarkOwnInjection() => _lastOwnInjectionTick = Environment.TickCount64;

    private static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(lParam, 8); // KBDLLHOOKSTRUCT.flags
            Record((flags & LLKHF_INJECTED) != 0);
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(lParam, 12); // MSLLHOOKSTRUCT.flags
            Record((flags & LLMHF_INJECTED) != 0);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static void Record(bool injected)
    {
        long now = Environment.TickCount64;
        if (!injected)
        {
            _lastHumanTick = now;
            _humanEvents++;
        }
        else if (now - _lastOwnInjectionTick <= 500)
        {
            _ownInjected++;
        }
        else
        {
            _foreignInjected++;
            _lastForeignInjectionTick = now;
        }
    }

    /// <summary>Time since the last input that came from a person (or, without hooks, since the last idle reset we did not cause).</summary>
    public static TimeSpan HumanIdleTime()
    {
        if (HooksActive) return TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastHumanTick);

        // Fallback: the system idle timer, unless its last reset coincides with one of our own injections.
        var systemIdle = Jiggler.IdleTime();
        long lastInputTick = Environment.TickCount64 - (long)systemIdle.TotalMilliseconds;
        if (Math.Abs(lastInputTick - _lastOwnInjectionTick) > 500) _lastHumanTick = Math.Max(_lastHumanTick, lastInputTick);
        return TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastHumanTick);
    }

    public static string Status
    {
        get
        {
            string mode = HooksActive ? "hooks" : "timing fallback";
            string foreign = _foreignInjected > 0
                ? $"; injected input from other software: {_foreignInjected} event(s), last {Presence.Describe(TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastForeignInjectionTick))} ago"
                : "";
            return $"human idle {Presence.Describe(HumanIdleTime())} (system idle {Presence.Describe(Jiggler.IdleTime())}); {mode}; " +
                   $"{_humanEvents} human event(s), {_ownInjected} of ours{foreign}";
        }
    }
}
