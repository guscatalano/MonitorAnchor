using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitorAnchor;

/// <summary>
/// Client for the Parsec Virtual Display Driver, a signed Windows indirect-display driver that can
/// create up to eight virtual monitors on demand. Protocol from https://github.com/nomi-san/parsec-vdd.
/// The driver must be installed separately; without it <see cref="IsDriverPresent"/> is false.
/// </summary>
public sealed class ParsecVdd : IDisposable
{
    public const string DriverProjectUrl = "https://github.com/nomi-san/parsec-vdd#install-driver";
    public const int MaxDisplays = 8;

    /// <summary>Monitor hardware id the driver reports; appears in the device path as DISPLAY#PSCCDD0#...</summary>
    private const string DisplayId = "PSCCDD0";
    private static readonly Guid AdapterInterfaceGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");

    private const uint IOCTL_ADD = 0x0022e004;
    private const uint IOCTL_REMOVE = 0x0022a008;
    private const uint IOCTL_UPDATE = 0x0022a00c;
    private const uint IOCTL_VERSION = 0x0022e010;

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();
    private readonly Thread _keepAlive;
    private volatile bool _stop;

    public static bool IsVirtualMonitorId(string monitorId) =>
        monitorId.Contains("#" + DisplayId + "#", StringComparison.OrdinalIgnoreCase);

    public static bool IsDriverPresent() => DevicePath() != null;

    private static string? DevicePath()
    {
        try
        {
            var guid = AdapterInterfaceGuid;
            if (Native.CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || len <= 1)
                return null;
            var buffer = new char[len];
            if (Native.CM_Get_Device_Interface_ListW(ref guid, null, buffer, len, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
                return null;
            string list = new(buffer);
            string first = list.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return first.Length > 0 ? first : null;
        }
        catch (Exception ex)
        {
            Log.Write("Parsec VDD lookup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Opens the driver and starts the keep-alive ping. Returns null when the driver is not installed.</summary>
    public static ParsecVdd? Open()
    {
        string? path = DevicePath();
        if (path == null) return null;

        var handle = Native.CreateFileW(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_OVERLAPPED | Native.FILE_FLAG_WRITE_THROUGH,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Log.Write($"Parsec VDD open failed (error {Marshal.GetLastWin32Error()})");
            handle.Dispose();
            return null;
        }
        return new ParsecVdd(handle);
    }

    private ParsecVdd(SafeFileHandle handle)
    {
        _handle = handle;
        Log.Write($"Parsec VDD opened, driver minor version {Version()}");
        // The driver drops every virtual display unless it is pinged more often than every 100 ms.
        _keepAlive = new Thread(KeepAliveLoop) { IsBackground = true, Name = "ParsecVDD keep-alive" };
        _keepAlive.Start();
    }

    private void KeepAliveLoop()
    {
        while (!_stop)
        {
            try { Update(); } catch { /* keep pinging */ }
            Thread.Sleep(40);
        }
    }

    public int Version() => (int)IoControl(IOCTL_VERSION, null);

    public void Update() => IoControl(IOCTL_UPDATE, null);

    /// <summary>Plugs in a new virtual display and returns its index (0-based), or -1 on failure.</summary>
    public int AddDisplay()
    {
        uint idx = IoControl(IOCTL_ADD, null);
        Update();
        return idx == uint.MaxValue ? -1 : (int)idx;
    }

    /// <summary>Unplugs the virtual display with the given index.</summary>
    public void RemoveDisplay(int index)
    {
        // The driver reads the index as a 16-bit big-endian value.
        var data = new byte[] { (byte)(index & 0xFF), (byte)((index >> 8) & 0xFF) };
        IoControl(IOCTL_REMOVE, data);
        Update();
    }

    /// <summary>Overlapped DeviceIoControl with a 32-byte input buffer and a DWORD output, waited synchronously (5 s cap).</summary>
    private uint IoControl(uint code, byte[]? data)
    {
        lock (_gate)
        {
            if (_handle.IsClosed) return uint.MaxValue;

            IntPtr inBuf = Marshal.AllocHGlobal(32);
            IntPtr outBuf = Marshal.AllocHGlobal(4);
            IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            IntPtr evt = IntPtr.Zero;
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(inBuf, i, 0);
                if (data != null) Marshal.Copy(data, 0, inBuf, Math.Min(data.Length, 32));
                Marshal.WriteInt32(outBuf, 0);
                for (int i = 0; i < Marshal.SizeOf<NativeOverlapped>(); i++) Marshal.WriteByte(overlapped, i, 0);

                evt = Native.CreateEventW(IntPtr.Zero, true, false, null);
                Marshal.WriteIntPtr(overlapped, Marshal.OffsetOf<NativeOverlapped>(nameof(NativeOverlapped.EventHandle)).ToInt32(), evt);

                Native.DeviceIoControl(_handle, code, inBuf, 32, outBuf, 4, IntPtr.Zero, overlapped);
                if (!Native.GetOverlappedResultEx(_handle, overlapped, out _, 5000, false))
                    return uint.MaxValue;
                return (uint)Marshal.ReadInt32(outBuf);
            }
            finally
            {
                if (evt != IntPtr.Zero) Native.CloseHandle(evt);
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
                Marshal.FreeHGlobal(overlapped);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_gate)
        {
            _handle.Dispose(); // closing the handle lets the driver retire any remaining displays
        }
        Log.Write("Parsec VDD closed");
    }
}
