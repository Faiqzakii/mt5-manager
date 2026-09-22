using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Persistence;

public sealed class JsonAlgoScheduleStore : IAlgoScheduleStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;

    public JsonAlgoScheduleStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mt5Manager", "algo-schedules.json");

    public string DefaultPath => path;

    public async Task<AlgoScheduleState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return AlgoScheduleState.Empty;
            }

            ScheduleDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<ScheduleDocument>(content, Options);
            }
            catch (JsonException)
            {
                await PreserveUnreadableFileAsync(content, cancellationToken).ConfigureAwait(false);
                return AlgoScheduleState.Empty;
            }

            if (document is null || document.Version != CurrentVersion || !IsValid(document.State))
            {
                await PreserveUnreadableFileAsync(content, cancellationToken).ConfigureAwait(false);
                return AlgoScheduleState.Empty;
            }

            return document.State;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AlgoScheduleState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValid(state)) throw new ArgumentException("Algo scheduler state must be valid.", nameof(state));

        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, new ScheduleDocument(CurrentVersion, state), Options, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (FileNotFoundException) { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool IsValid(AlgoScheduleState? state)
    {
        if (state?.Schedules is null || state.Executions is null) return false;
        if (state.Schedules.Select(item => item.Id).Distinct().Count() != state.Schedules.Count) return false;
        if (state.Executions.Select(item => item.Id).Distinct().Count() != state.Executions.Count) return false;
        return state.Schedules.All(IsValid) && state.Executions.All(IsValid);
    }

    internal static bool IsValid(AlgoSchedule? schedule) => schedule is not null &&
        schedule.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(schedule.Name) &&
        schedule.Revision > 0 &&
        schedule.EffectiveFrom != default &&
        schedule.Days != AlgoScheduleDays.None &&
        (schedule.Days & ~AlgoScheduleDays.EveryDay) == 0 &&
        schedule.TerminalIds is not null &&
        schedule.TerminalIds.Count > 0 &&
        schedule.TerminalIds.All(id => id != Guid.Empty) &&
        schedule.TerminalIds.Distinct().Count() == schedule.TerminalIds.Count;

    internal static bool IsValid(AlgoScheduleExecution? execution) => execution is not null &&
        execution.Id != Guid.Empty &&
        execution.ScheduleId != Guid.Empty &&
        execution.ScheduleRevision > 0 &&
        !string.IsNullOrWhiteSpace(execution.ScheduleName) &&
        execution.OccurrenceDate != default &&
        execution.ScheduledAt != default &&
        execution.TerminalId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(execution.TerminalName) &&
        execution.Attempt is 1 or 2 &&
        execution.AttemptedAt != default &&
        Enum.IsDefined(execution.Outcome) &&
        !string.IsNullOrWhiteSpace(execution.Message) &&
        (!execution.TelegramNotificationSent || execution.TelegramNotificationAttempted) &&
        (!execution.TelegramNotificationAttempted || !string.IsNullOrWhiteSpace(execution.TelegramNotificationMessage));

    private SemaphoreSlim GetGate() => PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));

    private async Task PreserveUnreadableFileAsync(byte[] content, CancellationToken cancellationToken)
    {
        var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
        try { await File.WriteAllBytesAsync(backup, content, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public sealed record ScheduleDocument(int Version, AlgoScheduleState State);
}
