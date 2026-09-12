using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed record CleanupRequest(Guid TerminalId, IReadOnlySet<CleanupCategory> Categories);
public enum CleanupPreparationStatus { Ready, RequiresForceConfirmation, Rejected }
public sealed record CleanupPreparation(
    CleanupPreparationStatus Status,
    CleanupRequest Request,
    bool WasRunning,
    string? Message,
    TerminalRegistration TerminalSnapshot,
    Guid ReservationToken);
public enum CleanupOutcomeStatus { Completed, ForceDeclined, Rejected }
public sealed record CleanupOutcome(CleanupOutcomeStatus Status, CleanupResult Result, string? Message);

public sealed class TerminalOperationCoordinator
{
    public const string CleanupOperation = "Cleanup";
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

    private readonly ITerminalRegistry _registry;
    private readonly ITerminalProcessController _processController;
    private readonly ITerminalCleanupService _cleanupService;
    private readonly IAuditLogger _auditLogger;
    private readonly TimeSpan _stopTimeout;
    private readonly ConcurrentDictionary<Guid, GateState> _gates = new();
    private readonly ConcurrentDictionary<Guid, CleanupPreparation> _issuedActive = new();
    private readonly ConcurrentDictionary<Guid, Guid> _reservations = new();
    private readonly byte[] _rejectionKey = RandomNumberGenerator.GetBytes(32);

    internal int GateCount => _gates.Count;

    public TerminalOperationCoordinator(ITerminalRegistry registry, ITerminalProcessController processController,
        ITerminalCleanupService cleanupService, IAuditLogger auditLogger, TimeSpan? stopTimeout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), _stopTimeout, "The stop timeout must be positive.");
    }

    public async Task<CleanupPreparation> PrepareCleanupAsync(CleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownedRequest = new CleanupRequest(request.TerminalId, request.Categories.ToFrozenSet());
        var found = await FindTerminalAsync(ownedRequest.TerminalId, cancellationToken);
        var snapshot = Snapshot(found ?? UnknownTerminal(ownedRequest.TerminalId));
        await using var gate = await AcquireGateAsync(ownedRequest.TerminalId, cancellationToken);

            if (_reservations.ContainsKey(ownedRequest.TerminalId))
                return IssueRejected(ownedRequest, snapshot, "Another operation is already prepared for this terminal.");
            if (found is null) return IssueRejected(ownedRequest, snapshot, "The terminal is no longer registered.");
            if (!found.DataDirectoryVerified)
                return IssueRejected(ownedRequest, snapshot, "Cleanup requires a verified terminal data directory.");

            var token = Guid.NewGuid();
            if (!_reservations.TryAdd(ownedRequest.TerminalId, token))
                return IssueRejected(ownedRequest, snapshot, "Another operation is already prepared for this terminal.");

            try
            {
                var state = await _processController.GetStateAsync(found, cancellationToken);
                if (state.State == TerminalState.Error)
                    return IssueActive(CleanupPreparationStatus.Rejected, ownedRequest, false,
                        state.Error ?? "The terminal state could not be determined.", snapshot, token);
                if (state.State == TerminalState.Stopped)
                    return IssueActive(CleanupPreparationStatus.Ready, ownedRequest, false, null, snapshot, token);

                var stop = await _processController.StopAsync(found, _stopTimeout, false, cancellationToken);
                return stop.Outcome switch
                {
                    StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped =>
                        IssueActive(CleanupPreparationStatus.Ready, ownedRequest, true, null, snapshot, token),
                    StopOutcome.TimedOut => IssueActive(CleanupPreparationStatus.RequiresForceConfirmation, ownedRequest, true,
                        "The terminal did not close within the timeout. Force termination is required to continue.", snapshot, token),
                    _ => IssueActive(CleanupPreparationStatus.Rejected, ownedRequest, true,
                        stop.Error ?? "The terminal could not be stopped.", snapshot, token)
                };
            }
            catch
            {
                _reservations.TryRemove(new KeyValuePair<Guid, Guid>(ownedRequest.TerminalId, token));
                throw;
            }

    }

    public async Task<CleanupOutcome> ContinueCleanupAsync(CleanupPreparation preparation, bool forceApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        GateLease gate;
        try { gate = await AcquireGateAsync(preparation.Request.TerminalId, cancellationToken); }
        catch (OperationCanceledException)
        {
            // Only the exact issued preparation may surrender its reservation. Rejected, stale, or
            // tampered preparations must not unblock a different operation on the same terminal.
            Consume(preparation);
            throw;
        }
        await using var gateLease = gate;

            if (IsIssuedRejection(preparation))
                return await RejectAndAuditAsync(preparation,
                    preparation.Message ?? "The cleanup was rejected.");
            if (!Consume(preparation))
                return await RejectAndAuditAsync(preparation, "This cleanup preparation is no longer active.");

            try
            {
                if (preparation.Status == CleanupPreparationStatus.Rejected)
                    return await RejectAndAuditAsync(preparation, preparation.Message ?? "The cleanup was rejected.");

                var terminal = await FindTerminalAsync(preparation.Request.TerminalId, cancellationToken);
                if (terminal is null)
                    return await RejectAndAuditAsync(preparation, "The terminal is no longer registered.");
                if (!terminal.DataDirectoryVerified)
                    return await RejectAndAuditAsync(preparation, "Cleanup requires a verified terminal data directory.");
                if (!MatchesSnapshot(terminal, preparation.TerminalSnapshot))
                    return await RejectAndAuditAsync(preparation,
                        "The terminal registration changed after the cleanup preview, so no files were deleted.");
                return await CompleteAsync(terminal, preparation, forceApproved, cancellationToken);
            }
            finally
            {
                Release(preparation);
            }

    }

    public async Task CancelPreparationAsync(CleanupPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var terminalId = preparation.Request.TerminalId;
        await using var gate = await AcquireGateAsync(terminalId, cancellationToken);

            // Only the exact issued preparation may surrender its reservation. Rejected, stale, or
            // tampered preparations must not unblock a different operation on the same terminal.
            Consume(preparation);

    }

    private async Task<CleanupOutcome> CompleteAsync(TerminalRegistration terminal, CleanupPreparation preparation,
        bool forceApproved, CancellationToken cancellationToken)
    {
        var request = preparation.Request;
        var wasRunning = preparation.WasRunning;
        var shutdown = wasRunning ? ShutdownMethod.Graceful : ShutdownMethod.None;
        IReadOnlyList<CleanupCategoryResult> results = [];
        string? cleanupError = null;
        try
        {
            if (preparation.Status == CleanupPreparationStatus.RequiresForceConfirmation)
            {
                if (!forceApproved) return await DeclineAsync(terminal, request, wasRunning);
                var stop = await _processController.StopAsync(terminal, _stopTimeout, true, cancellationToken);
                if (stop.Outcome is not (StopOutcome.ForceTerminated or StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped))
                    return await FailAsync(terminal, request, wasRunning, ShutdownMethod.Force,
                        stop.Error ?? "The terminal could not be force terminated.");
                shutdown = ShutdownMethod.Force;
            }
            else
            {
                // The preparation can be minutes old: never delete the data of a terminal that is running again.
                var state = await _processController.GetStateAsync(terminal, cancellationToken);
                if (state.State == TerminalState.Error)
                    return await FailAsync(terminal, request, wasRunning, ShutdownMethod.None,
                        state.Error ?? "The terminal state could not be determined, so no files were deleted.");
                if (state.State != TerminalState.Stopped)
                {
                    // The terminal has to be running again even when this operation is abandoned half way
                    // through, so the intent is recorded before the stop is attempted.
                    wasRunning = true;
                    shutdown = ShutdownMethod.Graceful;
                    var stop = await _processController.StopAsync(terminal, _stopTimeout, false, cancellationToken);
                    if (stop.Outcome is not (StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped))
                        return await FailAsync(terminal, request, true, ShutdownMethod.Graceful,
                            stop.Error ?? "The terminal is running again and could not be stopped, so no files were deleted.");
                }
            }

            results = await _cleanupService.CleanAsync(terminal, request.Categories, cancellationToken);
            // Every requested category was refused and nothing was deleted: report why instead of hiding
            // the refusal behind a "Completed: 0 deleted" outcome.
            if (results.Count > 0 && results.All(category => category.DeletedFiles == 0 && category.Failures.Count > 0))
                cleanupError = results[0].Failures[0].Error;
        }
        catch (Exception exception)
        {
            cleanupError = exception is OperationCanceledException ? "The cleanup was canceled." : exception.Message;
        }

        var restarted = false;
        string? restartError = null;
        if (wasRunning)
        {
            try
            {
                // A terminal that was running is left running, but never started twice: an abandoned stop
                // can leave it alive, and the state is the only trustworthy answer.
                var state = await _processController.GetStateAsync(terminal, CancellationToken.None);
                if (state.State == TerminalState.Stopped)
                {
                    await _processController.StartAsync(terminal, CancellationToken.None);
                    restarted = true;
                }
                else if (state.State == TerminalState.Error)
                    restartError = state.Error ?? "The terminal state could not be determined, so it was not started again.";
            }
            catch (Exception exception) { restartError = exception.Message; }
        }

        var auditOutcome = cleanupError is null ? AuditOutcome.Completed : AuditOutcome.Rejected;
        var auditError = await TryAppendAuditAsync(terminal, request, wasRunning, shutdown, auditOutcome, cleanupError,
            results, restarted, restartError);
        var result = new CleanupResult(wasRunning, restarted, results, restartError);
        return cleanupError is not null
            ? new CleanupOutcome(CleanupOutcomeStatus.Rejected, result, cleanupError)
            : new CleanupOutcome(CleanupOutcomeStatus.Completed, result, auditError);
    }

    private async Task<CleanupOutcome> RejectAndAuditAsync(CleanupPreparation preparation, string message)
    {
        await TryAppendAuditAsync(preparation.TerminalSnapshot, preparation.Request, preparation.WasRunning,
            ShutdownMethod.None, AuditOutcome.Rejected, message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.Rejected, EmptyResult(preparation.WasRunning), message);
    }

    private async Task<CleanupOutcome> DeclineAsync(TerminalRegistration terminal, CleanupRequest request, bool wasRunning)
    {
        const string message = "The terminal did not close within the timeout and force termination was declined, so no files were deleted.";
        await TryAppendAuditAsync(terminal, request, wasRunning, ShutdownMethod.Graceful, AuditOutcome.ForceDeclined,
            message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.ForceDeclined, EmptyResult(wasRunning), message);
    }

    private async Task<CleanupOutcome> FailAsync(TerminalRegistration terminal, CleanupRequest request,
        bool wasRunning, ShutdownMethod shutdownMethod, string message)
    {
        await TryAppendAuditAsync(terminal, request, wasRunning, shutdownMethod, AuditOutcome.Rejected,
            message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.Rejected, EmptyResult(wasRunning), message);
    }

    private async Task<string?> TryAppendAuditAsync(TerminalRegistration terminal, CleanupRequest request, bool wasRunning,
        ShutdownMethod shutdownMethod, AuditOutcome outcome, string? message,
        IReadOnlyList<CleanupCategoryResult> results, bool restarted, string? restartError)
    {
        try
        {
            await _auditLogger.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, terminal.Id, terminal.DisplayName,
                CleanupOperation, request.Categories.OrderBy(category => category).ToArray(), wasRunning,
                shutdownMethod, outcome, message,
                results.Select(result => new AuditCategoryOutcome(result.Category, result.DeletedFiles,
                    result.DeletedBytes, result.Failures)).ToArray(), restarted, restartError));
            return null;
        }
        catch (Exception exception)
        {
            // A completed cleanup must never be reported as failed just because its audit line
            // could not be written; the error travels with the outcome instead.
            return $"The audit log could not be written: {exception.Message}";
        }
    }

    private static bool MatchesSnapshot(TerminalRegistration terminal, TerminalRegistration snapshot) =>
        string.Equals(Path.TrimEndingDirectorySeparator(terminal.ExecutablePath),
            Path.TrimEndingDirectorySeparator(snapshot.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.TrimEndingDirectorySeparator(terminal.DataDirectory),
            Path.TrimEndingDirectorySeparator(snapshot.DataDirectory), StringComparison.OrdinalIgnoreCase);

    private void Discard(Guid reservationToken)
    {
        if (_issuedActive.TryGetValue(reservationToken, out var active))
            _issuedActive.TryRemove(new KeyValuePair<Guid, CleanupPreparation>(reservationToken, active));
    }

    private bool Consume(CleanupPreparation preparation)
    {
        if (!_issuedActive.TryGetValue(preparation.ReservationToken, out var issued) || issued != preparation)
            return false;
        if (!_reservations.TryRemove(new KeyValuePair<Guid, Guid>(preparation.Request.TerminalId, preparation.ReservationToken)))
            return false;
        return _issuedActive.TryRemove(new KeyValuePair<Guid, CleanupPreparation>(preparation.ReservationToken, issued));
    }
    private void Release(CleanupPreparation preparation) =>
        _reservations.TryRemove(new KeyValuePair<Guid, Guid>(preparation.Request.TerminalId, preparation.ReservationToken));
    private CleanupPreparation IssueActive(CleanupPreparationStatus status, CleanupRequest request, bool wasRunning,
        string? message, TerminalRegistration terminal, Guid token)
    {
        var preparation = Prepared(status, request, wasRunning, message, terminal, token);
        _issuedActive.TryAdd(token, preparation);
        return preparation;
    }
    private CleanupPreparation IssueRejected(CleanupRequest request, TerminalRegistration terminal, string message)
    {
        var unsigned = Prepared(CleanupPreparationStatus.Rejected, request, false, message, terminal, Guid.Empty);
        return unsigned with { ReservationToken = SignRejection(unsigned) };
    }
    private bool IsIssuedRejection(CleanupPreparation preparation) =>
        preparation.Status == CleanupPreparationStatus.Rejected &&
        preparation.ReservationToken != Guid.Empty &&
        CryptographicOperations.FixedTimeEquals(
            preparation.ReservationToken.ToByteArray(), SignRejection(preparation with { ReservationToken = Guid.Empty }).ToByteArray());
    private static CleanupPreparation Prepared(CleanupPreparationStatus status, CleanupRequest request, bool wasRunning,
        string? message, TerminalRegistration terminal, Guid token) =>
        new(status, request, wasRunning, message, terminal, token);
    private static CleanupResult EmptyResult(bool wasRunning) => new(wasRunning, false, [], null);
    private async Task<TerminalRegistration?> FindTerminalAsync(Guid id, CancellationToken token) =>
        (await _registry.LoadAsync(token)).FirstOrDefault(terminal => terminal.Id == id);
    private async ValueTask<GateLease> AcquireGateAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = _gates.GetOrAdd(id, static _ => new GateState());
            lock (state)
            {
                if (state.Retired) continue;
                state.ReferenceCount++;
            }

            try
            {
                await state.Semaphore.WaitAsync(cancellationToken);
                return new GateLease(this, id, state);
            }
            catch
            {
                ReleaseGate(id, state, acquired: false);
                throw;
            }
        }
    }

    private void ReleaseGate(Guid id, GateState state, bool acquired)
    {
        if (acquired) state.Semaphore.Release();
        lock (state)
        {
            state.ReferenceCount--;
            if (state.ReferenceCount != 0) return;
            state.Retired = true;
            _gates.TryRemove(new KeyValuePair<Guid, GateState>(id, state));
        }
        state.Semaphore.Dispose();
    }

    private Guid SignRejection(CleanupPreparation preparation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            preparation.Status,
            preparation.Request.TerminalId,
            Categories = preparation.Request.Categories.OrderBy(static category => category),
            preparation.WasRunning,
            preparation.Message,
            Terminal = preparation.TerminalSnapshot
        });
        using var hmac = new HMACSHA256(_rejectionKey);
        return new Guid(hmac.ComputeHash(payload).AsSpan(0, 16));
    }
    private sealed class GateState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class GateLease(TerminalOperationCoordinator owner, Guid id, GateState state) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.ReleaseGate(id, state, acquired: true);
            return ValueTask.CompletedTask;
        }
    }
    private static TerminalRegistration Snapshot(TerminalRegistration terminal) =>
        terminal with { Arguments = terminal.Arguments.ToImmutableArray() };
    private static TerminalRegistration UnknownTerminal(Guid id) =>
        new(id, "Unknown terminal", string.Empty, string.Empty, string.Empty, [], DiscoverySource.Manual, false);
}
