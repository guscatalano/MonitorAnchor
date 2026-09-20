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
    private readonly AppState _state;
    private readonly KvmDetector _kvm;
    private readonly DeviceWatcher _devices;
    private Presence? _presence;

    /// <summary>Set by --screenshots: build the UI without registering startup, learning a layout or enforcing anything.</summary>
    public static bool ScreenshotMode;
    private readonly ToolStripMenuItem _diagnosticsItem;
    private readonly ToolStripMenuItem _warningsItem;
    private WarningsForm? _warnings;
    private Updater.Release? _pendingUpdate;
    private DiagnosticsForm? _diagnostics;
    private readonly ToolStripMenuItem _enforceItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _keepAwakeItem;
    private readonly ToolStripMenuItem _restoreWindowsItem;
    private readonly ToolStripMenuItem _userChangesMenu;
    private readonly System.Windows.Forms.Timer _userChange;
    private readonly WindowMemory _memory = new();
    private readonly System.Windows.Forms.Timer _memoryTimer;
    private HashSet<string> _lastActiveMonitorIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _restorePending;
    private DateTime _lastDisplayChange = DateTime.MinValue;
    private readonly ToolStripMenuItem _standInItem;
    private readonly ToolStripMenuItem _installDriverItem;
    private readonly StandInManager _standIns = new();
    private readonly ToolStripMenuItem _delayMenu;
    private readonly ToolStripMenuItem _jiggleItem;
    private readonly ToolStripMenuItem _rdpItem;
    private readonly ToolStripMenuItem _tourItem;
    private readonly ToolStripMenuItem _mouseOffItem;
    private readonly ToolStripMenuItem _idleAfterMenu;
    private readonly ToolStripMenuItem _wuStatusItem;
    private readonly ToolStripMenuItem _wuMenu;
    private readonly Jiggler _jiggler = new();
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _installItem;
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
    private readonly LayoutStore _store;
    /// <summary>The saved layout that fits the monitors connected right now; null when none does.</summary>
    private DisplayProfile? _profile;
    private readonly ToolStripMenuItem _layoutsMenu = new("Layouts");

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
        _store = LayoutStore.Load();
        _profile = _store.SelectForCurrentMonitors();
        _state = AppState.Load();
        _kvm = new KvmDetector(_state);
        _devices = new DeviceWatcher();
        _devices.DeviceChanged += _kvm.OnDevice;
        _devices.DisplayPowerChanged += state => Log.Write(state switch
        {
            0 => "Displays turned off by Windows (power/idle)",
            1 => "Displays turned on by Windows",
            2 => "Displays dimmed by Windows",
            _ => $"Display power state {state}",
        });
        if (!ScreenshotMode) { InputTracker.Start(); _presence = new Presence(); }
        Diagnostics.LiveStatus = () => (_settings.RestoreWindows ? "on: " : "off: ") + _memory.Status + Environment.NewLine +
                                       "  presence: " + (_presence?.Status ?? "n/a");

        _persistItem = new ToolStripMenuItem("Persist current layout", null, (_, _) => Persist())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        _applyItem = new ToolStripMenuItem("Apply saved layout now", null, (_, _) => ApplyNow(manual: true));
        _showItem = new ToolStripMenuItem("Show saved layout...", null, (_, _) => ShowSaved());
        _diagnosticsItem = new ToolStripMenuItem("Show diagnostics...", null, (_, _) => ShowDiagnostics());
        _warningsItem = new ToolStripMenuItem("Show warnings...", null, (_, _) => ShowWarnings());
        _enforceItem = new ToolStripMenuItem("Enforce layout on display changes", null, (_, _) => ToggleEnforce()) { CheckOnClick = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem("Keep displays awake (never sleep)", null, (_, _) => ToggleKeepAwake()) { CheckOnClick = false };
        _restoreWindowsItem = new ToolStripMenuItem("When a monitor returns, move windows back", null, (_, _) => ToggleRestoreWindows()) { CheckOnClick = false };
        _userChangesMenu = new ToolStripMenuItem("When I change display settings myself");
        foreach (var (label, mode) in new[] { ("Update the saved layout (after 20 s)", "update"), ("Ask me with a notification", "ask"), ("Put the saved layout back", "revert") })
            _userChangesMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetUserChanges(mode)) { Tag = mode });
        _userChange = new System.Windows.Forms.Timer { Interval = UserChangeGraceMs };
        _userChange.Tick += (_, _) => OnUserChangeSettled();
        // Everything that happens while you are idle lives in one submenu. The mouse choice is one-of-three
        // (hovering over every window already includes a nudge); poking Remote Desktop is separate because it
        // also takes focus and presses a key.
        if (_settings.WiggleAllWindows) _settings.JiggleWhenIdle = false; // hover subsumes nudge
        _jiggler.IdleThreshold = TimeSpan.FromSeconds(Math.Max(10, _settings.JiggleIdleSeconds));
        _jiggler.JiggleMouse = _settings.JiggleWhenIdle;
        _jiggler.WiggleAllWindows = _settings.WiggleAllWindows;
        _jiggler.KeepRdpAlive = _settings.KeepRdpAlive;

        _mouseOffItem = new ToolStripMenuItem("Leave the mouse alone", null, (_, _) => SetMouseMode(nudge: false, hover: false)) { CheckOnClick = false };
        _jiggleItem = new ToolStripMenuItem("Nudge the mouse in place", null, (_, _) => SetMouseMode(nudge: true, hover: false)) { CheckOnClick = false };
        _tourItem = new ToolStripMenuItem("Hover over every window (includes a nudge)", null, (_, _) => SetMouseMode(nudge: false, hover: true)) { CheckOnClick = false };
        _rdpItem = new ToolStripMenuItem("Also poke Remote Desktop sessions (focus + F15)", null, (_, _) => ToggleRdp()) { CheckOnClick = false };
        _idleAfterMenu = new ToolStripMenuItem("Count as idle after");
        foreach (var (label, seconds) in new[] { ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("5 minutes", 300), ("10 minutes", 600) })
            _idleAfterMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetIdleSeconds(seconds)) { Tag = seconds });
        var idleMenu = new ToolStripMenuItem("When idle");
        idleMenu.DropDownItems.Add(_mouseOffItem);
        idleMenu.DropDownItems.Add(_jiggleItem);
        idleMenu.DropDownItems.Add(_tourItem);
        idleMenu.DropDownItems.Add(new ToolStripSeparator());
        idleMenu.DropDownItems.Add(_rdpItem);
        idleMenu.DropDownItems.Add(new ToolStripSeparator());
        idleMenu.DropDownItems.Add(_idleAfterMenu);

        // Windows Update pause lives in its own submenu; every action prompts for administrator approval.
        _wuStatusItem = new ToolStripMenuItem("Status") { Enabled = false };
        _wuMenu = new ToolStripMenuItem("Windows Update");
        _wuMenu.DropDownItems.Add(_wuStatusItem);
        _wuMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (label, days) in new[] { ("Pause for 1 week", 7), ("Pause for 5 weeks", 35), ("Pause for 6 months", 182), ("Pause for 1 year", 365) })
            _wuMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => PauseUpdates(days)));
        _wuMenu.DropDownItems.Add(new ToolStripSeparator());
        _wuMenu.DropDownItems.Add(new ToolStripMenuItem("Resume updates", null, (_, _) => PauseUpdates(0)));

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

        _installItem = new ToolStripMenuItem("Install to Programs folder...", null, (_, _) => InstallOrUninstall());
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates now", null, (_, _) => CheckForUpdates(manual: true));
        _autoUpdateItem = new ToolStripMenuItem("Install updates automatically", null, (_, _) => ToggleAutoUpdate()) { CheckOnClick = false };

        // Layout: title / actions / behaviours / views / app settings / exit.
        var menu = _menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"About Monitor Anchor {Updater.Current}...", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_persistItem);
        menu.Items.Add(_applyItem);
        menu.Items.Add(_layoutsMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_enforceItem);
        menu.Items.Add(_userChangesMenu);
        menu.Items.Add(_restoreWindowsItem);
        menu.Items.Add(_keepAwakeItem);
        menu.Items.Add(idleMenu);
        menu.Items.Add(fakeMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_showItem);
        menu.Items.Add(_warningsItem);
        menu.Items.Add(_diagnosticsItem);
        menu.Items.Add(new ToolStripMenuItem("Show log...", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_startupItem);
        menu.Items.Add(_autoUpdateItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(_installItem);
        menu.Items.Add(_wuMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) => { try { CollectWarnings(); } catch { /* count stays stale */ } RefreshMenu(); };

        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Persist();
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();

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

        // Windows' own "remember window locations" restore can land a few seconds after a reconnect; ours runs after it.
        _lateSnapshot = new System.Windows.Forms.Timer { Interval = 5000 };
        _lateSnapshot.Tick += (_, _) =>
        {
            _lateSnapshot.Stop();
            WindowSnapshot.LogNow("5 s after apply");
            if (_restorePending && _settings.RestoreWindows)
            {
                var moved = _memory.Restore();
                Log.Write(moved.Count == 0 ? "Window memory: nothing to move back" : $"Window memory: moved {moved.Count} window(s) back:" + string.Concat(moved.Select(l => Environment.NewLine + "    " + l)));
                if (moved.Count > 0) _tray.ShowBalloonTip(3000, "Windows restored", $"{moved.Count} window(s) moved back to their monitor.", ToolTipIcon.Info);
            }
            _restorePending = false;
        };

        // Remember window placements every 20 s while the layout is intact and has been quiet for a while.
        _memoryTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _memoryTimer.Tick += (_, _) =>
        {
            if (!_settings.RestoreWindows || _profile == null || _restorePending) return;
            if (DateTime.Now - _lastDisplayChange < TimeSpan.FromSeconds(30)) return;
            try { _memory.Snapshot(_profile); } catch (Exception ex) { Log.Write("Window memory snapshot failed: " + ex.Message); }
        };
        if (!ScreenshotMode) _memoryTimer.Start();

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
        Log.Write("System:" + Environment.NewLine + SystemInfo.Describe());
        var health = LinkInfo.CurrentWarnings();
        CollectWarnings(); // seeds the count shown in the menu
        if (health.Count > 0)
        {
            Log.Write("Link health:" + string.Concat(health.Select(h => Environment.NewLine + "  ! " + h)));
            if (!ScreenshotMode)
                _tray.ShowBalloonTip(8000, "Display link warning", health[0].Length > 250 ? health[0][..250] + "…" : health[0], ToolTipIcon.Warning);
        }
        _kvm.Detected += (kind, detail) =>
        {
            if (kind != "Display link retrained") return;
            var advice = LinkInfo.CurrentWarnings();
            _tray.ShowBalloonTip(8000, "Display link retrained",
                advice.Count > 0 ? "The link dropped and came back. Likely cause: " + (advice[0].Length > 200 ? advice[0][..200] + "…" : advice[0])
                                 : "The link dropped and came back. See diagnostics for the link details.",
                ToolTipIcon.Warning);
        };
        // The offer must run inside the message loop, not here in the constructor: accepting it exits the app,
        // and an ExitThread before Application.Run has started is silently lost, leaving a ghost instance that
        // holds the single-instance lock while the installed copy waits for it and gives up.
        var offer = new System.Windows.Forms.Timer { Interval = 1500 };
        offer.Tick += (_, _) => { offer.Dispose(); OfferInstall(); };
        if (!ScreenshotMode) offer.Start();
        Log.Write($"Started. {_store.Layouts.Count} saved layout(s); active: {(_profile == null ? "none matches the connected monitors" : $"\"{_profile.Name}\" ({_profile.Monitors.Count} monitor(s), captured {_profile.CapturedAt:g})")}; enforce={_settings.Enforce}");

        if (ScreenshotMode)
        {
            // nothing to learn or enforce
        }
        else if (_store.Layouts.Count == 0)
        {
            // First run: learn whatever the layout is right now and enforce it from here on.
            var learned = DisplayManager.CaptureForProfile();
            if (learned.Monitors.Count > 0)
            {
                _profile = _store.Upsert(learned);
                Log.Write($"Learned initial layout \"{learned.Name}\":" + Environment.NewLine + learned);
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
            bool existed = _store.Layouts.Any(l => LayoutStore.Signature(l) == LayoutStore.Signature(captured));
            _store.Upsert(captured);
            RefreshActiveLayout();
            Log.Write($"Persisted layout \"{captured.Name}\" ({(existed ? "updated" : "new")}):" + Environment.NewLine + captured);
            _tray.ShowBalloonTip(4000, existed ? "Layout updated" : "Layout saved",
                $"\"{captured.Name}\": {captured.Monitors.Count} monitor(s). It is restored whenever these monitors are connected and something changes.",
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
        RefreshActiveLayout();
        if (_profile == null)
        {
            if (manual)
                _tray.ShowBalloonTip(4000, "Monitor Anchor", _store.Layouts.Count == 0
                    ? "Nothing saved yet. Choose \"Persist current layout\" first."
                    : "No saved layout matches the monitors connected right now. Arrange them and choose \"Persist current layout\" to add one.", ToolTipIcon.Warning);
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
            if (result.MadeChanges) _ownChangeUntil = DateTime.Now.AddSeconds(5); // the change events that follow are ours

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

            // Did a monitor come back? Then windows may need moving once things settle.
            var activeIds = DisplayManager.Capture().Monitors.Where(m => m.IsActive).Select(m => m.MonitorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (activeIds.Except(_lastActiveMonitorIds).Any() && _lastActiveMonitorIds.Count > 0) _restorePending = true;
            _lastActiveMonitorIds = activeIds;
        _memory.Note(activeIds.Count);

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

    // ---- Layouts ------------------------------------------------------------------------------------

    /// <summary>Re-selects the layout for the monitors connected now and logs when it changes.</summary>
    private void RefreshActiveLayout()
    {
        var before = _profile;
        _profile = _store.SelectForCurrentMonitors();
        if (!ReferenceEquals(before, _profile))
            Log.Write($"Active layout: {(_profile == null ? "none matches the connected monitors" : $"\"{_profile.Name}\"")}");
    }

    private void BuildLayoutsMenu()
    {
        _layoutsMenu.DropDownItems.Clear();
        if (_store.Layouts.Count == 0)
        {
            _layoutsMenu.DropDownItems.Add(new ToolStripMenuItem("No layouts saved yet") { Enabled = false });
            return;
        }
        foreach (var layout in _store.Layouts)
        {
            var l = layout;
            var item = new ToolStripMenuItem($"{l.Name}  ({l.Monitors.Count} monitor{(l.Monitors.Count == 1 ? "" : "s")})") { Checked = ReferenceEquals(l, _profile) };
            item.DropDownItems.Add(new ToolStripMenuItem("Apply now", null, (_, _) => ApplyLayout(l)));
            item.DropDownItems.Add(new ToolStripMenuItem("Rename...", null, (_, _) => RenameLayout(l)));
            item.DropDownItems.Add(new ToolStripMenuItem("Delete", null, (_, _) => DeleteLayout(l)));
            _layoutsMenu.DropDownItems.Add(item);
        }
        _layoutsMenu.DropDownItems.Add(new ToolStripSeparator());
        _layoutsMenu.DropDownItems.Add(new ToolStripMenuItem("The layout whose monitors are all connected is applied; more monitors win.") { Enabled = false });
    }

    /// <summary>Pushes a specific layout regardless of which one is active; monitors it names that are absent are skipped.</summary>
    private void ApplyLayout(DisplayProfile layout)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            var result = DisplayManager.Apply(layout);
            Log.Write($"Apply layout \"{layout.Name}\" (manual): {result.Summary}");
            _tray.ShowBalloonTip(4000, $"Layout \"{layout.Name}\"", result.Summary, result.HadFailures ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        finally { _applying = false; }
    }

    private void RenameLayout(DisplayProfile layout)
    {
        string name = Microsoft.VisualBasic.Interaction.InputBox("New name for this layout:", "Rename layout", layout.Name);
        if (string.IsNullOrWhiteSpace(name) || name == layout.Name) return;
        _store.Rename(layout, name);
        Log.Write($"Renamed layout to \"{layout.Name}\"");
        RefreshMenu();
    }

    private void DeleteLayout(DisplayProfile layout)
    {
        if (MessageBox.Show($"Delete layout \"{layout.Name}\"?\n\n{layout}", "Monitor Anchor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _store.Remove(layout);
        Log.Write($"Deleted layout \"{layout.Name}\"");
        RefreshActiveLayout();
        RefreshMenu();
    }

    private void ShowSaved()
    {
        var sb = new System.Text.StringBuilder();
        if (_store.Layouts.Count == 0) sb.AppendLine("No layout has been saved yet.");
        foreach (var l in _store.Layouts)
        {
            sb.AppendLine($"{l.Name}{(ReferenceEquals(l, _profile) ? "   (active now)" : "")}   saved {l.CapturedAt:g}");
            sb.AppendLine(l.ToString());
            sb.AppendLine();
        }
        if (_store.Layouts.Count > 0 && _profile == null) sb.AppendLine("None of these matches the monitors connected right now.").AppendLine();
        sb.Append("File: ").Append(LayoutStore.Path);
        MessageBox.Show(sb.ToString(), "Monitor Anchor - saved layouts", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private AboutForm? _about;

    private void ShowAbout()
    {
        if (_about == null || _about.IsDisposed)
        {
            _about = new AboutForm();
            _about.Show();
        }
        else
        {
            _about.Activate();
        }
    }

    private int? _cachedWarningCount;

    /// <summary>Every current concern, worded with what to do. Link health is queried live; the rest is app state.</summary>
    private List<string> CollectWarnings()
    {
        var list = new List<string>();
        try { list.AddRange(LinkInfo.CurrentWarnings()); }
        catch (Exception ex) { list.Add("Link health could not be checked: " + ex.Message); }

        if (_state.LinkRetrains > 0)
            list.Add($"A display link has retrained {_state.LinkRetrains} time(s), last at {_state.LastLinkRetrain:g}. That is a flicker or blank seen from Windows' side; the link warnings above are the usual cause. The count is in the log and diagnostics.");

        if (_store.Layouts.Count == 0)
            list.Add("No layout is saved yet, so nothing is being enforced. Arrange the monitors and choose \"Persist current layout\".");
        else if (_profile == null)
            list.Add("No saved layout matches the monitors connected right now, so nothing is being enforced for this set. Arrange them and choose \"Persist current layout\" to add one.");
        if (!_settings.Enforce)
            list.Add("Enforcement is switched off (\"Enforce layout on display changes\"), so the saved layout will not be restored.");
        if (DateTime.Now < _backoffUntil)
            list.Add($"Automatic apply is paused until {_backoffUntil:T} after three failures in a row; see the log for the driver's rejection.");
        if (_settings.VirtualStandIns && !_standIns.DriverPresent)
            list.Add("Fake monitors are enabled but the Parsec Virtual Display Driver is not installed, so no stand-in can be created. Use Fake monitors > Install.");
        if (_pendingUpdate != null && _pendingUpdate.Version > Updater.Current)
            list.Add($"Monitor Anchor {_pendingUpdate.Version} is available and automatic updates are off. Use \"Check for updates now\" to install it.");
        if (!Startup.IsEnabled())
            list.Add("The app is not set to start with Windows, so the layout will not be enforced after a reboot until you launch it.");

        _cachedWarningCount = list.Count;
        return list;
    }

    private void ShowWarnings()
    {
        if (_warnings == null || _warnings.IsDisposed)
        {
            _warnings = new WarningsForm(CollectWarnings) { OpenDiagnostics = ShowDiagnostics };
            _warnings.Show();
        }
        else
        {
            _warnings.Refresh();
            if (_warnings.WindowState == FormWindowState.Minimized) _warnings.WindowState = FormWindowState.Normal;
            _warnings.Activate();
        }
    }

    private void ShowDiagnostics()
    {
        if (_diagnostics == null || _diagnostics.IsDisposed)
        {
            _diagnostics = new DiagnosticsForm(() => _store, _kvm.Verdict);
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

    private void ToggleRestoreWindows()
    {
        _settings.RestoreWindows = !_settings.RestoreWindows;
        _settings.Save();
        Log.Write($"RestoreWindows = {_settings.RestoreWindows}");
        if (_settings.RestoreWindows && _profile != null) { try { _memory.Snapshot(_profile); } catch { /* next tick */ } }
        RefreshMenu();
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
        if (Startup.ManagedByWindows)
        {
            Startup.OpenWindowsStartupSettings(); // packaged: the StartupTask is toggled in Settings > Apps > Startup
            return;
        }
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
        BuildLayoutsMenu();
        _enforceItem.Checked = _settings.Enforce;
        foreach (ToolStripMenuItem item in _userChangesMenu.DropDownItems)
            item.Checked = (string)item.Tag! == _settings.UserChanges;
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _restoreWindowsItem.Checked = _settings.RestoreWindows;
        _standInItem.Checked = _settings.VirtualStandIns;
        bool driver = _standIns.DriverPresent;
        _installDriverItem.Text = driver ? "Parsec virtual display driver: installed" : "Install Parsec virtual display driver...";
        _installDriverItem.Enabled = !driver && !_installingDriver;
        foreach (ToolStripMenuItem item in _delayMenu.DropDownItems)
            item.Checked = (int)item.Tag! == _settings.StandInDelaySeconds;
        _autoUpdateItem.Checked = _settings.AutoUpdate;
        _mouseOffItem.Checked = !_settings.JiggleWhenIdle && !_settings.WiggleAllWindows;
        _jiggleItem.Checked = _settings.JiggleWhenIdle;
        _tourItem.Checked = _settings.WiggleAllWindows;
        _rdpItem.Checked = _settings.KeepRdpAlive;
        foreach (ToolStripMenuItem item in _idleAfterMenu.DropDownItems)
            item.Checked = (int)item.Tag! == _settings.JiggleIdleSeconds;
        var pausedUntil = WindowsUpdate.PausedUntil();
        _wuStatusItem.Text = pausedUntil == null ? "Updates are not paused" : $"Paused until {pausedUntil:g}";
        _checkUpdatesItem.Enabled = !_checkingUpdates;
        _checkUpdatesItem.Text = _checkingUpdates ? "Checking for updates..." : "Check for updates now";
        int warningCount = _cachedWarningCount ?? 0;
        _warningsItem.Text = warningCount > 0 ? $"Show warnings ({warningCount})..." : "Show warnings...";
        _warningsItem.Font = warningCount > 0 ? new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) : null;
        _installItem.Text = Installer.IsInstalled ? "Uninstall..." : "Install to Programs folder...";
        _startupItem.Checked = Startup.IsEnabled();
        _startupItem.Text = Startup.ManagedByWindows ? "Start with Windows (managed in Settings > Apps > Startup)..." : "Start with Windows";
        _installItem.Visible = !Packaged.IsPackaged;

        string state = _store.Layouts.Count == 0 ? "no layout saved"
                     : !hasProfile ? "no layout for these monitors"
                     : _settings.Enforce ? $"{_profile!.Name}" +
                                           (_standIns.ActiveStandIns > 0 ? $", {_standIns.ActiveStandIns} fake" : "")
                     : "paused";
        _tray.Text = Truncate($"Monitor Anchor - {state}", 63); // NotifyIcon.Text is limited to 63 chars
    }

    // ---- Display change handling -----------------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _lastDisplayChange = DateTime.Now;
        var (removed, added) = _kvm.OnDisplayChange();
        // Capture what Windows just did to the windows, before we touch anything.
        WindowSnapshot.LogNow("right after display change");

        // Same monitors, a person at the keyboard, and not our own apply: that is a deliberate change (yours, or an
        // app you launched), not Windows shuffling things. Fighting it makes settings impossible to change.
        bool ours = DateTime.Now < _ownChangeUntil;
        bool humanPresent = InputTracker.HumanIdleTime() < TimeSpan.FromSeconds(60);
        if (removed + added == 0 && humanPresent && !ours && _settings.Enforce && _profile != null && _settings.UserChanges != "revert")
        {
            Log.Write($"Display settings changed with the same monitors while you were active: treating as intentional ({_settings.UserChanges}); checking in {UserChangeGraceMs / 1000} s");
            _userChange.Stop();
            _userChange.Start();
            return;
        }
        ScheduleApply("display settings changed");
    }

    private const int UserChangeGraceMs = 20_000; // Windows' own "Keep these display settings?" prompt reverts after 15 s
    private DateTime _ownChangeUntil = DateTime.MinValue;

    /// <summary>Grace period over: whatever is live now is what the user meant. Save it, or offer to.</summary>
    private void OnUserChangeSettled()
    {
        _userChange.Stop();
        if (_profile == null) return;
        var live = DisplayManager.CaptureForProfile();
        var diffs = _profile.DifferencesFrom(live);
        if (diffs.Count == 0)
        {
            Log.Write("Intentional change: settings are back to the saved layout (reverted or temporary); nothing to do");
            return;
        }
        string summary = string.Join("; ", diffs);
        if (_settings.UserChanges == "update")
        {
            _store.Upsert(live);
            RefreshActiveLayout();
            Log.Write($"Layout \"{_profile?.Name}\" updated from your change: {summary}");
            _tray.ShowBalloonTip(6000, "Layout updated", summary + Environment.NewLine + "Saved as the layout for these monitors.", ToolTipIcon.Info);
        }
        else
        {
            Log.Write($"Intentional change kept, not saved: {summary}. Click the notification or choose Persist to save it.");
            _pendingUserChange = live;
            _tray.ShowBalloonTip(10000, "Keep these display settings?", summary + Environment.NewLine + "Click to save them as the layout. Otherwise they stay until the next monitor change.", ToolTipIcon.Info);
        }
    }

    private DisplayProfile? _pendingUserChange;

    private void OnBalloonClicked()
    {
        if (_pendingUserChange == null) return;
        _store.Upsert(_pendingUserChange);
        _pendingUserChange = null;
        RefreshActiveLayout();
        Log.Write($"Layout \"{_profile?.Name}\" updated from the notification");
        _tray.ShowBalloonTip(3000, "Layout updated", "Your display settings are now the saved layout.", ToolTipIcon.Info);
    }

    private void SetUserChanges(string mode)
    {
        _settings.UserChanges = mode;
        _settings.Save();
        Log.Write($"UserChanges = {mode}");
        RefreshMenu();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleApply("resume from sleep");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Log.Write($"Session: {e.Reason}");
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteDisconnect)
            ScheduleApply(e.Reason.ToString());
    }

    private void ScheduleApply(string reason, int delayMs = DebounceMs)
    {
        if (!_settings.Enforce || _store.Layouts.Count == 0) return;
        Log.Write($"Display change detected ({reason}); checking in {delayMs} ms");
        _debounce.Stop();
        _debounce.Interval = delayMs;
        _debounce.Start();
    }

    private void SetMouseMode(bool nudge, bool hover)
    {
        _settings.JiggleWhenIdle = nudge;
        _settings.WiggleAllWindows = hover;
        _settings.Save();
        _jiggler.JiggleMouse = nudge;
        _jiggler.WiggleAllWindows = hover;
        Log.Write($"When idle, mouse: {(hover ? "hover over every window" : nudge ? "nudge in place" : "leave alone")} (after {_settings.JiggleIdleSeconds} s idle)");
        RefreshMenu();
    }

    private void SetIdleSeconds(int seconds)
    {
        _settings.JiggleIdleSeconds = seconds;
        _settings.Save();
        _jiggler.IdleThreshold = TimeSpan.FromSeconds(seconds);
        Log.Write($"JiggleIdleSeconds = {seconds}");
        RefreshMenu();
    }

    private void ToggleRdp()
    {
        _settings.KeepRdpAlive = !_settings.KeepRdpAlive;
        _settings.Save();
        _jiggler.KeepRdpAlive = _settings.KeepRdpAlive;
        var sessions = RemoteDesktop.FindSessions();
        Log.Write($"KeepRdpAlive = {_settings.KeepRdpAlive} (after {_settings.JiggleIdleSeconds} s idle; {sessions.Count} Remote Desktop window(s) open now)");
        RefreshMenu();
    }

    private void PauseUpdates(int days)
    {
        string what = days == 0 ? "resume Windows Update" : $"pause Windows Update for {days} days";
        if (MessageBox.Show($"Monitor Anchor will {what}. Windows will ask for administrator approval.\n\nContinue?",
                "Windows Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        int rc = WindowsUpdate.RequestPause(days);
        var until = WindowsUpdate.PausedUntil();
        string msg = rc == -1 ? "Administrator approval was declined; nothing changed."
                   : rc != 0 ? $"The elevated helper failed (code {rc}); see the log."
                   : until == null ? "Windows Update resumed."
                   : $"Windows Update paused until {until:g}.";
        Log.Write($"Windows Update request ({what}): {msg}");
        _tray.ShowBalloonTip(5000, "Windows Update", msg, rc == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
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

    // ---- Install ------------------------------------------------------------------------------------

    /// <summary>First run from Downloads, the desktop or a temp folder: offer a permanent home once.</summary>
    private void OfferInstall()
    {
        if (ScreenshotMode || Packaged.IsPackaged || _settings.InstallOfferAnswered || !Installer.LooksTemporary) return;
        _settings.InstallOfferAnswered = true;
        _settings.Save();
        var answer = MessageBox.Show(
            $"Monitor Anchor is running from\n{Environment.ProcessPath}\n\nInstall it to your Programs folder so it has a permanent home, a Start Menu entry and starts with Windows?\n\n" +
            $"({Installer.InstallDir})",
            "Install Monitor Anchor?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) DoInstall();
    }

    private void InstallOrUninstall()
    {
        if (!Installer.IsInstalled)
        {
            if (MessageBox.Show($"Copy Monitor Anchor to {Installer.InstallDir}, add a Start Menu shortcut, register it to start with Windows, and restart from there?",
                    "Install Monitor Anchor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                DoInstall();
            return;
        }
        var choice = MessageBox.Show(
            "Uninstall Monitor Anchor?\n\nYes: remove the program, shortcut and startup entry but keep your saved layouts and settings.\nNo: remove everything including saved layouts.\nCancel: keep it.",
            "Uninstall Monitor Anchor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        try
        {
            Installer.Uninstall(removeData: choice == DialogResult.No);
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write("Uninstall failed: " + ex);
            _tray.ShowBalloonTip(5000, "Monitor Anchor", "Uninstall failed: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void DoInstall()
    {
        try
        {
            string exe = Installer.Install();
            Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write("Install failed: " + ex);
            _tray.ShowBalloonTip(5000, "Monitor Anchor", "Install failed: " + ex.Message, ToolTipIcon.Error);
        }
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
                _pendingUpdate = release;
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
        if (!_settings.Enforce || _store.Layouts.Count == 0) return;
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
        _userChange.Dispose();
        _memoryTimer.Dispose();
        _updateTimer.Dispose();
        _jiggler.Dispose();
        _presence?.Dispose();
        InputTracker.Stop();
        _devices.Dispose();
        _kvm.Dispose();
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
        var fake = _menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "When idle");
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

        using var form = new DiagnosticsForm(() => _store, _kvm.Verdict);
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

        using (var about = new AboutForm())
        {
            about.StartPosition = FormStartPosition.Manual;
            about.Location = new Point(50, 50);
            about.TopMost = true;
            about.Show();
            about.Activate();
            Application.DoEvents();
            System.Threading.Thread.Sleep(500);
            Application.DoEvents();
            using var bmp = new Bitmap(about.Width, about.Height - 10);
            CaptureScreen(bmp, about.Location);
            bmp.Save(Path.Combine(dir, "about.png"), System.Drawing.Imaging.ImageFormat.Png);
            about.Close();
        }

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
