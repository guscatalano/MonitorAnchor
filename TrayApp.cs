using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>The tray icon, its menu, and the display-change watcher that re-applies the saved layout.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _persistItem;
    private readonly ToolStripMenuItem _applyItem;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _enforceItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _keepAwakeItem;
    private readonly System.Windows.Forms.Timer _debounce;

    private readonly AppSettings _settings;
    private DisplayProfile? _profile;

    private bool _applying;
    private int _consecutiveFailures;
    private DateTime _backoffUntil = DateTime.MinValue;

    private const int DebounceMs = 1500;
    private const int MaxFailuresBeforeBackoff = 3;
    private static readonly TimeSpan BackoffWindow = TimeSpan.FromMinutes(1);

    public TrayApp()
    {
        _settings = AppSettings.Load();
        _profile = DisplayProfile.Load();

        _persistItem = new ToolStripMenuItem("Persist current layout", null, (_, _) => Persist())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        _applyItem = new ToolStripMenuItem("Apply saved layout now", null, (_, _) => ApplyNow(manual: true));
        _showItem = new ToolStripMenuItem("Show saved layout...", null, (_, _) => ShowSaved());
        _enforceItem = new ToolStripMenuItem("Enforce layout on display changes", null, (_, _) => ToggleEnforce()) { CheckOnClick = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem("Keep displays awake (never sleep)", null, (_, _) => ToggleKeepAwake()) { CheckOnClick = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_persistItem);
        menu.Items.Add(_applyItem);
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enforceItem);
        menu.Items.Add(_keepAwakeItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Persist();

        _debounce = new System.Windows.Forms.Timer { Interval = DebounceMs };
        _debounce.Tick += (_, _) => OnDebounceElapsed();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // First run: register for startup so the tool survives reboots without any extra clicks.
        if (!AppSettings.Exists)
        {
            _settings.Save();
            Startup.Enable();
        }
        else if (Startup.IsStale())
        {
            Startup.Enable(); // exe moved; re-point the Run entry
        }

        RefreshMenu();
        ApplyKeepAwake();
        Log.Write($"Started. Profile: {(_profile == null ? "none" : $"{_profile.Monitors.Count} monitor(s) from {_profile.CapturedAt}")}, enforce={_settings.Enforce}");

        if (_profile == null)
        {
            // First run: learn whatever the layout is right now and enforce it from here on.
            var learned = DisplayManager.Capture();
            if (learned.Monitors.Count > 0)
            {
                learned.Save();
                _profile = learned;
                Log.Write("Learned initial layout:" + Environment.NewLine + learned);
                RefreshMenu();
                _tray.ShowBalloonTip(6000, "Monitor Anchor",
                    $"Learned your current layout ({learned.Monitors.Count} monitor(s)) and will keep it. " +
                    "Rearrange and choose \"Persist current layout\" any time to update it.",
                    ToolTipIcon.Info);
            }
            else
            {
                _tray.ShowBalloonTip(6000, "Monitor Anchor",
                    "No active monitors found. Right-click this icon and choose \"Persist current layout\" once they are connected.",
                    ToolTipIcon.Warning);
            }
        }
        else if (_settings.Enforce)
        {
            // Displays may have changed while we were not running (e.g. before sign-in).
            ScheduleApply("startup");
        }
    }

    // ---- Menu actions -------------------------------------------------------------------------

    private void Persist()
    {
        try
        {
            var captured = DisplayManager.Capture();
            if (captured.Monitors.Count == 0)
            {
                _tray.ShowBalloonTip(4000, "Monitor Anchor", "No active monitors found to capture.", ToolTipIcon.Warning);
                return;
            }
            captured.Save();
            _profile = captured;
            Log.Write("Persisted layout:" + Environment.NewLine + captured);
            _tray.ShowBalloonTip(4000, "Layout persisted",
                $"{captured.Monitors.Count} monitor(s) saved. This layout will be restored whenever displays change.",
                ToolTipIcon.Info);
            RefreshMenu();
        }
        catch (Exception ex)
        {
            Log.Write("Persist failed: " + ex);
            _tray.ShowBalloonTip(4000, "Monitor Anchor", "Could not save layout: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void ApplyNow(bool manual)
    {
        if (_profile == null)
        {
            if (manual) _tray.ShowBalloonTip(4000, "Monitor Anchor", "Nothing saved yet. Choose \"Persist current layout\" first.", ToolTipIcon.Warning);
            return;
        }
        if (_applying) return;

        _applying = true;
        try
        {
            var result = DisplayManager.Apply(_profile);
            Log.Write($"Apply ({(manual ? "manual" : "auto")}): {result.Summary}");

            if (result.HadFailures)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxFailuresBeforeBackoff)
                {
                    _backoffUntil = DateTime.Now + BackoffWindow;
                    _consecutiveFailures = 0;
                    Log.Write($"Too many failures; pausing automatic apply until {_backoffUntil:T}");
                }
                _tray.ShowBalloonTip(5000, "Could not restore layout", result.Summary, ToolTipIcon.Warning);
            }
            else
            {
                _consecutiveFailures = 0;
                if (result.MadeChanges)
                    _tray.ShowBalloonTip(3000, "Layout restored", result.Summary, ToolTipIcon.Info);
                else if (manual)
                    _tray.ShowBalloonTip(3000, "Monitor Anchor", result.Summary, ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Apply threw: " + ex);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ShowSaved()
    {
        string text = _profile == null
            ? "No layout has been persisted yet."
            : $"Saved {_profile.CapturedAt:g}{Environment.NewLine}{Environment.NewLine}{_profile}{Environment.NewLine}{Environment.NewLine}File: {DisplayProfile.ProfilePath}";
        MessageBox.Show(text, "Monitor Anchor - saved layout", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ToggleEnforce()
    {
        _settings.Enforce = !_settings.Enforce;
        _settings.Save();
        Log.Write($"Enforce = {_settings.Enforce}");
        RefreshMenu();
        if (_settings.Enforce) ScheduleApply("enforce enabled");
    }

    private void ToggleKeepAwake()
    {
        _settings.KeepAwake = !_settings.KeepAwake;
        _settings.Save();
        ApplyKeepAwake();
        RefreshMenu();
    }

    /// <summary>Holds (or releases) the display and system idle timers. Must run on the UI thread, which lives for the app's lifetime.</summary>
    private void ApplyKeepAwake()
    {
        uint flags = Native.ES_CONTINUOUS;
        if (_settings.KeepAwake) flags |= Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED;
        uint prev = Native.SetThreadExecutionState(flags);
        Log.Write($"KeepAwake = {_settings.KeepAwake} (SetThreadExecutionState {(prev == 0 ? "failed" : "ok")})");
    }

    private void ToggleStartup()
    {
        try
        {
            if (Startup.IsEnabled()) Startup.Disable(); else Startup.Enable();
        }
        catch (Exception ex)
        {
            Log.Write("Startup toggle failed: " + ex);
            _tray.ShowBalloonTip(4000, "Monitor Anchor", "Could not change startup setting: " + ex.Message, ToolTipIcon.Error);
        }
        RefreshMenu();
    }

    private static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(DisplayProfile.ConfigDir);
            if (!File.Exists(Log.Path)) File.WriteAllText(Log.Path, string.Empty);
            Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Open log failed: " + ex.Message);
        }
    }

    private void RefreshMenu()
    {
        bool hasProfile = _profile != null;
        _applyItem.Enabled = hasProfile;
        _enforceItem.Checked = _settings.Enforce;
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _startupItem.Checked = Startup.IsEnabled();

        string state = !hasProfile ? "no layout saved"
                     : _settings.Enforce ? $"enforcing {_profile!.Monitors.Count} monitor(s)"
                     : "paused";
        _tray.Text = Truncate($"Monitor Anchor - {state}", 63); // NotifyIcon.Text is limited to 63 chars
    }

    // ---- Display change handling -----------------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleApply("display settings changed");

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleApply("resume from sleep");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteDisconnect)
            ScheduleApply(e.Reason.ToString());
    }

    private void ScheduleApply(string reason)
    {
        if (!_settings.Enforce || _profile == null) return;
        Log.Write($"Display change detected ({reason}); checking in {DebounceMs} ms");
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceElapsed()
    {
        _debounce.Stop();
        if (!_settings.Enforce || _profile == null) return;
        if (DateTime.Now < _backoffUntil)
        {
            Log.Write("Skipping automatic apply (backoff active)");
            return;
        }
        // Our own successful apply raises DisplaySettingsChanged again; that pass finds everything
        // already matching and becomes a no-op, so the loop converges.
        ApplyNow(manual: false);
    }

    // ---- Housekeeping -----------------------------------------------------------------------------

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _debounce.Dispose();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // release the keep-awake hold
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("Exited.");
        base.ExitThreadCore();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Draws a small monitor glyph so we do not need an icon resource.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var screenBrush = new SolidBrush(Color.FromArgb(0x2E, 0x8B, 0xF0));
            using var bezel = new Pen(Color.FromArgb(0x1A, 0x1A, 0x1A), 2f);
            using var standBrush = new SolidBrush(Color.FromArgb(0x1A, 0x1A, 0x1A));
            using var pin = new SolidBrush(Color.White);

            var screen = new Rectangle(2, 4, 28, 18);
            g.FillRectangle(screenBrush, screen);
            g.DrawRectangle(bezel, screen);
            g.FillRectangle(standBrush, 14, 23, 4, 4);   // neck
            g.FillRectangle(standBrush, 8, 27, 16, 3);   // base
            g.FillEllipse(pin, 21, 7, 6, 6);             // "pinned" dot
        }
        IntPtr h = bmp.GetHicon();
        return (Icon)Icon.FromHandle(h).Clone(); // clone so the icon owns its handle
    }
}
