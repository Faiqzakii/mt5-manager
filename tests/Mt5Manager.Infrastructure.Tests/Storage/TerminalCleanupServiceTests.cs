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

    private static void SetDirectoryDeny(string path, string identity, bool deny)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls.exe",
            ArgumentList = { path, deny ? "/deny" : "/remove:d", deny ? $"*{identity}:(OI)(CI)F" : $"*{identity}" },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start icacls.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not update test directory ACL.");
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
