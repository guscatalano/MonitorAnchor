using System.Diagnostics;

namespace MonitorAnchor;

/// <summary>Read-only window showing the diagnostic report, with refresh / copy / open-folder buttons.</summary>
public sealed class DiagnosticsForm : Form
{
    private readonly TextBox _text;
    private readonly Func<DisplayProfile?> _profile;

    public DiagnosticsForm(Func<DisplayProfile?> profile)
    {
        _profile = profile;
        Text = "Monitor Anchor diagnostics";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 700);
        MinimumSize = new Size(600, 400);
        ShowInTaskbar = true;
        Icon = TrayApp.AppIcon();

        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var copy = new Button { Text = "Copy to clipboard", AutoSize = true };
        var folder = new Button { Text = "Open data folder", AutoSize = true };
        var log = new Button { Text = "Open log", AutoSize = true };
        refresh.Click += (_, _) => Refresh();
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { /* clipboard busy */ } };
        folder.Click += (_, _) => Open(DisplayProfile.ConfigDir);
        log.Click += (_, _) => Open(Log.Path);
        bar.Controls.AddRange(new Control[] { refresh, copy, folder, log });

        Controls.Add(_text);
        Controls.Add(bar);
        Load += (_, _) => Refresh();
    }

    public new void Refresh()
    {
        try
        {
            _text.Text = Diagnostics.WriteDump(_profile());
        }
        catch (Exception ex)
        {
            _text.Text = "Failed to build diagnostics: " + ex;
        }
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* ignore */ }
    }
}
