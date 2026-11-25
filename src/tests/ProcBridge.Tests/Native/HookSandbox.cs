using System.Diagnostics;
using System.Text;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// A real folder with a fixed tree plus a virtual folder path (which does not exist) mapped onto it.
/// Both sit side by side under the test output folder, so they share the volume. The sandbox owns the
/// folder and deletes it on dispose.
/// </summary>
internal sealed class HookSandbox : IDisposable
{
    public const string FileAContent = "hello from the real folder\r\n";
    // Vietnamese + Latin-1 name built from code points so the source stays ASCII; must match NativeProbe.
    public static readonly string UnicodeFileName = "t" + (char)0x1EC7 + "p-" + (char)0x00FC + ".txt";

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

    private readonly string _baseDirectory;
    private readonly NativeBinaries _binaries;

    public HookSandbox(string platform)
    {
        _binaries = new NativeBinaries(platform);
        _baseDirectory = Path.Combine(AppContext.BaseDirectory, "hook-sandbox", Guid.NewGuid().ToString("N"));
        RealRoot = Path.Combine(_baseDirectory, "real");
        VirtualRoot = Path.Combine(_baseDirectory, "virtual-root");
        CreateTree();
    }

    /// <summary>Folder holding <see cref="RealRoot"/>; the virtual root path also sits directly inside it.</summary>
    public string BaseDirectory => _baseDirectory;

    public string RealRoot { get; }

    /// <summary>Virtual folder; longer than <see cref="RealRoot"/> on purpose so name rewriting changes lengths.</summary>
    public string VirtualRoot { get; }

    public NativeBinaries Binaries => _binaries;

    public ProcessResult Run(string fileName, string arguments) => RunProcess(fileName, arguments, null);

    /// <summary>Runs a command line under ProcBridge.Spawn with extra environment variables for the tree.</summary>
    public ProcessResult RunHooked(string commandLine, IReadOnlyDictionary<string, string> environment)
        => RunProcess(_binaries.Spawn, $"--map \"{VirtualRoot}\" \"{RealRoot}\" -- {commandLine}", null, environment);

    /// <summary>Runs a command line under ProcBridge.Spawn with the virtual root mapped onto the real root.</summary>
    public ProcessResult RunHooked(string commandLine, string? workingDirectory = null)
        => RunSpawn($"--map \"{VirtualRoot}\" \"{RealRoot}\" -- {commandLine}", workingDirectory);

    /// <summary>Runs ProcBridge.Spawn with raw arguments (own --map options and command line).</summary>
    public ProcessResult RunSpawn(string arguments, string? workingDirectory = null)
        => RunProcess(_binaries.Spawn, arguments, workingDirectory);

    /// <summary>
    /// Starts a command line under ProcBridge.Spawn without waiting; output is not redirected. The caller
    /// owns the returned process.
    /// </summary>
    public Process StartHooked(string commandLine)
    {
        ProcessStartInfo startInfo = new(_binaries.Spawn, $"--map \"{VirtualRoot}\" \"{RealRoot}\" -- {commandLine}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start ProcBridge.Spawn.");
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_baseDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover sandbox under bin/ does not affect other tests.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void CreateTree()
    {
        Directory.CreateDirectory(Path.Combine(RealRoot, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(RealRoot, "empty"));
        File.WriteAllText(Path.Combine(RealRoot, "a.txt"), FileAContent);
        File.WriteAllText(Path.Combine(RealRoot, "sub", "b.txt"), "bee" + Environment.NewLine);
        File.WriteAllText(Path.Combine(RealRoot, UnicodeFileName), "unicode");
        string readOnly = Path.Combine(RealRoot, "ro.txt");
        File.WriteAllText(readOnly, "read only");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        byte[] binary = new byte[300_000];
        for (int i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(i * 31 % 251);
        }
        File.WriteAllBytes(Path.Combine(RealRoot, "sub", "deep", "c.bin"), binary);
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = new(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Cannot start {fileName}.");
        // Read both pipes concurrently, otherwise a full pipe blocks the child.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(_timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {arguments} did not exit within {_timeout}.");
        }
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.Result, error.Result);
    }
}
