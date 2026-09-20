using System.Diagnostics;
using System.Text;

namespace MonitorAnchor;

/// <summary>Live view of log.txt: tails the file twice a second and keeps the newest lines in view.</summary>
public sealed class LogForm : Form
{
    private readonly TextBox _text;
    private readonly CheckBox _follow;
    private readonly System.Windows.Forms.Timer _poll;
    private long _readOffset;
    private readonly string _path;
    private const int MaxChars = 400_000; // keep the view responsive; the file itself is trimmed separately

    /// <param name="path">File to tail; defaults to the app log. Used with a sample file when rendering README screenshots.</param>
    public LogForm(string? path = null)
    {
        _path = path ?? Log.Path;
        Text = "Monitor Anchor log";
        Icon = TrayApp.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 650);
        MinimumSize = new Size(600, 300);

        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            HideSelection = false,
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        _follow = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(6, 8, 12, 0) };
        var reload = new Button { Text = "Reload", AutoSize = true };
        var copy = new Button { Text = "Copy all", AutoSize = true };
        var editor = new Button { Text = "Open in editor", AutoSize = true };
        var clear = new Button { Text = "Clear log file", AutoSize = true };
        reload.Click += (_, _) => Reload();
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { /* clipboard busy */ } };
        editor.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); } catch { /* ignore */ } };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete everything in the log file?", "Monitor Anchor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.WriteAllText(_path, string.Empty); } catch { /* ignore */ }
            Log.Write("Log cleared from the viewer");
            Reload();
        };
        bar.Controls.AddRange(new Control[] { _follow, reload, copy, editor, clear });

        Controls.Add(_text);
        Controls.Add(bar);

        _poll = new System.Windows.Forms.Timer { Interval = 500 };
        _poll.Tick += (_, _) => Poll();
        Load += (_, _) => { Reload(); _poll.Start(); };
        Shown += (_, _) => ScrollToEnd(); // the window is now visible; land on the newest entries
        Resize += (_, _) => { if (_follow.Checked) ScrollToEnd(); };
        FormClosed += (_, _) => _poll.Dispose();

        // Scrolling up by hand pauses following; jumping back to the bottom resumes it.
        _text.MouseWheel += (_, e) => { if (e.Delta > 0) _follow.Checked = false; };
        _text.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.End) _follow.Checked = true; };
    }

    private void Reload()
    {
        try
        {
            string all = File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
            _readOffset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (all.Length > MaxChars) all = "... (older lines not shown; use Open in editor for the full file)" + Environment.NewLine + all[^MaxChars..];
            _text.Text = all;
        }
        catch (Exception ex)
        {
            _text.Text = "Cannot read log: " + ex.Message;
        }
        ScrollToEnd();
    }

    private void Poll()
    {
        try
        {
            if (!File.Exists(_path)) return;
            long length = new FileInfo(_path).Length;
            if (length < _readOffset) { Reload(); return; } // trimmed or cleared: start over
            if (length == _readOffset) return;

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_readOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string added = reader.ReadToEnd();
            _readOffset = length;

            if (_text.TextLength + added.Length > MaxChars * 2) { Reload(); return; }
            _text.AppendText(added);
            if (_follow.Checked) ScrollToEnd();
        }
        catch
        {
            // file momentarily locked; try again on the next tick
        }
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
        // ScrollToCaret is unreliable before the control has painted; the edit control's own message is not.
        if (_text.IsHandleCreated) SendMessage(_text.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
    }

    private const int WM_VSCROLL = 0x0115;
    private const int SB_BOTTOM = 7;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
