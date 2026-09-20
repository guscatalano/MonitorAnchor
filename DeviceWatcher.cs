using System.Runtime.InteropServices;

namespace MonitorAnchor;

/// <summary>Raises an event for every device interface that arrives or is removed (USB keyboards, mice, hubs, ...).</summary>
public sealed class DeviceWatcher : NativeWindow, IDisposable
{
    public event Action<bool /*arrived*/, string /*device path*/>? DeviceChanged;

    /// <summary>Windows turned the displays off (0), on (1) or dimmed them (2).</summary>
    public event Action<int>? DisplayPowerChanged;

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    private IntPtr _powerRegistration;

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const uint DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 4;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size; public int dbcc_devicetype; public int dbcc_reserved; public Guid dbcc_classguid; public short dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private IntPtr _registration;

    public DeviceWatcher()
    {
        // A message-only window is enough for registered notifications (they are sent, not broadcast).
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "MonitorAnchor.DeviceWatcher" });

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
        };
        IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            _registration = RegisterDeviceNotificationW(Handle, buffer, DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
            if (_registration == IntPtr.Zero) Log.Write($"RegisterDeviceNotification failed (error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _powerRegistration = RegisterPowerSettingNotification(Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_powerRegistration == IntPtr.Zero) Log.Write($"RegisterPowerSettingNotification failed (error {Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID PowerSetting (16), DWORD DataLength (4), UCHAR Data[]
            var bytes = new byte[16];
            Marshal.Copy(m.LParam, bytes, 0, 16);
            if (new Guid(bytes) == GUID_CONSOLE_DISPLAY_STATE && Marshal.ReadInt32(m.LParam, 16) >= 4)
            {
                int state = Marshal.ReadInt32(m.LParam, 20);
                try { DisplayPowerChanged?.Invoke(state); } catch { /* listener error must not break the pump */ }
            }
        }
        if (m.Msg == WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
        {
            int evt = m.WParam.ToInt32();
            if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
            {
                int type = Marshal.ReadInt32(m.LParam, 4);
                if (type == DBT_DEVTYP_DEVICEINTERFACE)
                {
                    // dbcc_name starts after size(4) + devicetype(4) + reserved(4) + classguid(16)
                    string path = Marshal.PtrToStringUni(m.LParam + 28) ?? string.Empty;
                    try { DeviceChanged?.Invoke(evt == DBT_DEVICEARRIVAL, path); } catch { /* listener error must not break the pump */ }
                }
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) { UnregisterDeviceNotification(_registration); _registration = IntPtr.Zero; }
        if (_powerRegistration != IntPtr.Zero) { UnregisterPowerSettingNotification(_powerRegistration); _powerRegistration = IntPtr.Zero; }
        DestroyHandle();
    }
}
