using System.Diagnostics;

namespace MonitorAnchor;

/// <summary>Everything the app is currently unhappy about, in one place, with what to do about each.</summary>
public sealed class WarningsForm : Form
{
    private readonly TextBox _text;
    private readonly Func<List<string>> _collect;

    public WarningsForm(Func<List<string>> collect)
    {
        _collect = collect;
        Text = "Monitor Anchor warnings";
        Icon = TrayApp.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 480);
        MinimumSize = new Size(500, 300);

        _text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Font = new Font("Segoe UI", 10f), Dock = DockStyle.Fill, BackColor = SystemColors.Window,
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var copy = new Button { Text = "Copy", AutoSize = true };
        var diag = new Button { Text = "Open diagnostics", AutoSize = true };
        refresh.Click += (_, _) => Refresh();
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { /* busy */ } };
        diag.Click += (_, _) => OpenDiagnostics?.Invoke();
        bar.Controls.AddRange(new Control[] { refresh, copy, diag });
        Controls.Add(_text);
        Controls.Add(bar);
        Load += (_, _) => Refresh();
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OpenDiagnostics { get; set; }

    public new void Refresh()
    {
        List<string> items;
        try { items = _collect(); } catch (Exception ex) { items = new List<string> { "Could not collect warnings: " + ex.Message }; }
        _text.Text = items.Count == 0
            ? "No warnings. Every link is under the HDMI high-speed threshold, drivers are recent, a saved layout matches the connected monitors, and nothing has retrained since the app started." + Environment.NewLine + Environment.NewLine + $"Checked {DateTime.Now:T}."
            : string.Join(Environment.NewLine + Environment.NewLine, items.Select((w, i) => $"{i + 1}. {w}")) + Environment.NewLine + Environment.NewLine + $"Checked {DateTime.Now:T}.";
        _text.SelectionStart = 0; _text.SelectionLength = 0;
    }
}
