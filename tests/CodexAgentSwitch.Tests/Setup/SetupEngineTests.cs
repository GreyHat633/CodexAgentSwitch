using System.IO.Compression;
using System.Security.Cryptography;
using CodexAgentSwitch.Setup;

namespace CodexAgentSwitch.Tests.Setup;

public sealed class SetupEngineTests
{
    [Fact]
    public async Task Install_backs_up_existing_version_preserves_data_and_uninstalls_recoverably()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-setup-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "payload");
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(target, "data"));
        await File.WriteAllTextAsync(Path.Combine(source, "CodexAgentSwitch.App.exe"), "new-app");
        await File.WriteAllTextAsync(Path.Combine(source, "AppIcon.ico"), "icon-v2");
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old-app");
        await File.WriteAllTextAsync(Path.Combine(target, "data", "user.db"), "user-data");
        var zip = Path.Combine(root, "CodexAgentSwitch-win10-x64.zip");
        ZipFile.CreateFromDirectory(source, zip);
        await WriteChecksumAsync(zip);
        var shortcut = new FakeShortcut();
        try
        {
            var result = await new SetupEngine(shortcut).InstallAsync(zip, target);

            Assert.NotNull(result.BackupDirectory);
            Assert.True(Directory.Exists(result.BackupDirectory));
            Assert.Equal("old-app", await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory!, "old.txt")));
            Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(target, "CodexAgentSwitch.App.exe")));
            Assert.Equal("icon-v2", await File.ReadAllTextAsync(Path.Combine(target, "AppIcon.ico")));
            Assert.Equal("user-data", await File.ReadAllTextAsync(Path.Combine(target, "data", "user.db")));
            Assert.True(File.Exists(Path.Combine(target, "install.json")));
            Assert.Equal(target, shortcut.CreatedFor);

            var uninstall = new SetupEngine(shortcut).Uninstall(target);
            Assert.False(Directory.Exists(target));
            Assert.True(Directory.Exists(uninstall.BackupDirectory));
            Assert.True(shortcut.Removed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_payload_hash_stops_before_target_is_changed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-setup-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "payload.zip");
        await File.WriteAllBytesAsync(zip, [1, 2, 3]);
        await File.WriteAllTextAsync(zip + ".sha256", new string('0', 64));
        var target = Path.Combine(root, "target");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new SetupEngine().InstallAsync(zip, target));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Drive_root_is_never_an_install_target()
    {
        Assert.Throws<ArgumentException>(() => new SetupEngine().Uninstall(Path.GetPathRoot(Environment.CurrentDirectory)!));
    }

    [Fact]
    public void Explicit_delete_data_choice_removes_data_but_keeps_recoverable_program_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-setup-delete-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, "app.txt"), "program");
        File.WriteAllText(Path.Combine(target, "data", "profile.db"), "private");
        try
        {
            var result = new SetupEngine().Uninstall(target, deleteUserData: true);
            Assert.NotNull(result.BackupDirectory);
            Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "app.txt")));
            Assert.False(Directory.Exists(Path.Combine(result.BackupDirectory!, "data")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteChecksumAsync(string zip)
    {
        await using var stream = File.OpenRead(zip);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        await File.WriteAllTextAsync(zip + ".sha256", hash + "  " + Path.GetFileName(zip));
    }

    private sealed class FakeShortcut : IStartMenuShortcut
    {
        public string? CreatedFor { get; private set; }
        public bool Removed { get; private set; }
        public string Create(string targetDirectory) { CreatedFor = targetDirectory; return Path.Combine(targetDirectory, "shortcut.lnk"); }
        public void Remove() => Removed = true;
    }
}
