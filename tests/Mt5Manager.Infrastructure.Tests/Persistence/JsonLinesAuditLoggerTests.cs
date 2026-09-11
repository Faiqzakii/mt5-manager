using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Tests.Persistence;

public sealed class JsonLinesAuditLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-audit-{Guid.NewGuid():N}");
    private string AuditPath => Path.Combine(_root, "audit.jsonl");

    public JsonLinesAuditLoggerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Appends_one_json_object_per_record_without_rewriting_earlier_records()
    {
        var logger = new JsonLinesAuditLogger(AuditPath);
        var first = Record(Guid.NewGuid(), AuditOutcome.Completed);
        var second = Record(Guid.NewGuid(), AuditOutcome.ForceDeclined);

        await logger.AppendAsync(first);
        await logger.AppendAsync(second);

        var lines = await File.ReadAllLinesAsync(AuditPath);
        lines.Should().HaveCount(2);

        var firstEntry = JsonDocument.Parse(lines[0]).RootElement;
        firstEntry.GetProperty("terminalId").GetGuid().Should().Be(first.TerminalId);
        firstEntry.GetProperty("outcome").GetString().Should().Be("Completed");
        firstEntry.GetProperty("timestamp").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var secondEntry = JsonDocument.Parse(lines[1]).RootElement;
        secondEntry.GetProperty("terminalId").GetGuid().Should().Be(second.TerminalId);
        secondEntry.GetProperty("terminalName").GetString().Should().Be("Terminal 1");
        secondEntry.GetProperty("operation").GetString().Should().Be("Cleanup");
        secondEntry.GetProperty("outcome").GetString().Should().Be("ForceDeclined");
        secondEntry.GetProperty("shutdownMethod").GetString().Should().Be("Graceful");
        secondEntry.GetProperty("wasRunning").GetBoolean().Should().BeTrue();
        secondEntry.GetProperty("restarted").GetBoolean().Should().BeFalse();
        secondEntry.GetProperty("categories")[0].GetString().Should().Be("Logs");

        var categoryOutcome = secondEntry.GetProperty("categoryOutcomes")[0];
        categoryOutcome.GetProperty("category").GetString().Should().Be("Logs");
        categoryOutcome.GetProperty("deletedFiles").GetInt64().Should().Be(7);
        categoryOutcome.GetProperty("deletedBytes").GetInt64().Should().Be(700);
        categoryOutcome.GetProperty("failures")[0].GetProperty("path").GetString().Should().Be(@"C:\Data\Logs\a.log");
        categoryOutcome.GetProperty("failures")[0].GetProperty("error").GetString().Should().Be("Access denied.");
    }

    [Fact]
    public async Task Concurrent_appends_produce_one_well_formed_line_per_record()
    {
        var logger = new JsonLinesAuditLogger(AuditPath);
        var terminalIds = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(terminalIds.Select(id => logger.AppendAsync(Record(id, AuditOutcome.Completed))));

        var lines = await File.ReadAllLinesAsync(AuditPath);
        lines.Should().HaveCount(terminalIds.Length);
        lines.Select(ReadTerminalId).Should().BeEquivalentTo(terminalIds);
    }

    [Fact]
    public async Task Creates_the_audit_directory_when_it_is_missing()
    {
        var path = Path.Combine(_root, "nested", "audit.jsonl");

        await new JsonLinesAuditLogger(path).AppendAsync(Record(Guid.NewGuid(), AuditOutcome.Completed));

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_does_not_break_already_started_appends()
    {
        var logger = new JsonLinesAuditLogger(AuditPath);
        var appends = Enumerable.Range(0, 100)
            .Select(_ => logger.AppendAsync(Record(Guid.NewGuid(), AuditOutcome.Completed)))
            .ToArray();

        logger.Dispose();

        await Task.WhenAll(appends);
        (await File.ReadAllLinesAsync(AuditPath)).Should().HaveCount(100);
    }

    [Fact]
    public async Task Append_after_dispose_is_rejected()
    {
        var logger = new JsonLinesAuditLogger(AuditPath);
        logger.Dispose();

        var action = () => logger.AppendAsync(Record(Guid.NewGuid(), AuditOutcome.Completed));

        await action.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static Guid ReadTerminalId(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("terminalId").GetGuid();
    }

    private static AuditRecord Record(Guid terminalId, AuditOutcome outcome) =>
        new(DateTimeOffset.UtcNow,
            terminalId,
            "Terminal 1",
            "Cleanup",
            [CleanupCategory.Logs],
            WasRunning: true,
            ShutdownMethod.Graceful,
            outcome,
            Message: null,
            [new AuditCategoryOutcome(CleanupCategory.Logs, 7, 700, [new FileFailure(@"C:\Data\Logs\a.log", "Access denied.")])],
            Restarted: false,
            RestartError: null);
}
