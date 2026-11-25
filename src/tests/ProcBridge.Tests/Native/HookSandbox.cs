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

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

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

    public string RealRoot { get; }

    /// <summary>Virtual folder; longer than <see cref="RealRoot"/> on purpose so name rewriting changes lengths.</summary>
    public string VirtualRoot { get; }

    public NativeBinaries Binaries => _binaries;

    public ProcessResult Run(string fileName, string arguments) => RunProcess(fileName, arguments);

    /// <summary>Runs a command line under ProcBridge.Spawn with the virtual root mapped onto the real root.</summary>
    public ProcessResult RunHooked(string commandLine)
        => RunProcess(_binaries.Spawn, $"--map \"{VirtualRoot}\" \"{RealRoot}\" -- {commandLine}");

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

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        ProcessStartInfo startInfo = new(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
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
