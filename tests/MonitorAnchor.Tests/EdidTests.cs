using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class EdidTests
{
    /// <summary>Builds a minimal 128-byte EDID with the given manufacturer, product, serial, name and serial text.</summary>
    private static byte[] Build(string mfg, ushort product, uint serial, string name, string serialText, byte week = 16, byte yearOffset = 34)
    {
        var e = new byte[128];
        // Manufacturer: three 5-bit letters packed big-endian.
        int m = ((mfg[0] - 'A' + 1) << 10) | ((mfg[1] - 'A' + 1) << 5) | (mfg[2] - 'A' + 1);
        e[8] = (byte)(m >> 8); e[9] = (byte)m;
        e[10] = (byte)product; e[11] = (byte)(product >> 8);
        e[12] = (byte)serial; e[13] = (byte)(serial >> 8); e[14] = (byte)(serial >> 16); e[15] = (byte)(serial >> 24);
        e[16] = week; e[17] = yearOffset;
        WriteDescriptor(e, 54, 0xFC, name);
        WriteDescriptor(e, 72, 0xFF, serialText);
        return e;
    }

    private static void WriteDescriptor(byte[] e, int off, byte tag, string text)
    {
        e[off + 3] = tag;
        var bytes = System.Text.Encoding.ASCII.GetBytes((text + "\n").PadRight(13));
        Array.Copy(bytes, 0, e, off + 5, 13);
    }

    [Fact]
    public void Parses_manufacturer_product_serial_and_descriptors()
    {
        var info = Edid.Parse(Build("AUS", 0x25A8, 16843009, "VG259QM", "S4LMQS077249"));
        Assert.Equal("AUS", info.Manufacturer);
        Assert.Equal(0x25A8, info.ProductCode);
        Assert.Equal(16843009u, info.Serial);
        Assert.Equal("VG259QM", info.Name);
        Assert.Equal("S4LMQS077249", info.SerialText);
        Assert.Equal(16, info.Week);
        Assert.Equal(2024, info.Year);
        Assert.Contains("AUS 25A8 \"VG259QM\"", Edid.Describe(info));
    }

    [Fact]
    public void Missing_descriptors_leave_empty_strings()
    {
        var e = Build("BOE", 0x0CB4, 0, "NE135A1M-NY1", "");
        Array.Clear(e, 72, 18); // no serial descriptor at all
        var info = Edid.Parse(e);
        Assert.Equal("NE135A1M-NY1", info.Name);
        Assert.Equal("", info.SerialText);
        Assert.DoesNotContain(" / \"", Edid.Describe(info)); // no serial-text clause
    }
}
