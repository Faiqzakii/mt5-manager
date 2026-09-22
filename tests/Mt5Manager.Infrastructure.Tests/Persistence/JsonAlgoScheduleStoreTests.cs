using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Tests.Persistence;

public sealed class JsonAlgoScheduleStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mt5-schedules-{Guid.NewGuid():N}");
    private string StorePath => Path.Combine(root, "algo-schedules.json");

    public JsonAlgoScheduleStoreTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Save_and_load_round_trip_with_atomic_overwrite()
    {
        var store = new JsonAlgoScheduleStore(StorePath);
        var first = State("Asia on", true);
        var replacement = State("London off", false);

        await store.SaveAsync(first);
        await store.SaveAsync(replacement);

        (await store.LoadAsync()).Should().BeEquivalentTo(replacement);
        Directory.EnumerateFiles(root).Should().ContainSingle().Which.Should().Be(StorePath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        json.RootElement.GetProperty("version").GetInt32().Should().Be(JsonAlgoScheduleStore.CurrentVersion);
    }

    [Fact]
    public void Default_store_uses_per_user_local_application_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mt5Manager",
            "algo-schedules.json");

        new JsonAlgoScheduleStore().DefaultPath.Should().Be(expected);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"version\":2,\"state\":{\"schedules\":[],\"executions\":[]}}")]
    [InlineData("{\"version\":1,\"state\":{\"schedules\":null,\"executions\":[]}}")]
    public async Task Load_fails_closed_and_preserves_invalid_documents(string content)
    {
        await File.WriteAllTextAsync(StorePath, content);
        var state = await new JsonAlgoScheduleStore(StorePath).LoadAsync();

        state.Should().BeEquivalentTo(AlgoScheduleState.Empty);
        File.Exists(StorePath).Should().BeTrue();
        (await File.ReadAllTextAsync(Directory.EnumerateFiles(root, "algo-schedules.json.corrupt-*").Single()))
            .Should().Be(content);
    }

    [Fact]
    public async Task Save_rejects_invalid_schedule_without_replacing_current_state()
    {
        var store = new JsonAlgoScheduleStore(StorePath);
        var current = State("Asia on", true);
        await store.SaveAsync(current);
        var invalid = current with
        {
            Schedules = [current.Schedules[0] with { TerminalIds = [] }]
        };

        var action = () => store.SaveAsync(invalid);

        await action.Should().ThrowAsync<ArgumentException>();
        (await store.LoadAsync()).Should().BeEquivalentTo(current);
    }

    [Fact]
    public async Task Concurrent_store_instances_leave_one_complete_document()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => new JsonAlgoScheduleStore(StorePath)).ToArray();
        var states = Enumerable.Range(0, 12).Select(index => State($"Schedule {index}", index % 2 == 0)).ToArray();

        await Task.WhenAll(stores.Zip(states).Select(pair => pair.First.SaveAsync(pair.Second)));

        states.Should().ContainEquivalentOf(await stores[0].LoadAsync());
        Directory.EnumerateFiles(root, ".algo-schedules.json.*.tmp").Should().BeEmpty();
    }

    private static AlgoScheduleState State(string name, bool enable)
    {
        var scheduleId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var scheduledAt = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
        var schedule = new AlgoSchedule(
            scheduleId,
            name,
            new TimeOnly(8, 0),
            AlgoScheduleDays.Weekdays,
            enable,
            [terminalId],
            true,
            scheduledAt.AddDays(-1),
            1);
        var execution = new AlgoScheduleExecution(
            Guid.NewGuid(),
            scheduleId,
            1,
            name,
            new DateOnly(2026, 9, 23),
            scheduledAt,
            terminalId,
            "Broker",
            enable,
            1,
            scheduledAt,
            AlgoScheduleExecutionOutcome.Succeeded,
            "changed");
        return new([schedule], [execution]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
