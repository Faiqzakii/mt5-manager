using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Discovery;

public interface IRunningTerminalEnumerator
{
    IEnumerable<RunningTerminal> Enumerate();
}

public sealed record RunningTerminal(string ExecutablePath, string CommandLine);

public sealed class ProcessDiscoverySource(IRunningTerminalEnumerator? enumerator = null) : ITerminalDiscoverySource
{
    private readonly IRunningTerminalEnumerator _enumerator = enumerator ?? new WindowsRunningTerminalEnumerator();

    public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TerminalRegistration>();
        foreach (var process in _enumerator.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = WindowsProcessQuery.Split(process.CommandLine);
            if (arguments.Count > 0) arguments.RemoveAt(0);
            var data = WindowsProcessQuery.FindDataDirectory(arguments);
            result.Add(new TerminalRegistration(Guid.NewGuid(),
                Path.GetFileName(Path.GetDirectoryName(process.ExecutablePath)) ?? "MetaTrader 5",
                process.ExecutablePath, data ?? string.Empty,
                Path.GetDirectoryName(process.ExecutablePath) ?? string.Empty,
                arguments, DiscoverySource.Process, data is not null && Directory.Exists(data)));
        }
        return Task.FromResult<IReadOnlyList<TerminalRegistration>>(result);
    }
}

public interface IProcessHandleSource
{
    nint Open(int processId);
    void Close(nint handle);
}

// Discovery runs unelevated, so query rights are all that may be requested.
public sealed class LimitedRightsProcessHandleSource : IProcessHandleSource
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    public nint Open(int processId) => OpenProcess(ProcessQueryLimitedInformation, false, processId);

    public void Close(nint handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

public sealed class WindowsRunningTerminalEnumerator(IProcessHandleSource? handles = null) : IRunningTerminalEnumerator
{
    private readonly IProcessHandleSource _handles = handles ?? new LimitedRightsProcessHandleSource();

    public IEnumerable<RunningTerminal> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var process in Process.GetProcessesByName("terminal64"))
        {
            using (process)
            {
                var handle = _handles.Open(process.Id);
                if (handle == nint.Zero) continue;
                try
                {
                    var executable = WindowsProcessQuery.ReadImagePath(handle);
                    if (executable is null) continue;
                    yield return new RunningTerminal(executable, WindowsProcessQuery.ReadCommandLine(handle) ?? Quote(executable));
                }
                finally { _handles.Close(handle); }
            }
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}

// Unelevated inspection of another process: image path, command line and the data directory it was launched with.
internal static class WindowsProcessQuery
{
    public static List<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        if (!OperatingSystem.IsWindows()) return [commandLine];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == nint.Zero) throw new Win32Exception();
        try
        {
            var result = new List<string>(count);
            for (var i = 0; i < count; i++) result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * nint.Size))!);
            return result;
        }
        finally { LocalFree(pointer); }
    }

    public static string? FindDataDirectory(string commandLine)
    {
        var arguments = Split(commandLine);
        if (arguments.Count > 0) arguments.RemoveAt(0);
        return FindDataDirectory(arguments);
    }

    public static string? FindDataDirectory(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.StartsWith("/datadir:", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-datadir:", StringComparison.OrdinalIgnoreCase))
                return argument[(argument.IndexOf(':') + 1)..].Trim('"');
            if ((argument.Equals("/datadir", StringComparison.OrdinalIgnoreCase) ||
                 argument.Equals("-datadir", StringComparison.OrdinalIgnoreCase)) && i + 1 < arguments.Count)
                return arguments[i + 1];
        }
        return null;
    }

    public static string? ReadImagePath(nint handle)
    {
        var size = 32768;
        var buffer = new StringBuilder(size);
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    public static string? ReadCommandLine(nint handle)
    {
        var size = 0;
        NtQueryInformationProcess(handle, 60, nint.Zero, 0, ref size);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, size, ref size) < 0) return null;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == nint.Zero ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString { public readonly ushort Length; public readonly ushort MaximumLength; public readonly nint Buffer; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, nint information, int length, ref int returnLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
