using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;

namespace Mt5Manager.Infrastructure.Tests.Discovery;

public sealed class TerminalDiscoveryTests
{
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

    private static TerminalRegistration Candidate(DiscoverySource source, string executable, string data, string name) =>
        new(Guid.NewGuid(), name, executable, data, Path.GetDirectoryName(executable)!, [], source, data.Length > 0);

    private sealed class StubSource(params TerminalRegistration[] candidates) : ITerminalDiscoverySource
    {
        public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalRegistration>>(candidates);
    }
}
