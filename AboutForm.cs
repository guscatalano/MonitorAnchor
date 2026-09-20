using System.Diagnostics;

namespace MonitorAnchor;

/// <summary>Version, links to the project and the author's blog, and where the data lives.</summary>
public sealed class AboutForm : Form
{
    public const string BlogUrl = "https://guscatalano.dev/";
    public const string GitHubUrl = Updater.RepoUrl;

    public AboutForm()
    {
        Text = "About Monitor Anchor";
        Icon = TrayApp.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 250);

        var icon = new PictureBox
        {
            Image = TrayApp.AppIcon().ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(20, 20, 64, 64),
        };
        var title = new Label
        {
            Text = "Monitor Anchor",
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(100, 22),
        };
        title.Font = new Font(title.Font.FontFamily, 14f, FontStyle.Bold);
        var version = new Label
        {
            Text = $"Version {Updater.Current} ({Updater.AssetName})",
            AutoSize = true,
            Location = new Point(102, 52),
        };
        var blurb = new Label
        {
            Text = "Pins your monitor layout: resolution, refresh rate, position, orientation and HDR stay the way you set them, " +
                   "no matter what Windows does. Made by Gus Catalano.",
            Location = new Point(102, 76),
            Size = new Size(340, 50),
        };

        var github = Link("GitHub: " + GitHubUrl, GitHubUrl, 102, 130);
        var blog = Link("Blog: " + BlogUrl, BlogUrl, 102, 152);
        var releases = Link("Releases and changelog", GitHubUrl + "/releases", 102, 174);
        var folder = Link("Open data folder", DisplayProfile.ConfigDir, 102, 196);

        var ok = new Button { Text = "Close", DialogResult = DialogResult.OK, Size = new Size(80, 26), Location = new Point(360, 210) };
        AcceptButton = ok;
        CancelButton = ok;

        Controls.AddRange(new Control[] { icon, title, version, blurb, github, blog, releases, folder, ok });
    }

    private static LinkLabel Link(string text, string target, int x, int y)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Location = new Point(x, y) };
        l.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { /* ignore */ }
        };
        return l;
    }
}
