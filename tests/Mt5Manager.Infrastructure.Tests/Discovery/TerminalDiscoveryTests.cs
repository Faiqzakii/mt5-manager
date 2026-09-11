using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Tests.Discovery;

public sealed class TerminalDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-discovery-{Guid.NewGuid():N}");

    private readonly List<Process> _spawned = [];

    public TerminalDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Discover_deduplicates_canonical_paths_and_prefers_manual_source()
    {
        var manual = Candidate(DiscoverySource.Manual, @"C:\Apps\MT5\terminal64.exe", @"C:\Users\Me\Data", "Manual");
        var process = Candidate(DiscoverySource.Process, @"c:\apps\mt5\.\terminal64.exe", @"c:\users\me\data\", "Process");
        var discovery = new TerminalDiscovery([new StubSource(process), new StubSource(manual)]);

        var result = await discovery.DiscoverAsync();

        result.Should().ContainSingle();
        result[0].Source.Should().Be(DiscoverySource.Manual);
        result[0].DisplayName.Should().Be("Manual");
        result[0].ExecutablePath.Should().Be(Path.GetFullPath(manual.ExecutablePath));
        result[0].DataDirectory.Should().Be(Path.GetFullPath(manual.DataDirectory));
    }

    [Fact]
    public async Task Discover_keeps_same_executable_when_data_roots_differ()
    {
        var first = Candidate(DiscoverySource.Process, @"C:\MT5\terminal64.exe", @"C:\Data-One", "One");
        var second = Candidate(DiscoverySource.Shortcut, @"C:\MT5\terminal64.exe", @"C:\Data-Two", "Two");

        var result = await new TerminalDiscovery([new StubSource(first, second)]).DiscoverAsync();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Discover_keeps_unknown_data_directory_visible_and_unverified()
    {
        var unknown = Candidate(DiscoverySource.Process, @"C:\MT5\terminal64.exe", string.Empty, "Running") with
        {
            DataDirectoryVerified = false
        };

        var result = await new TerminalDiscovery([new StubSource(unknown)]).DiscoverAsync();

        result.Should().ContainSingle();
        result[0].DataDirectory.Should().BeEmpty();
        result[0].DataDirectoryVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Discover_applies_process_shortcut_standard_precedence()
    {
        var path = @"C:\MT5\terminal64.exe";
        var data = @"C:\Data";
        var standard = Candidate(DiscoverySource.StandardLocation, path, data, "Standard");
        var shortcut = Candidate(DiscoverySource.Shortcut, path, data, "Shortcut");
        var process = Candidate(DiscoverySource.Process, path, data, "Process");

        var result = await new TerminalDiscovery([
            new StubSource(standard), new StubSource(shortcut), new StubSource(process)]).DiscoverAsync();

        result.Should().ContainSingle().Which.Source.Should().Be(DiscoverySource.Process);
    }

    [Fact]
    public async Task Discover_skips_candidates_that_fail_registry_validation()
    {
        var registry = new JsonTerminalRegistry(Path.Combine(_root, "terminals.json"));
        var invalid = Candidate(DiscoverySource.Process, @"C:\MT5\terminal64.exe", @"C:\Data", " ");
        var valid = Candidate(DiscoverySource.Process, @"D:\MT5\terminal64.exe", @"D:\Data", "Valid");

        var act = async () => await new TerminalDiscovery([new StubSource(invalid, valid)], registry).DiscoverAsync();

        await act.Should().NotThrowAsync();
        (await registry.LoadAsync()).Should().ContainSingle().Which.DisplayName.Should().Be("Valid");
    }

    [Fact]
    public async Task Discover_preserves_manual_registrations_from_registry()
    {
        var registry = new JsonTerminalRegistry(Path.Combine(_root, "terminals.json"));
        var manual = Candidate(DiscoverySource.Manual, @"C:\MT5\terminal64.exe", @"C:\Data", "Manual");
        await registry.SaveAsync([manual]);
        var process = Candidate(DiscoverySource.Process, @"C:\MT5\terminal64.exe", @"C:\Data", "Process");

        var result = await new TerminalDiscovery([new StubSource(process)], registry).DiscoverAsync();

        result.Should().ContainSingle();
        result[0].Source.Should().Be(DiscoverySource.Manual);
        (await registry.LoadAsync()).Should().ContainSingle().Which.Source.Should().Be(DiscoverySource.Manual);
    }

    [Fact]
    public async Task Discover_enriches_an_unverified_terminal_before_deduplication_and_persistence()
    {
        var registry = new JsonTerminalRegistry(Path.Combine(_root, "terminals.json"));
        var executable = Path.Combine(_root, "Broker", "terminal64.exe");
        var candidate = Candidate(DiscoverySource.StandardLocation, executable, string.Empty, "Broker");
        var resolvedData = Directory.CreateDirectory(Path.Combine(_root, "ResolvedData")).FullName;
        var resolver = new RecordingDataDirectoryResolver(resolvedData);

        var result = await new TerminalDiscovery([new StubSource(candidate, candidate)], registry, resolver).DiscoverAsync();

        result.Should().ContainSingle();
        result[0].DataDirectory.Should().Be(resolvedData);
        result[0].DataDirectoryVerified.Should().BeTrue();
        resolver.Calls.Should().Be(2);
        (await registry.LoadAsync()).Should().ContainSingle().Which.DataDirectory.Should().Be(resolvedData);
    }

    [Fact]
    public async Task Discover_standard_locations_survives_inaccessible_subdirectory()
    {
        var portableRoot = Path.Combine(_root, "Portable");
        var denied = Path.Combine(portableRoot, "Denied");
        Directory.CreateDirectory(denied);
        var executable = Path.Combine(portableRoot, "terminal64.exe");
        File.WriteAllText(executable, "stub");
        File.WriteAllText(Path.Combine(denied, "terminal64.exe"), "stub");
        var rule = DenyListing(denied);

        try
        {
            var source = new StandardLocationDiscoverySource(portableRoots: [portableRoot]);

            // The source also scans conventional Program Files, so assert on the injected root only.
            var result = await source.DiscoverAsync();

            result.Should().ContainSingle(item => item.ExecutablePath == executable);
            result.Should().NotContain(item => item.ExecutablePath == Path.Combine(denied, "terminal64.exe"));
        }
        finally
        {
            AllowListing(denied, rule);
        }
    }

    [Fact]
    public async Task Discover_shortcuts_survives_inaccessible_subdirectory()
    {
        var shortcutRoot = Path.Combine(_root, "Shortcuts");
        var denied = Path.Combine(shortcutRoot, "Denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(shortcutRoot, "MetaTrader 5.lnk"), "stub");
        File.WriteAllText(Path.Combine(denied, "Hidden.lnk"), "stub");
        var rule = DenyListing(denied);

        try
        {
            var source = new ShortcutDiscoverySource(shortcutRoots: [shortcutRoot], resolver: new StubResolver());

            var result = await source.DiscoverAsync();

            result.Should().ContainSingle().Which.DisplayName.Should().Be("MetaTrader 5");
        }
        finally
        {
            AllowListing(denied, rule);
        }
    }

    [Fact]
    public async Task Enumerate_continues_when_one_terminal_handle_cannot_be_opened()
    {
        var (directory, executable) = CreateTerminalFixture();
        var blocked = StartTerminal(executable, Path.Combine(directory, "blocked.json"));
        var surviving = StartTerminal(executable, Path.Combine(directory, "surviving.json"));

        try
        {
            // Both terminals must be running before the assertion, so absence can only mean the handle failed.
            await WaitForFileAsync(Path.Combine(directory, "blocked.json"));
            await WaitForFileAsync(Path.Combine(directory, "surviving.json"));
            blocked.HasExited.Should().BeFalse();
            var terminals = new WindowsRunningTerminalEnumerator(new BlockingHandleSource(blocked.Id))
                .Enumerate().ToArray();

            terminals.Should().ContainSingle(item => item.CommandLine.Contains("surviving.json"));
            terminals.Should().NotContain(item => item.CommandLine.Contains("blocked.json"));
        }
        finally
        {
            Kill(blocked, surviving);
        }
    }

    [Fact]
    public async Task Enumerate_resolves_executable_and_command_line_of_running_terminal()
    {
        var (directory, executable) = CreateTerminalFixture();
        var dataDirectory = Path.Combine(directory, "Data");
        var process = StartTerminal(executable, Path.Combine(directory, "sole.json"), $"/datadir:{dataDirectory}");

        try
        {
            await WaitForFileAsync(Path.Combine(directory, "sole.json"));

            var terminals = await WaitForTerminalAsync(new WindowsRunningTerminalEnumerator(), "sole.json");
            var terminal = terminals.Should().ContainSingle(item => item.CommandLine.Contains("sole.json")).Subject;

            terminal.ExecutablePath.Should().Be(executable);
            terminal.CommandLine.Should().Contain($"/datadir:{dataDirectory}");
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Discover_names_a_terminal_whose_executable_sits_at_a_drive_root()
    {
        var source = new ProcessDiscoverySource(new StubEnumerator(
            new RunningTerminal(@"C:\terminal64.exe", @"C:\terminal64.exe")));

        var found = await source.DiscoverAsync();

        var terminal = found.Should().ContainSingle().Subject;
        terminal.DisplayName.Should().Be("MetaTrader 5");
    }

    private static TerminalRegistration Candidate(DiscoverySource source, string executable, string data, string name) =>
        new(Guid.NewGuid(), name, executable, data, Path.GetDirectoryName(executable)!, [], source, data.Length > 0);


    private (string Directory, string Executable) CreateTerminalFixture()
    {
        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "ExitOnClose",
            "bin", "Debug", "net8.0-windows");
        File.Exists(Path.Combine(fixtureRoot, "ExitOnClose.exe"))
            .Should().BeTrue($"fixture should be built at {fixtureRoot}");
        var directory = Directory.CreateDirectory(Path.Combine(_root, $"terminal64-{Guid.NewGuid():N}")).FullName;
        foreach (var file in Directory.GetFiles(fixtureRoot))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var executable = Path.Combine(directory, "terminal64.exe");
        File.Move(Path.Combine(directory, "ExitOnClose.exe"), executable);
        return (directory, executable);
    }

    private Process StartTerminal(string executable, string outputPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("ignore");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start fixture.");
        _spawned.Add(process);
        return process;
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++) await Task.Delay(25);
        File.Exists(path).Should().BeTrue($"fixture should have started and written {path}");
    }

    private static async Task<IReadOnlyList<RunningTerminal>> WaitForTerminalAsync(
        IRunningTerminalEnumerator enumerator, string marker)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var terminals = enumerator.Enumerate().ToArray();
            if (terminals.Any(item => item.CommandLine.Contains(marker))) return terminals;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Terminal '{marker}' was not enumerated.");
    }

    private static void Kill(params Process[] processes)
    {
        foreach (var process in processes) Kill(process);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mt5Manager.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class BlockingHandleSource(int blockedProcessId) : IProcessHandleSource
    {
        private readonly LimitedRightsProcessHandleSource _inner = new();

        public nint Open(int processId) => processId == blockedProcessId ? nint.Zero : _inner.Open(processId);

        public void Close(nint handle) => _inner.Close(handle);
    }

    private sealed class StubSource(params TerminalRegistration[] candidates) : ITerminalDiscoverySource
    {
        public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalRegistration>>(candidates);
    }

    private sealed class RecordingDataDirectoryResolver(string result) : IMt5DataDirectoryResolver
    {
        public int Calls { get; private set; }
        public string? Resolve(TerminalRegistration terminal) { Calls++; return result; }
    }

    private sealed class StubEnumerator(params RunningTerminal[] terminals) : IRunningTerminalEnumerator
    {
        public IEnumerable<RunningTerminal> Enumerate() => terminals;
    }

    private static FileSystemAccessRule DenyListing(string directory)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return rule;
    }

    private static void AllowListing(string directory, FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(security);
    }

    private sealed class StubResolver : IShortcutResolver
    {
        public ShortcutTarget? Resolve(string shortcutPath) =>
            new(@"C:\MT5\terminal64.exe", "/portable", @"C:\MT5");
    }

    public void Dispose()
    {
        foreach (var process in _spawned) Kill(process);
        for (var attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
