namespace MonitorAnchor;

/// <summary>
/// Logs whether someone appears to be at the machine: a line when input stops for a while ("away"), a line when
/// it resumes ("back after ..."), and a heartbeat every half hour either way, so the log answers "was anyone
/// there at 3am?" without guesswork.
/// </summary>
public sealed class Presence : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(30);

    private bool _away;
    private DateTime _awaySince;
    private DateTime _lastHeartbeat = DateTime.MinValue;

    public bool IsAway => _away;
    public TimeSpan AwayFor => _away ? DateTime.Now - _awaySince : TimeSpan.Zero;

    public Presence()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Log.Write($"Presence: active (last human input {Describe(InputTracker.HumanIdleTime())} ago)");
        _lastHeartbeat = DateTime.Now;
    }

    private void Tick()
    {
        var idle = InputTracker.HumanIdleTime(); // ignores the app's own jiggles and key presses
        var now = DateTime.Now;

        if (!_away && idle >= AwayAfter)
        {
            _away = true;
            _awaySince = now - idle;
            Log.Write($"Presence: away (no input since {_awaySince:HH:mm})");
            _lastHeartbeat = now;
        }
        else if (_away && idle < AwayAfter)
        {
            Log.Write($"Presence: back after {Describe(now - _awaySince)} away");
            _away = false;
            _lastHeartbeat = now;
        }
        else if (now - _lastHeartbeat >= Heartbeat)
        {
            Log.Write(_away
                ? $"Presence: still away ({Describe(now - _awaySince)}, since {_awaySince:HH:mm})"
                : $"Presence: active (last human input {Describe(idle)} ago)");
            _lastHeartbeat = now;
        }
    }

    public string Status => _away ? $"away for {Describe(AwayFor)} (since {_awaySince:HH:mm})" : $"active, last human input {Describe(InputTracker.HumanIdleTime())} ago";

    public static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} s";

    public void Dispose() => _timer.Dispose();
}
