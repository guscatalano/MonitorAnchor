using System.Text;
using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>Reads and decodes the EDID Windows cached for a monitor, for the diagnostic dump.</summary>
public static class Edid
{
    public sealed record Info(string Manufacturer, int ProductCode, uint Serial, string Name, string SerialText, int Week, int Year);

    /// <summary>Looks up the EDID for a device path like \\?\DISPLAY#AUS25A8#9&amp;4d2881a&amp;0&amp;UID257#{guid}.</summary>
    public static Info? ForMonitor(string monitorId)
    {
        try
        {
            var parts = monitorId.Split('#');
            if (parts.Length < 4) return null;
            string key = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
            using var reg = Registry.LocalMachine.OpenSubKey(key);
            if (reg?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return null;
            return Parse(edid);
        }
        catch
        {
            return null;
        }
    }

    public static Info Parse(byte[] e)
    {
        int m = (e[8] << 8) | e[9];
        string manufacturer = new(new[] { (char)('A' - 1 + ((m >> 10) & 0x1F)), (char)('A' - 1 + ((m >> 5) & 0x1F)), (char)('A' - 1 + (m & 0x1F)) });
        int product = e[10] | (e[11] << 8);
        uint serial = (uint)(e[12] | (e[13] << 8) | (e[14] << 16) | (e[15] << 24));
        int week = e[16], year = 1990 + e[17];

        string name = "", serialText = "";
        for (int off = 54; off + 18 <= 126; off += 18)
        {
            if (e[off] != 0 || e[off + 1] != 0 || e[off + 2] != 0) continue; // detailed timing, not a text descriptor
            string text = Encoding.ASCII.GetString(e, off + 5, 13).Split('\n')[0].Trim();
            if (e[off + 3] == 0xFC) name = text;
            else if (e[off + 3] == 0xFF) serialText = text;
        }
        return new Info(manufacturer, product, serial, name, serialText, week, year);
    }

    public static string Describe(Info i) =>
        $"{i.Manufacturer} {i.ProductCode:X4} \"{i.Name}\" serial {i.Serial}{(i.SerialText.Length > 0 ? $" / \"{i.SerialText}\"" : "")} made wk{i.Week}/{i.Year}";
}
