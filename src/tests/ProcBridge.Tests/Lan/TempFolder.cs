using System.Diagnostics;

namespace ProcBridge.Tests.Lan;

/// <summary>Temporary folder deleted on dispose; owns everything inside it.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pb-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public string WriteFile(string relative, byte[] content)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Creates a directory junction (no admin right needed, unlike symbolic links).</summary>
    public void CreateJunction(string relative, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Combine(relative)}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(info)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("mklink failed: " + process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try
        {
            // Junctions are deleted as links (not followed) by Directory.Delete on .NET.
            foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
