using System.Diagnostics;
using System.Net.Http;
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
    private readonly ContextMenuStrip _menu;

    /// <summary>Set by --screenshots: build the UI without registering startup, learning a layout or enforcing anything.</summary>
    public static bool ScreenshotMode;
    private readonly ToolStripMenuItem _diagnosticsItem;
    private DiagnosticsForm? _diagnostics;
    private readonly ToolStripMenuItem _enforceItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _keepAwakeItem;
    private readonly ToolStripMenuItem _standInItem;
    private readonly ToolStripMenuItem _installDriverItem;
    private readonly StandInManager _standIns = new();
    private readonly ToolStripMenuItem _delayMenu;
    private readonly ToolStripMenuItem _jiggleItem;
    private readonly Jiggler _jiggler = new();
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _autoUpdateItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly SynchronizationContext _ui;
    private bool _installingDriver;
    private bool _checkingUpdates;

    private static readonly (string Label, int Seconds)[] DelayPresets =
    {
        ("Immediately", 0), ("After 5 seconds", 5), ("After 15 seconds", 15), ("After 1 minute", 60), ("After 5 minutes", 300),
    };
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly System.Windows.Forms.Timer _lateSnapshot;

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
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load();
        _profile = DisplayProfile.Load();

        _persistItem = new ToolStripMenuItem("Persist current layout", null, (_, _) => Persist())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        _applyItem = new ToolStripMenuItem("Apply saved layout now", null, (_, _) => ApplyNow(manual: true));
        _showItem = new ToolStripMenuItem("Show saved layout...", null, (_, _) => ShowSaved());
        _diagnosticsItem = new ToolStripMenuItem("Show diagnostics...", null, (_, _) => ShowDiagnostics());
        _enforceItem = new ToolStripMenuItem("Enforce layout on display changes", null, (_, _) => ToggleEnforce()) { CheckOnClick = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem("Keep displays awake (never sleep)", null, (_, _) => ToggleKeepAwake()) { CheckOnClick = false };
        _jiggleItem = new ToolStripMenuItem("Jiggle mouse when idle", null, (_, _) => ToggleJiggle()) { CheckOnClick = false };
        _jiggler.IdleThreshold = TimeSpan.FromSeconds(Math.Max(10, _settings.JiggleIdleSeconds));
        _jiggler.Enabled = _settings.JiggleWhenIdle;

        // Everything about fake monitors lives in one submenu.
        _standInItem = new ToolStripMenuItem("Enabled (stand in for unplugged monitors)", null, (_, _) => ToggleStandIns()) { CheckOnClick = false };
        _delayMenu = new ToolStripMenuItem("Delay before a fake monitor appears");
        foreach (var (label, seconds) in DelayPresets)
            _delayMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetStandInDelay(seconds)) { Tag = seconds });
        _installDriverItem = new ToolStripMenuItem("Install Parsec virtual display driver...", null, (_, _) => InstallDriver());
        _standIns.Delay = TimeSpan.FromSeconds(Math.Max(0, _settings.StandInDelaySeconds));
        var fakeMenu = new ToolStripMenuItem("Fake monitors");
        fakeMenu.DropDownItems.Add(_standInItem);
        fakeMenu.DropDownItems.Add(_delayMenu);
        fakeMenu.DropDownItems.Add(new ToolStripSeparator());
        fakeMenu.DropDownItems.Add(_installDriverItem);

        _checkUpdatesItem = new ToolStripMenuItem("Check for updates now", null, (_, _) => CheckForUpdates(manual: true));
        _autoUpdateItem = new ToolStripMenuItem("Install updates automatically", null, (_, _) => ToggleAutoUpdate()) { CheckOnClick = false };

        // Layout: title / actions / behaviours / views / app settings / exit.
        var menu = _menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Monitor Anchor {Updater.Current}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_persistItem);
        menu.Items.Add(_applyItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_enforceItem);
        menu.Items.Add(_keepAwakeItem);
        menu.Items.Add(_jiggleItem);
        menu.Items.Add(fakeMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_showItem);
        menu.Items.Add(_diagnosticsItem);
        menu.Items.Add(new ToolStripMenuItem("Show log...", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_startupItem);
        menu.Items.Add(_autoUpdateItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Persist();

        _debounce = new System.Windows.Forms.Timer { Interval = DebounceMs };
        _debounce.Tick += (_, _) => OnDebounceElapsed();

        // First update check shortly after start (so a broken network does not delay startup), then daily.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds;
            if (_settings.AutoUpdate) CheckForUpdates(manual: false);
        };
        if (!ScreenshotMode) _updateTimer.Start();

        // Windows' own "remember window locations" restore can land a few seconds after a reconnect.
        _lateSnapshot = new System.Windows.Forms.Timer { Interval = 5000 };
        _lateSnapshot.Tick += (_, _) => { _lateSnapshot.Stop(); WindowSnapshot.LogNow("5 s after apply"); };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // First run: register for startup so the tool survives reboots without any extra clicks.
        if (ScreenshotMode)
        {
            // Rendering screenshots for the README: no side effects on the machine.
        }
        else if (!AppSettings.Exists)
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
        WindowSnapshot.LogNow("startup");
        Log.Write($"Started. Profile: {(_profile == null ? "none" : $"{_profile.Monitors.Count} monitor(s) from {_profile.CapturedAt}")}, enforce={_settings.Enforce}");

        if (ScreenshotMode)
        {
            // nothing to learn or enforce
        }
        else if (_profile == null)
        {
            // First run: learn whatever the layout is right now and enforce it from here on.
            var learned = DisplayManager.CaptureForProfile();
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
            var captured = DisplayManager.CaptureForProfile();
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
            // A real monitor that just came back must get its spot before we position it: retire its stand-in first.
            if (_settings.VirtualStandIns && _standIns.RemoveSurplus(_profile))
            {
                Log.Write("Stand-in retired; re-checking after the display change settles");
                ScheduleApply("stand-in removed");
                return;
            }

            var result = DisplayManager.Apply(_profile);

            if (_settings.VirtualStandIns)
            {
                var standIn = _standIns.AddAndPosition(_profile);
                if (standIn != null)
                {
                    result = new ApplyResult(result.Changed + standIn.Changed, result.Failed + standIn.Failed, result.Unmatched,
                        (result.MadeChanges || result.HadFailures ? result.Summary + Environment.NewLine : "") + standIn.Summary);
                    if (standIn.MadeChanges) ScheduleApply("stand-in changed");
                }
                if (_standIns.NextDue is { } due)
                    ScheduleApply("fake monitor due", (int)Math.Clamp(due.TotalMilliseconds + 250, 1000, int.MaxValue));
            }

            Log.Write($"Apply ({(manual ? "manual" : "auto")}): {result.Summary}");
            WindowSnapshot.LogNow("after apply");
            _lateSnapshot.Stop();
            _lateSnapshot.Start();

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

    private void ShowDiagnostics()
    {
        if (_diagnostics == null || _diagnostics.IsDisposed)
        {
            _diagnostics = new DiagnosticsForm(() => _profile);
            _diagnostics.Show();
        }
        else
        {
            _diagnostics.Refresh();
            if (_diagnostics.WindowState == FormWindowState.Minimized) _diagnostics.WindowState = FormWindowState.Normal;
            _diagnostics.Activate();
        }
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

    private void ToggleStandIns()
    {
        _settings.VirtualStandIns = !_settings.VirtualStandIns;
        _settings.Save();
        Log.Write($"VirtualStandIns = {_settings.VirtualStandIns}");
        RefreshMenu();

        if (!_settings.VirtualStandIns)
        {
            _standIns.RemoveAll();
            return;
        }
        if (!_standIns.DriverPresent)
        {
            var answer = MessageBox.Show(
                "Fake monitors need the Parsec Virtual Display Driver, which is not installed.\n\n" +
                "Download and install it now? Windows will ask for administrator approval.",
                "Monitor Anchor", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) InstallDriver();
            return;
        }
        ScheduleApply("stand-ins enabled");
    }

    private const string DriverInstallerUrl = "https://builds.parsec.app/vdd/parsec-vdd-0.41.0.0.exe";

    /// <summary>Downloads the Parsec Virtual Display Driver installer and runs it silently (elevation prompt).</summary>
    private void InstallDriver()
    {
        if (_standIns.DriverPresent)
        {
            _tray.ShowBalloonTip(3000, "Monitor Anchor", "The Parsec virtual display driver is already installed.", ToolTipIcon.Info);
            return;
        }
        if (_installingDriver) return;

        if (MessageBox.Show(
                "Monitor Anchor will download the Parsec Virtual Display Driver installer (about 2 MB) from\n" +
                DriverInstallerUrl + "\nand run it silently. Windows will ask for administrator approval.\n\nContinue?",
                "Install virtual display driver", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _installingDriver = true;
        _installDriverItem.Enabled = false;
        _tray.ShowBalloonTip(3000, "Monitor Anchor", "Downloading the Parsec virtual display driver...", ToolTipIcon.Info);

        Task.Run(async () =>
        {
            string message; ToolTipIcon icon;
            try
            {
                string installer = Path.Combine(DisplayProfile.ConfigDir, Path.GetFileName(DriverInstallerUrl));
                Directory.CreateDirectory(DisplayProfile.ConfigDir);
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                {
                    var bytes = await http.GetByteArrayAsync(DriverInstallerUrl);
                    await File.WriteAllBytesAsync(installer, bytes);
                }
                Log.Write($"Driver installer downloaded to {installer}; launching with /S");

                using var proc = Process.Start(new ProcessStartInfo(installer, "/S") { UseShellExecute = true, Verb = "runas" })
                    ?? throw new InvalidOperationException("installer did not start");
                await proc.WaitForExitAsync();
                Log.Write($"Driver installer exited with code {proc.ExitCode}");

                // Give Plug and Play a moment to bring the device up.
                for (int i = 0; i < 20 && !_standIns.DriverPresent; i++) await Task.Delay(500);

                if (_standIns.DriverPresent)
                {
                    message = "Parsec virtual display driver installed. Fake monitors are ready.";
                    icon = ToolTipIcon.Info;
                }
                else
                {
                    message = $"The installer finished (exit code {proc.ExitCode}) but the driver is not visible yet. A reboot may be needed.";
                    icon = ToolTipIcon.Warning;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Driver install failed: " + ex);
                message = "Driver install failed: " + ex.Message;
                icon = ToolTipIcon.Error;
            }

            _ui.Post(_ =>
            {
                _installingDriver = false;
                _tray.ShowBalloonTip(5000, "Monitor Anchor", message, icon);
                RefreshMenu();
                if (_settings.VirtualStandIns && _standIns.DriverPresent) ScheduleApply("driver installed");
            }, null);
        });
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

    private LogForm? _logForm;

    /// <summary>Opens the live log viewer (tails the file, auto-scrolls); one window, re-activated if already open.</summary>
    private void OpenLog()
    {
        if (_logForm == null || _logForm.IsDisposed)
        {
            _logForm = new LogForm();
            _logForm.Show();
        }
        else
        {
            if (_logForm.WindowState == FormWindowState.Minimized) _logForm.WindowState = FormWindowState.Normal;
            _logForm.Activate();
        }
    }

    private void RefreshMenu()
    {
        bool hasProfile = _profile != null;
        _applyItem.Enabled = hasProfile;
        _enforceItem.Checked = _settings.Enforce;
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _standInItem.Checked = _settings.VirtualStandIns;
        bool driver = _standIns.DriverPresent;
        _installDriverItem.Text = driver ? "Parsec virtual display driver: installed" : "Install Parsec virtual display driver...";
        _installDriverItem.Enabled = !driver && !_installingDriver;
        foreach (ToolStripMenuItem item in _delayMenu.DropDownItems)
            item.Checked = (int)item.Tag! == _settings.StandInDelaySeconds;
        _autoUpdateItem.Checked = _settings.AutoUpdate;
        _jiggleItem.Checked = _settings.JiggleWhenIdle;
        _checkUpdatesItem.Enabled = !_checkingUpdates;
        _checkUpdatesItem.Text = _checkingUpdates ? "Checking for updates..." : "Check for updates now";
        _startupItem.Checked = Startup.IsEnabled();

        string state = !hasProfile ? "no layout saved"
                     : _settings.Enforce ? $"enforcing {_profile!.Monitors.Count} monitor(s)" +
                                           (_standIns.ActiveStandIns > 0 ? $", {_standIns.ActiveStandIns} fake" : "")
                     : "paused";
        _tray.Text = Truncate($"Monitor Anchor - {state}", 63); // NotifyIcon.Text is limited to 63 chars
    }

    // ---- Display change handling -----------------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Capture what Windows just did to the windows, before we touch anything.
        WindowSnapshot.LogNow("right after display change");
        ScheduleApply("display settings changed");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleApply("resume from sleep");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteDisconnect)
            ScheduleApply(e.Reason.ToString());
    }

    private void ScheduleApply(string reason, int delayMs = DebounceMs)
    {
        if (!_settings.Enforce || _profile == null) return;
        Log.Write($"Display change detected ({reason}); checking in {delayMs} ms");
        _debounce.Stop();
        _debounce.Interval = delayMs;
        _debounce.Start();
    }

    private void ToggleJiggle()
    {
        _settings.JiggleWhenIdle = !_settings.JiggleWhenIdle;
        _settings.Save();
        _jiggler.Enabled = _settings.JiggleWhenIdle;
        Log.Write($"JiggleWhenIdle = {_settings.JiggleWhenIdle} (after {_settings.JiggleIdleSeconds} s idle)");
        RefreshMenu();
    }

    private void SetStandInDelay(int seconds)
    {
        _settings.StandInDelaySeconds = seconds;
        _settings.Save();
        _standIns.Delay = TimeSpan.FromSeconds(seconds);
        Log.Write($"StandInDelaySeconds = {seconds}");
        RefreshMenu();
    }

    // ---- Updates ------------------------------------------------------------------------------------

    private void ToggleAutoUpdate()
    {
        _settings.AutoUpdate = !_settings.AutoUpdate;
        _settings.Save();
        Log.Write($"AutoUpdate = {_settings.AutoUpdate}");
        RefreshMenu();
    }

    /// <summary>Checks GitHub for a newer release; installs it when auto-update is on or the user agrees.</summary>
    private void CheckForUpdates(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        RefreshMenu();

        Task.Run(async () =>
        {
            Updater.Release? release = null;
            string? error = null;
            try { release = await Updater.CheckAsync(); }
            catch (Exception ex) { error = ex.Message; }

            _ui.Post(_ =>
            {
                _checkingUpdates = false;
                RefreshMenu();
                if (error != null)
                {
                    Log.Write("Update check failed: " + error);
                    if (manual) _tray.ShowBalloonTip(4000, "Monitor Anchor", "Could not check for updates: " + error, ToolTipIcon.Warning);
                    return;
                }
                if (release == null || release.Version <= Updater.Current)
                {
                    Log.Write($"Update check: {Updater.Current} is current" + (release == null ? " (no matching release asset)" : $" (latest {release.Version})"));
                    if (manual) _tray.ShowBalloonTip(3000, "Monitor Anchor", $"You have the latest version ({Updater.Current}).", ToolTipIcon.Info);
                    return;
                }

                Log.Write($"Update available: {release.Version} (running {Updater.Current})");
                bool install = _settings.AutoUpdate;
                if (!install && manual)
                {
                    install = MessageBox.Show($"Monitor Anchor {release.Version} is available (you have {Updater.Current}).\n\nDownload and install it now? The app restarts itself afterwards.",
                        "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                }
                if (install) InstallUpdate(release);
                else _tray.ShowBalloonTip(5000, "Update available", $"Monitor Anchor {release.Version} is available. Use \"Check for updates now\" to install it.", ToolTipIcon.Info);
            }, null);
        });
    }

    private void InstallUpdate(Updater.Release release)
    {
        _checkingUpdates = true;
        RefreshMenu();
        _tray.ShowBalloonTip(3000, "Monitor Anchor", $"Downloading version {release.Version}...", ToolTipIcon.Info);

        Task.Run(async () =>
        {
            string? path = null, error = null;
            try { path = await Updater.DownloadAsync(release); }
            catch (Exception ex) { error = ex.Message; }

            _ui.Post(_ =>
            {
                _checkingUpdates = false;
                RefreshMenu();
                if (path == null)
                {
                    Log.Write("Update download failed: " + error);
                    _tray.ShowBalloonTip(5000, "Monitor Anchor", "Update failed: " + error, ToolTipIcon.Error);
                    return;
                }
                try
                {
                    _tray.ShowBalloonTip(3000, "Monitor Anchor", $"Installing version {release.Version} and restarting...", ToolTipIcon.Info);
                    if (Updater.SwapAndRestart(path)) ExitThread();
                }
                catch (Exception ex)
                {
                    Log.Write("Update install failed: " + ex);
                    _tray.ShowBalloonTip(5000, "Monitor Anchor", "Update install failed: " + ex.Message, ToolTipIcon.Error);
                }
            }, null);
        });
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
        _lateSnapshot.Dispose();
        _updateTimer.Dispose();
        _jiggler.Dispose();
        _standIns.Dispose(); // retires any fake monitors
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // release the keep-awake hold
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("Exited.");
        base.ExitThreadCore();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string SampleLog()
    {
        var t = DateTime.Now.AddMinutes(-3);
        string S(int s) => t.AddSeconds(s).ToString("yyyy-MM-dd HH:mm:ss");
        return string.Join(Environment.NewLine, new[]
        {
            $"{S(0)} Started. Profile: 2 monitor(s) from {t.AddDays(-4):MM/dd/yyyy HH:mm:ss}, enforce=True",
            $"{S(0)} Display change detected (startup); checking in 1500 ms",
            $"{S(2)} Apply (auto): Layout already matches the saved profile.",
            $"{S(41)} Display change detected (display settings changed); checking in 1500 ms",
            $"{S(43)} No saved settings for Generic PnP Monitor [\\\\.\\DISPLAY1] 2880x1920 @ 120Hz, pos (0,0), rot 0°, primary",
            $"{S(43)} Apply (auto): Layout already matches the saved profile.",
            $"{S(48)} Display change detected (display settings changed); checking in 1500 ms",
            $"{S(50)} SetDisplayConfig(apply supplied, 2 path(s), flags 0x2010): success",
            $"{S(50)} Re-enabling VG259QM: success (targeted)",
            $"{S(50)} Generic PnP Monitor (\\\\.\\DISPLAY10): 1920x1080@60 -> 1920x1080@120 pos(0,-1080) : success",
            $"{S(50)} Commit: success",
            $"{S(50)} Apply (auto): 2 change(s) applied, 0 failed.",
            "Re-enabling VG259QM: success (targeted)",
            "Generic PnP Monitor (\\\\.\\DISPLAY10): 1920x1080@60 -> 1920x1080@120 pos(0,-1080) : success",
            $"{S(51)} Display change detected (display settings changed); checking in 1500 ms",
            $"{S(52)} Apply (auto): Layout already matches the saved profile.",
            $"{S(80)} Update check: {Updater.Current} is current (latest {Updater.Current})",
            "",
        });
    }

    /// <summary>Copies what is actually on screen at <paramref name="origin"/> (the window must be shown and on top).</summary>
    private static void CaptureScreen(Bitmap target, Point origin)
    {
        using var g = Graphics.FromImage(target);
        g.CopyFromScreen(origin, Point.Empty, target.Size);
    }

    /// <summary>Renders the tray menu (with the Fake monitors submenu open) and the diagnostics window to PNG files.</summary>
    public void SaveScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        RefreshMenu();

        _menu.Show(new Point(200, 200));
        Application.DoEvents();
        var fake = _menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "Fake monitors");
        fake.ShowDropDown();
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Application.DoEvents();

        // Compose menu + open submenu into one image.
        var mb = _menu.Bounds; var sb = fake.DropDown.Bounds;
        var union = Rectangle.Union(mb, sb);
        using (var bmp = new Bitmap(union.Width, union.Height))
        {
            using (var menuBmp = new Bitmap(mb.Width, mb.Height))
            using (var subBmp = new Bitmap(sb.Width, sb.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                _menu.DrawToBitmap(menuBmp, new Rectangle(0, 0, mb.Width, mb.Height));
                fake.DropDown.DrawToBitmap(subBmp, new Rectangle(0, 0, sb.Width, sb.Height));
                g.Clear(Color.Transparent);
                g.DrawImage(menuBmp, mb.X - union.X, mb.Y - union.Y);
                g.DrawImage(subBmp, sb.X - union.X, sb.Y - union.Y);
            }
            bmp.Save(Path.Combine(dir, "menu.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        fake.HideDropDown();
        _menu.Close();

        using var form = new DiagnosticsForm(() => _profile);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(50, 50);
        form.TopMost = true; // we are not the foreground app, so force the window above whatever is there
        form.Show();
        form.Activate();
        form.Refresh();
        form.PerformLayout();
        Application.DoEvents();
        System.Threading.Thread.Sleep(600);
        Application.DoEvents();
        // Crop above the EDID section so monitor serial numbers stay out of the published image.
        int height = Math.Min(form.Height, 436);
        using (var bmp = new Bitmap(form.Width, height))
        {
            CaptureScreen(bmp, form.Location);
            bmp.Save(Path.Combine(dir, "diagnostics.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Close();

        // A representative sample rather than the real log, which lists the user's window titles.
        string sample = Path.Combine(Path.GetTempPath(), "MonitorAnchor-sample-log.txt");
        File.WriteAllText(sample, SampleLog());
        using var logForm = new LogForm(sample);
        logForm.StartPosition = FormStartPosition.Manual;
        logForm.Location = new Point(50, 50);
        logForm.Size = new Size(1000, 420);
        logForm.TopMost = true;
        logForm.Show();
        logForm.Activate();
        logForm.PerformLayout();
        Application.DoEvents();
        System.Threading.Thread.Sleep(700); // let the tail timer tick once
        Application.DoEvents();
        using (var bmp = new Bitmap(logForm.Width, logForm.Height - 10)) // the DWM frame reports a little taller than it paints
        {
            CaptureScreen(bmp, logForm.Location);
            bmp.Save(Path.Combine(dir, "log.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        logForm.Close();
    }

    /// <summary>The app icon (assets/icon.ico, embedded at build time), falling back to a drawn glyph.</summary>
    public static Icon AppIcon()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("MonitorAnchor.icon.ico");
            if (stream != null) return new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Write("Embedded icon failed to load: " + ex.Message);
        }
        return MakeIcon();
    }

    /// <summary>Draws a small monitor glyph, used only if the embedded icon is missing.</summary>
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
