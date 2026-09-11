using System.Runtime.InteropServices;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Discovery;

public interface IShortcutResolver
{
    ShortcutTarget? Resolve(string shortcutPath);
}

public sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory);

public sealed class ShortcutDiscoverySource(
    IEnumerable<string>? shortcutRoots = null,
    IShortcutResolver? resolver = null) : ITerminalDiscoverySource
{
    private readonly IReadOnlyList<string> _roots = shortcutRoots?.ToArray() ?? DefaultRoots();
    private readonly IShortcutResolver _resolver = resolver ?? new ComShortcutResolver();

    public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TerminalRegistration>();
        foreach (var root in _roots.Where(Directory.Exists))
        foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShortcutTarget? target;
            try { target = _resolver.Resolve(shortcut); } catch (COMException) { continue; }
            if (target is null || !Path.GetFileName(target.TargetPath).Equals("terminal64.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var arguments = WindowsCommandLine.Split($"terminal64.exe {target.Arguments}");
            if (arguments.Count > 0) arguments.RemoveAt(0);
            result.Add(new TerminalRegistration(Guid.NewGuid(), Path.GetFileNameWithoutExtension(shortcut),
                target.TargetPath, string.Empty,
                string.IsNullOrWhiteSpace(target.WorkingDirectory) ? Path.GetDirectoryName(target.TargetPath) ?? string.Empty : target.WorkingDirectory,
                arguments, DiscoverySource.Shortcut, false));
        }
        return Task.FromResult<IReadOnlyList<TerminalRegistration>>(result);
    }

    private static string[] DefaultRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
    ];
}

public sealed class ComShortcutResolver : IShortcutResolver
{
    public ShortcutTarget? Resolve(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);
            var type = shortcut!.GetType();
            return new ShortcutTarget(
                (string?)type.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) ?? string.Empty,
                (string?)type.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) ?? string.Empty,
                (string?)type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) ?? string.Empty);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
