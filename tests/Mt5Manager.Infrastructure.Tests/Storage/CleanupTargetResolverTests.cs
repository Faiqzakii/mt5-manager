using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Storage;

namespace Mt5Manager.Infrastructure.Tests.Storage;

public sealed class CleanupTargetResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-cleanup-{Guid.NewGuid():N}");

    public CleanupTargetResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolve_logs_returns_only_existing_fixed_log_directories_in_canonical_order()
    {
        var rootLog = CreateDirectory("Logs");
        var mqlLog = CreateDirectory("MQL5", "Logs");
        CreateDirectory("MQL5", "Files");

        var targets = Resolve(CleanupCategory.Logs);

        targets.Should().Equal(Path.GetFullPath(rootLog), Path.GetFullPath(mqlLog));
    }

    [Fact]
    public void Resolve_ticks_expands_only_immediate_broker_directories()
    {
        var first = CreateDirectory("bases", "BrokerA", "ticks");
        var second = CreateDirectory("bases", "BrokerB", "ticks");
        CreateDirectory("bases", "BrokerA", "nested", "ticks");
        CreateDirectory("ticks");

        var targets = Resolve(CleanupCategory.Ticks);

        targets.Should().Equal(new[] { Path.GetFullPath(first), Path.GetFullPath(second) }.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_history_expands_only_immediate_broker_directories()
    {
        var first = CreateDirectory("bases", "BrokerA", "history");
        var second = CreateDirectory("bases", "BrokerB", "history");
        CreateDirectory("bases", "BrokerA", "nested", "history");
        CreateDirectory("history");

        var targets = Resolve(CleanupCategory.History);

        targets.Should().Equal(new[] { Path.GetFullPath(first), Path.GetFullPath(second) }.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_rejects_unverified_data_root()
    {
        var registration = Registration(_root, verified: false);

        var act = () => new CleanupTargetResolver().Resolve(registration, CleanupCategory.Logs);

        act.Should().Throw<InvalidOperationException>().WithMessage("*verified*");
    }

    [Fact]
    public void Resolve_rejects_data_root_that_canonicalizes_outside_declared_path()
    {
        var declaredRoot = Path.Combine(_root, "declared", "..", "actual");
        Directory.CreateDirectory(Path.Combine(_root, "actual", "Logs"));
        var registration = Registration(declaredRoot);

        var act = () => new CleanupTargetResolver().Resolve(registration, CleanupCategory.Logs);

        act.Should().Throw<InvalidOperationException>().WithMessage("*canonical*");
    }

    [Fact]
    public void Resolve_rejects_reparse_point_segment_before_target()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"mt5-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(outside, "ticks"));
        CreateDirectory("bases");
        var link = Path.Combine(_root, "bases", "BrokerEscape");

        try
        {
            CreateJunction(link, outside);
            var act = () => Resolve(CleanupCategory.Ticks);
            act.Should().Throw<InvalidOperationException>().WithMessage("*reparse*");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not create test junction.");
    }

    private IReadOnlyList<string> Resolve(CleanupCategory category) =>
        new CleanupTargetResolver().Resolve(Registration(_root), category);

    private static TerminalRegistration Registration(string dataDirectory, bool verified = true) =>
        new(Guid.NewGuid(), "Test", "terminal64.exe", dataDirectory, dataDirectory, [], DiscoverySource.Manual, verified);

    private string CreateDirectory(params string[] segments)
    {
        var path = segments.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
