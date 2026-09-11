using System.Runtime.Versioning;
using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Storage;

namespace Mt5Manager.Infrastructure.Tests.Storage;

[SupportedOSPlatform("windows")]
public sealed class TerminalCleanupServiceTests : StorageTestBase
{
    [Fact]
    public async Task CleanAsync_deletes_only_selected_category_files_and_preserves_directories()
    {
        var logsDirectory = CreateDirectory("Logs", "nested");
        var log = WriteFile(new byte[3], "Logs", "nested", "terminal.log");
        var tick = WriteFile(new byte[5], "bases", "BrokerA", "ticks", "ticks.dat");

        var result = await new TerminalCleanupService(new CleanupTargetResolver())
            .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, CancellationToken.None);

        File.Exists(log).Should().BeFalse();
        Directory.Exists(logsDirectory).Should().BeTrue();
        File.Exists(tick).Should().BeTrue();
        var category = result.Should().ContainSingle().Subject;
        category.Category.Should().Be(CleanupCategory.Logs);
        category.DeletedFiles.Should().Be(1);
        category.DeletedBytes.Should().Be(3);
        category.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanAsync_skips_files_below_nested_reparse_directories()
    {
        var outside = CreateOutsideDirectory();
        var escaped = WriteOutsideFile(outside, new byte[13], "escaped.log");
        var link = CreateJunction(Path.Combine(Root, "Logs", "linked"), outside);

        try
        {
            var result = await new TerminalCleanupService(new CleanupTargetResolver())
                .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, CancellationToken.None);

            File.Exists(escaped).Should().BeTrue();
            result.Single().DeletedFiles.Should().Be(0);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task CleanAsync_records_denied_target_and_continues_with_later_target()
    {
        var deniedDirectory = CreateDirectory("Logs");
        WriteFile(new byte[7], "Logs", "blocked.log");
        var deletable = WriteFile(new byte[5], "MQL5", "Logs", "deletable.log");
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        SetDirectoryDeny(deniedDirectory, identity, deny: true);

        try
        {
            var result = await new TerminalCleanupService(new CleanupTargetResolver())
                .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, CancellationToken.None);

            var category = result.Single();
            category.DeletedFiles.Should().Be(1);
            category.DeletedBytes.Should().Be(5);
            category.Failures.Should().ContainSingle(x => x.Path == deniedDirectory && !string.IsNullOrWhiteSpace(x.Error));
            File.Exists(deletable).Should().BeFalse();
        }
        finally
        {
            SetDirectoryDeny(deniedDirectory, identity, deny: false);
        }
    }

    [Fact]
    public async Task CleanAsync_refuses_to_traverse_a_target_that_is_a_reparse_point()
    {
        var outside = CreateOutsideDirectory();
        var escaped = WriteOutsideFile(outside, new byte[13], "escaped.log");
        var link = CreateJunction(Path.Combine(Root, "Logs"), outside);

        try
        {
            var result = await new TerminalCleanupService(new StubResolver(Root, "Logs"))
                .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, CancellationToken.None);

            File.Exists(escaped).Should().BeTrue("a reparse target must never be traversed");
            var category = result.Single();
            category.DeletedFiles.Should().Be(0);
            category.Failures.Should().ContainSingle(x => x.Path == link && !string.IsNullOrWhiteSpace(x.Error));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task CleanAsync_keeps_completed_categories_when_a_later_resolution_fails()
    {
        var log = WriteFile(new byte[3], "Logs", "terminal.log");
        var tick = WriteFile(new byte[5], "bases", "BrokerA", "ticks", "ticks.dat");
        var history = WriteFile(new byte[7], "bases", "BrokerA", "history", "history.dat");
        var categories = new HashSet<CleanupCategory>
        {
            CleanupCategory.Logs, CleanupCategory.Ticks, CleanupCategory.History
        };

        var result = await new TerminalCleanupService(new FailingResolver(CleanupCategory.Ticks))
            .CleanAsync(Registration, categories, CancellationToken.None);

        result.Select(x => x.Category).Should().Equal(
            CleanupCategory.Logs, CleanupCategory.Ticks, CleanupCategory.History);
        result.Single(x => x.Category == CleanupCategory.Logs).DeletedFiles.Should().Be(1);
        var failed = result.Single(x => x.Category == CleanupCategory.Ticks);
        failed.DeletedFiles.Should().Be(0);
        failed.Failures.Should().ContainSingle(x => !string.IsNullOrWhiteSpace(x.Error));
        result.Single(x => x.Category == CleanupCategory.History).DeletedFiles.Should().Be(1);

        File.Exists(log).Should().BeFalse();
        File.Exists(tick).Should().BeTrue();
        File.Exists(history).Should().BeFalse();
    }

    private sealed class FailingResolver(CleanupCategory failing) : Application.Abstractions.ICleanupTargetResolver
    {
        private readonly CleanupTargetResolver _inner = new();

        public IReadOnlyList<string> Resolve(TerminalRegistration terminal, CleanupCategory category) =>
            category == failing
                ? throw new InvalidOperationException("Cleanup path contains a reparse point.")
                : _inner.Resolve(terminal, category);
    }

    private sealed class StubResolver(string root, params string[] relativeTargets) : Application.Abstractions.ICleanupTargetResolver
    {
        public IReadOnlyList<string> Resolve(TerminalRegistration terminal, CleanupCategory category) =>
            category == CleanupCategory.Logs
                ? relativeTargets.Select(target => Path.Combine(root, target)).ToArray()
                : [];
    }

    [Fact]
    public async Task CleanAsync_honors_cancellation_before_deleting_a_file()
    {
        var file = WriteFile(new byte[3], "Logs", "terminal.log");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => new TerminalCleanupService(new CleanupTargetResolver())
            .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task CleanAsync_records_locked_file_failure_and_continues_deleting()
    {
        var locked = WriteFile(new byte[7], "Logs", "a-locked.log");
        var deletable = WriteFile(new byte[5], "Logs", "z-deletable.log");
        await using var lockStream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new TerminalCleanupService(new CleanupTargetResolver())
            .CleanAsync(Registration, new HashSet<CleanupCategory> { CleanupCategory.Logs }, CancellationToken.None);

        var category = result.Single();
        category.DeletedFiles.Should().Be(1);
        category.DeletedBytes.Should().Be(5);
        category.Failures.Should().ContainSingle(x => x.Path == locked && !string.IsNullOrWhiteSpace(x.Error));
        File.Exists(locked).Should().BeTrue();
        File.Exists(deletable).Should().BeFalse();
    }
}
