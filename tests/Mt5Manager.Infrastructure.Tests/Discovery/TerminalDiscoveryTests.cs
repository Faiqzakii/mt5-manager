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

    private static TerminalRegistration Candidate(DiscoverySource source, string executable, string data, string name) =>
        new(Guid.NewGuid(), name, executable, data, Path.GetDirectoryName(executable)!, [], source, data.Length > 0);

    private sealed class StubSource(params TerminalRegistration[] candidates) : ITerminalDiscoverySource
    {
        public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalRegistration>>(candidates);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
