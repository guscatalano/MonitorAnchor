using MonitorAnchor;
using Xunit;

// The data-folder override is process-wide, so test classes must not run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MonitorAnchor.Tests;

/// <summary>
/// Points the app's data folder at a fresh temp directory for the lifetime of a test. Between tests the override
/// falls back to a shared temp root, never to null, so nothing in the test process can ever touch the real
/// data folder under %LOCALAPPDATA%.
/// </summary>
public sealed class TempDataFolder : IDisposable
{
    private static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MonitorAnchorTests");

    /// <summary>Runs once when the test assembly loads.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Guard()
    {
        Directory.CreateDirectory(Root);
        DisplayProfile.ConfigDirOverride = Root;
    }

    public string Path { get; }

    public TempDataFolder()
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        DisplayProfile.ConfigDirOverride = Path;
    }

    public void Dispose()
    {
        DisplayProfile.ConfigDirOverride = Root;
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
