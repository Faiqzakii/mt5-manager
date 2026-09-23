using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;

namespace Mt5Manager.Infrastructure.Tests.Discovery;

public sealed class Mt5DataDirectoryResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mt5-data-resolver-{Guid.NewGuid():N}");
    private readonly string appData;
    private readonly string installation;
    private readonly string executable;

    public Mt5DataDirectoryResolverTests()
    {
        appData = Directory.CreateDirectory(Path.Combine(root, "MetaQuotes", "Terminal")).FullName;
        installation = Directory.CreateDirectory(Path.Combine(root, "Broker MT5")).FullName;
        executable = Path.Combine(installation, "terminal64.exe");
        File.WriteAllText(executable, "fixture");
    }

    [Fact]
    public void Resolve_matches_the_unique_structured_origin_directory()
    {
        var expected = Candidate("A1", installation);
        Candidate("B2", Path.Combine(root, "Different MT5"));

        var resolved = new Mt5DataDirectoryResolver(appData).Resolve(Terminal([]));

        resolved.Should().Be(expected);
    }

    [Fact]
    public void Resolve_accepts_portable_installation_only_with_explicit_portable_argument_and_structure()
    {
        CreateStructure(installation);
        var resolver = new Mt5DataDirectoryResolver(appData);

        resolver.Resolve(Terminal(["/portable"])).Should().Be(installation);
        resolver.Resolve(Terminal([])).Should().BeNull();
    }

    [Fact]
    public void Resolve_portable_uses_executable_directory_not_shortcut_start_in()
    {
        CreateStructure(installation);
        var unrelatedStartIn = Directory.CreateDirectory(Path.Combine(root, "Unrelated MT5 Data")).FullName;
        CreateStructure(unrelatedStartIn);
        var terminal = Terminal(["/portable"]) with { WorkingDirectory = unrelatedStartIn };

        var resolved = new Mt5DataDirectoryResolver(appData).Resolve(terminal);

        resolved.Should().Be(installation);
    }

    [Fact]
    public void Resolve_explicit_datadir_wins_over_conflicting_origin_metadata()
    {
        Candidate("A1", installation);
        var explicitData = Directory.CreateDirectory(Path.Combine(root, "Explicit Data")).FullName;
        CreateStructure(explicitData);
        var terminal = Terminal([$"/datadir:{explicitData}"]);

        new Mt5DataDirectoryResolver(appData).Resolve(terminal).Should().Be(explicitData);
    }

    [Fact]
    public void Resolve_invalid_explicit_datadir_does_not_fall_back_to_origin()
    {
        Candidate("A1", installation);
        var invalid = Directory.CreateDirectory(Path.Combine(root, "Invalid Explicit Data")).FullName;
        var terminal = Terminal([$"/datadir:{invalid}"]);

        new Mt5DataDirectoryResolver(appData).Resolve(terminal).Should().BeNull();
    }

    [Fact]
    public void Resolve_rejects_matching_origin_without_mt5_data_structure()
    {
        var candidate = Directory.CreateDirectory(Path.Combine(appData, "A1")).FullName;
        File.WriteAllText(Path.Combine(candidate, "origin.txt"), installation);

        new Mt5DataDirectoryResolver(appData).Resolve(Terminal([])).Should().BeNull();
    }

    [Fact]
    public void Resolve_rejects_ambiguous_matching_origin_directories()
    {
        Candidate("A1", installation);
        Candidate("B2", installation + Path.DirectorySeparatorChar);

        new Mt5DataDirectoryResolver(appData).Resolve(Terminal([])).Should().BeNull();
    }

    [Fact]
    public void Resolve_rechecks_a_previously_verified_directory()
    {
        var corrected = Candidate("A1", installation);
        var wrong = Directory.CreateDirectory(Path.Combine(root, "Old Data")).FullName;
        CreateStructure(wrong);
        var terminal = Terminal([]) with { DataDirectory = wrong, DataDirectoryVerified = true };

        new Mt5DataDirectoryResolver(appData).Resolve(terminal).Should().Be(corrected);
    }

    private string Candidate(string name, string origin)
    {
        var candidate = Directory.CreateDirectory(Path.Combine(appData, name)).FullName;
        File.WriteAllText(Path.Combine(candidate, "origin.txt"), origin);
        CreateStructure(candidate);
        return candidate;
    }

    private static void CreateStructure(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        Directory.CreateDirectory(Path.Combine(directory, "bases"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
    }

    private TerminalRegistration Terminal(IReadOnlyList<string> arguments) =>
        new(Guid.NewGuid(), "Broker MT5", executable, string.Empty, installation, arguments, DiscoverySource.StandardLocation, false);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }
}
