using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Storage;

namespace Mt5Manager.Infrastructure.Tests.Storage;

public sealed class TerminalStorageInspectorTests : StorageTestBase
{
    [Fact]
    public async Task InspectAsync_returns_exact_recursive_file_counts_and_bytes_by_category()
    {
        WriteFile(new byte[3], "Logs", "root.log");
        WriteFile(new byte[5], "MQL5", "Logs", "nested", "mql.log");
        WriteFile(new byte[7], "bases", "BrokerA", "ticks", "2026", "ticks.dat");
        WriteFile(new byte[11], "bases", "BrokerA", "history", "history.dat");

        var result = await new TerminalStorageInspector(new CleanupTargetResolver())
            .InspectAsync(Registration, CancellationToken.None);

        result.Should().Equal(
            new CategoryUsage(CleanupCategory.Logs, 2, 8),
            new CategoryUsage(CleanupCategory.Ticks, 1, 7),
            new CategoryUsage(CleanupCategory.History, 1, 11));
    }

    [Fact]
    public async Task InspectAsync_skips_nested_reparse_directories()
    {
        var outside = CreateOutsideDirectory();
        WriteOutsideFile(outside, new byte[13], "escaped.dat");
        var link = CreateJunction(Path.Combine(Root, "Logs", "linked"), outside);
        WriteFile(new byte[2], "Logs", "kept.log");

        try
        {
            var result = await new TerminalStorageInspector(new CleanupTargetResolver())
                .InspectAsync(Registration, CancellationToken.None);

            result.Single(x => x.Category == CleanupCategory.Logs).Should().Be(new CategoryUsage(CleanupCategory.Logs, 1, 2));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }
}
