namespace ProcBridge.Tests.Native;

/// <summary>
/// Locates the native build output (src/native/out/&lt;Platform&gt;/&lt;Configuration&gt;) for one platform.
/// Release is preferred, Debug is the fallback; PROCBRIDGE_NATIVE_OUT overrides the search root.
/// The native solution is built separately (msbuild), so a missing output makes the test inconclusive
/// (or fails it when PROCBRIDGE_REQUIRE_NATIVE=1, as in CI).
/// </summary>
internal sealed class NativeBinaries
{
    private readonly string _directory;
    private readonly string _architecture;

    public NativeBinaries(string platform)
    {
        _architecture = platform == "Win32" ? "x86" : platform;
        string outRoot = Environment.GetEnvironmentVariable("PROCBRIDGE_NATIVE_OUT") ?? Path.Combine(FindSourceRoot(), "native", "out");
        _directory = new[] { "Release", "Debug" }
            .Select(configuration => Path.Combine(outRoot, platform, configuration))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, $"ProcBridge.Spawn.{_architecture}.exe")))
            ?? throw Missing($"Native binaries for {platform} not built under {outRoot}.");
    }

    public string Spawn => Path.Combine(_directory, $"ProcBridge.Spawn.{_architecture}.exe");

    public string Probe => Path.Combine(_directory, $"ProcBridge.NativeProbe.{_architecture}.exe");

    private static string FindSourceRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProcBridge.Native.sln")))
            {
                return directory.FullName;
            }
        }
        throw Missing("ProcBridge.Native.sln not found above the test output folder.");
    }

    /// <summary>Inconclusive locally; a hard failure when PROCBRIDGE_REQUIRE_NATIVE=1 (CI).</summary>
    private static Exception Missing(string message)
        => Environment.GetEnvironmentVariable("PROCBRIDGE_REQUIRE_NATIVE") == "1"
            ? new InvalidOperationException(message)
            : new AssertInconclusiveException(message);
}
