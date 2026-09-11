using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Tests.Storage;

public abstract class StorageTestBase : IDisposable
{
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), $"mt5-storage-{Guid.NewGuid():N}");
    protected TerminalRegistration Registration => new(Guid.NewGuid(), "Test", "terminal64.exe", Root, Root, [], DiscoverySource.Manual, true);

    protected StorageTestBase() => Directory.CreateDirectory(Root);

    protected string CreateDirectory(params string[] segments)
    {
        var path = segments.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    protected string WriteFile(byte[] content, params string[] segments)
    {
        var path = segments.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    protected static string CreateOutsideDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt5-storage-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    protected static string WriteOutsideFile(string root, byte[] content, params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    protected static string CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not create test junction.");
        return link;
    }

    protected static string CurrentUserIdentity() =>
        System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    protected static void SetDirectoryDeny(string path, string identity, bool deny)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls.exe",
            ArgumentList = { path, deny ? "/deny" : "/remove:d", deny ? $"*{identity}:(OI)(CI)F" : $"*{identity}" },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start icacls.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not update test directory ACL.");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
