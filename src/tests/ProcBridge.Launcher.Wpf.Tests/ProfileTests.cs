using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Protocol.Enums;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Profiles: saved and read back, edited, and turned into the Launcher options.</summary>
[TestClass]
public sealed class ProfileTests
{
    private static readonly PairedMachine MachineB = FakeRegistry.Machine("B", "FING-ERPR-INTB", "10.0.0.2:5000");

    private static LaunchProfile Sample() => new()
    {
        Name = "build",
        MachineFingerprint = MachineB.Fingerprint,
        ShareName = "src",
        ShareRoot = @"D:\src",
        Mappings = [new FolderMapping { VirtualRoot = @"V:\Proj\", RemotePath = @"D:\src\proj" }, new FolderMapping { VirtualRoot = @"V:\All", RemotePath = @"d:\SRC\" }],
        Program = @"C:\Program Files\dotnet\dotnet.exe",
        Arguments = "build \"My App.sln\"",
        WorkingDirectory = @"C:\Work",
        DisableBuildServers = true,
        WriteThrough = true,
        OverwriteOnConflict = true,
        Architecture = "x86",
    };

    [TestMethod]
    public void JsonStore_RoundTripsEveryField()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pb-wpf-{Guid.NewGuid():N}", "profiles.json");
        try
        {
            var store = new JsonProfileStore(path);
            Assert.IsEmpty(store.Load());

            store.Save([Sample(), new LaunchProfile { Name = "empty" }]);
            IReadOnlyList<LaunchProfile> loaded = new JsonProfileStore(path).Load();

            Assert.HasCount(2, loaded);
            LaunchProfile first = loaded[0];
            Assert.AreEqual(Sample() with { Mappings = first.Mappings }, first);
            CollectionAssert.AreEqual(Sample().Mappings.ToArray(), first.Mappings.ToArray());
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void ProfilesViewModel_SavesEditsAndReloadsThem()
    {
        var store = new MemoryProfileStore();
        store.Saved.Add(Sample());
        var registry = new FakeRegistry();
        registry.Machines.Add(MachineB);
        var dialogs = new FakeDialogs();
        var machines = new MachinesViewModel(registry, dialogs);
        var run = new RunViewModel(new FakeRunner(), new LaunchOptionsFactory(@"C:\pb"));
        var profiles = new ProfilesViewModel(store, dialogs, machines, run);

        Assert.AreEqual("build", profiles.SelectedProfile!.Name);
        Assert.AreSame(MachineB, profiles.SelectedProfile.Machine);
        profiles.SelectedProfile.Program = "cmd.exe";
        profiles.AddCommand.Execute(null);
        Assert.IsTrue(profiles.Save());

        var reloaded = new ProfilesViewModel(store, dialogs, machines, run);
        CollectionAssert.AreEqual(new[] { "build", "Hồ sơ 2" }, reloaded.Profiles.Select(x => x.Name).ToArray());
        Assert.AreEqual("cmd.exe", reloaded.Profiles[0].Program);
        // A new profile starts on a paired machine.
        Assert.AreEqual(MachineB.Fingerprint, reloaded.Profiles[1].MachineFingerprint);
    }

    [TestMethod]
    public void ProfileViewModel_PickRemote_UsesTheProfileMachine_AndSeedsTheShareRoot()
    {
        var dialogs = new FakeDialogs { RemoteFolder = @"D:\src\proj" };
        var profile = new ProfileViewModel(new LaunchProfile { MachineFingerprint = MachineB.Fingerprint }, dialogs, fp => fp == MachineB.Fingerprint ? MachineB : null);
        profile.AddMappingCommand.Execute(null);

        profile.PickRemotePathCommand.Execute(profile.Mappings[0]);

        Assert.AreSame(MachineB, dialogs.RemoteFolderMachine);
        Assert.AreEqual(@"D:\src\proj", profile.Mappings[0].RemotePath);
        Assert.AreEqual(@"D:\src\proj", profile.ShareRoot);
    }

    [TestMethod]
    public void ProfileViewModel_PickRemote_WithoutMachine_ExplainsInsteadOfOpening()
    {
        var dialogs = new FakeDialogs { RemoteFolder = @"D:\x" };
        var profile = new ProfileViewModel(new LaunchProfile(), dialogs, _ => null);
        profile.AddMappingCommand.Execute(null);

        profile.PickRemotePathCommand.Execute(profile.Mappings[0]);

        Assert.IsNull(dialogs.RemoteFolderMachine);
        Assert.HasCount(1, dialogs.Errors);
        Assert.AreEqual(string.Empty, profile.Mappings[0].RemotePath);
    }

    [TestMethod]
    public void OptionsFactory_BuildsWhatTheCommandLineWould()
    {
        LauncherOptions options = new LaunchOptionsFactory(@"C:\pb").Create(Sample(), MachineB);

        Assert.AreEqual("10.0.0.2", options.AgentHost);
        Assert.AreEqual(5000, options.AgentPort);
        Assert.AreEqual(MachineB.Fingerprint, options.AgentFingerprint);
        Assert.AreEqual("src", options.ShareName);
        Assert.AreEqual(@"V:\Proj", options.Mappings[0].VirtualRoot);
        Assert.AreEqual("proj", options.Mappings[0].RemotePath);
        Assert.AreEqual(string.Empty, options.Mappings[1].RemotePath);
        Assert.AreEqual(@"C:\pb\ProcBridge.Spawn.x86.exe", options.SpawnPath);
        Assert.AreEqual("\"C:\\Program Files\\dotnet\\dotnet.exe\" build \"My App.sln\"", options.CommandLine);
        Assert.AreEqual(@"C:\Work", options.WorkingDirectory);
        Assert.IsTrue(options.DisableBuildServers);
        Assert.IsTrue(options.WriteThrough);
        Assert.AreEqual(ConflictPolicy.Overwrite, options.ConflictPolicy);
        Assert.IsNull(options.Security);
    }

    [TestMethod]
    public void OptionsFactory_DefaultsToWriteBackOnCloseAndKeepingBothVersions()
    {
        LauncherOptions options = new LaunchOptionsFactory(@"C:\pb").Create(Sample() with { WriteThrough = false, OverwriteOnConflict = false }, MachineB);

        Assert.IsFalse(options.WriteThrough);
        Assert.AreEqual(ConflictPolicy.KeepBoth, options.ConflictPolicy);
    }

    [TestMethod]
    public void OptionsFactory_RefusesFoldersOutsideTheShareRoot_AndMissingFields()
    {
        LaunchProfile profile = Sample() with { Mappings = [new FolderMapping { VirtualRoot = @"V:\a", RemotePath = @"D:\srcOther\x" }], Program = " " };

        var e = Assert.ThrowsExactly<ProfileValidationException>(() => new LaunchOptionsFactory(@"C:\pb").Create(profile, machine: null));

        Assert.HasCount(3, e.Errors);
        Assert.IsTrue(e.Errors.Any(x => x.Contains(@"D:\srcOther\x")));
    }

    [TestMethod]
    [DataRow(@"D:\src", @"D:\src\a\b", "a/b")]
    [DataRow(@"D:\src\", @"D:\SRC", "")]
    [DataRow(@"D:\", @"D:\src", "src")]
    [DataRow(@"D:\src", @"D:\src2", null)]
    [DataRow(@"D:\src", @"E:\src", null)]
    public void ShareRelativePath(string root, string path, string? expected)
    {
        Assert.AreEqual(expected, RemotePathHelper.ToShareRelative(root, path));
    }
}
