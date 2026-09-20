using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAnchor;

/// <summary>Whether this process runs from an MSIX package, where a few things work differently.</summary>
public static class Packaged
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private static bool? _cached;

    /// <summary>
    /// True inside an MSIX package. There, the registry Run key is virtualised (startup is a manifest StartupTask
    /// managed in Settings > Apps > Startup), the install folder is read-only (updates go through App Installer),
    /// and the self-install option makes no sense.
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            if (_cached is { } c) return c;
            try
            {
                int len = 0;
                int rc = GetCurrentPackageFullName(ref len, null);
                _cached = rc != APPMODEL_ERROR_NO_PACKAGE;
            }
            catch
            {
                _cached = false; // very old Windows without the API
            }
            return _cached.Value;
        }
    }
}
