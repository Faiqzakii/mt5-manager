using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Deployment;

namespace Mt5Manager.Infrastructure.Tests.Deployment;

public sealed class Mt5PackageInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mt5-package-{Guid.NewGuid():N}");
    private readonly Mt5PackageInstaller installer = new();

    public Mt5PackageInstallerTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(Mt5PackageKind.ExpertAdvisor, "Experts")]
    [InlineData(Mt5PackageKind.Indicator, "Indicators")]
    public async Task Install_copies_identical_bytes_to_the_kind_directory(Mt5PackageKind kind, string folder)
    {
        var source = Source([0, 1, 2, 128, 255]);
        var terminal = Terminal("Selected");

        var result = await installer.InstallAsync(source, kind, [terminal]);

        result.Success.Should().BeTrue();
        result.Outcomes.Should().ContainSingle().Which.DestinationPath.Should().Be(Path.Combine(terminal.DataDirectory, "MQL5", folder, "Package.ex5"));
        File.ReadAllBytes(result.Outcomes[0].DestinationPath!).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Install_targets_exactly_the_supplied_selected_or_all_terminals()
    {
        var source = Source([4, 5, 6]);
        var first = Terminal("First");
        var second = Terminal("Second");

        var selected = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [first]);
        var all = await installer.InstallAsync(source, Mt5PackageKind.Indicator, [first, second]);

        selected.Outcomes.Select(x => x.TerminalId).Should().Equal(first.Id);
        all.Outcomes.Select(x => x.TerminalId).Should().Equal(first.Id, second.Id);
        File.Exists(Path.Combine(second.DataDirectory, "MQL5", "Experts", "Package.ex5")).Should().BeFalse();
    }

    [Fact]
    public async Task Install_atomically_overwrites_an_existing_package()
    {
        var source = Source([9, 8, 7]);
        var terminal = Terminal("Broker");
        var destination = Path.Combine(terminal.DataDirectory, "MQL5", "Experts", "Package.ex5");
        File.WriteAllBytes(destination, [1]);

        var result = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [terminal]);

        result.Success.Should().BeTrue();
        File.ReadAllBytes(destination).Should().Equal(9, 8, 7);
        Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Missing.ex5", "does not exist")]
    [InlineData("Package.bin", ".ex5 extension")]
    public async Task Install_rejects_an_invalid_source_for_every_terminal(string fileName, string expected)
    {
        var terminal = Terminal("Broker");
        var source = Path.Combine(root, fileName);
        if (Path.GetExtension(source) != ".ex5") File.WriteAllBytes(source, [1]);

        var result = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [terminal]);

        result.Outcomes.Should().ContainSingle().Which.Message.Should().Contain(expected);
        result.Outcomes[0].DisplayName.Should().Be("Broker");
        Directory.GetFiles(Path.Combine(terminal.DataDirectory, "MQL5", "Experts")).Should().BeEmpty();
    }

    [Fact]
    public async Task Install_rejects_an_unverified_terminal_without_writing()
    {
        var source = Source([1]);
        var terminal = Terminal("Unsafe") with { DataDirectoryVerified = false };

        var result = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [terminal]);

        result.Outcomes[0].Success.Should().BeFalse();
        result.Outcomes[0].Message.Should().Contain("verified data directory");
        Directory.GetFiles(Path.Combine(terminal.DataDirectory, "MQL5", "Experts")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, "MQL5 root")]
    [InlineData(true, false, "Experts directory")]
    public async Task Install_rejects_missing_roots(bool createMql5, bool createExperts, string expected)
    {
        var source = Source([1]);
        var data = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        if (createMql5) Directory.CreateDirectory(Path.Combine(data, "MQL5"));
        if (createExperts) Directory.CreateDirectory(Path.Combine(data, "MQL5", "Experts"));
        var terminal = Registration("Incomplete", data);

        var result = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [terminal]);

        result.Outcomes[0].Message.Should().Contain(expected);
        result.Outcomes[0].DestinationPath.Should().BeNull();
    }

    [Fact]
    public async Task Install_deduplicates_equivalent_destination_paths()
    {
        var source = Source([3]);
        var first = Terminal("First");
        var duplicate = Registration("Duplicate", first.DataDirectory + Path.DirectorySeparatorChar);

        var result = await installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [first, duplicate]);

        result.Outcomes.Should().HaveCount(2);
        result.Outcomes[0].Success.Should().BeTrue();
        result.Outcomes[1].Success.Should().BeFalse();
        result.Outcomes[1].Message.Should().Contain("already targeted");
    }

    [Fact]
    public async Task Install_continues_after_a_terminal_failure()
    {
        var source = Source([2]);
        var rejected = Terminal("Rejected") with { DataDirectoryVerified = false };
        var accepted = Terminal("Accepted");

        var result = await installer.InstallAsync(source, Mt5PackageKind.Indicator, [rejected, accepted]);

        result.Success.Should().BeFalse();
        result.Outcomes.Select(x => x.Success).Should().Equal(false, true);
        File.Exists(result.Outcomes[1].DestinationPath).Should().BeTrue();
    }

    [Fact]
    public async Task Install_honors_pre_cancellation_without_writing_or_leaving_temps()
    {
        var source = Source(new byte[1024]);
        var terminal = Terminal("Canceled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => installer.InstallAsync(source, Mt5PackageKind.ExpertAdvisor, [terminal], cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(Path.Combine(terminal.DataDirectory, "MQL5", "Experts")).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private string Source(byte[] bytes)
    {
        var path = Path.Combine(root, "Package.ex5");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private TerminalRegistration Terminal(string name)
    {
        var data = Path.Combine(root, $"terminal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(data, "MQL5", "Experts"));
        Directory.CreateDirectory(Path.Combine(data, "MQL5", "Indicators"));
        return Registration(name, data);
    }

    private static TerminalRegistration Registration(string name, string data) =>
        new(Guid.NewGuid(), name, Path.Combine(data, "terminal64.exe"), data, data, [], DiscoverySource.Manual, true);
}
