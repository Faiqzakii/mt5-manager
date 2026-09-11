using System.Collections.Frozen;
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, CleanupPreparation> _issuedRejections = new();
    private readonly ConcurrentDictionary<Guid, CleanupPreparation> _issuedActive = new();
    private readonly ConcurrentDictionary<Guid, Guid> _reservations = new();

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
        var gate = GateFor(ownedRequest.TerminalId);
        await gate.WaitAsync(cancellationToken);
        try
        {
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
        finally { gate.Release(); }
    }

    public async Task<CleanupOutcome> ContinueCleanupAsync(CleanupPreparation preparation, bool forceApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var gate = GateFor(preparation.Request.TerminalId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryConsumeIssuedRejection(preparation))
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
                return await CompleteAsync(terminal, preparation, forceApproved, cancellationToken);
            }
            finally
            {
                Release(preparation);
            }
        }
        finally { gate.Release(); }
    }

    public async Task CancelPreparationAsync(CleanupPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var gate = GateFor(preparation.Request.TerminalId);
        await gate.WaitAsync(cancellationToken);
        try { Consume(preparation); }
        finally { gate.Release(); }
    }

    private async Task<CleanupOutcome> CompleteAsync(TerminalRegistration terminal, CleanupPreparation preparation,
        bool forceApproved, CancellationToken cancellationToken)
    {
        var request = preparation.Request;
        var shutdown = preparation.WasRunning ? ShutdownMethod.Graceful : ShutdownMethod.None;
        if (preparation.Status == CleanupPreparationStatus.RequiresForceConfirmation)
        {
            if (!forceApproved) return await DeclineAsync(terminal, request, preparation.WasRunning);
            var stop = await _processController.StopAsync(terminal, _stopTimeout, true, cancellationToken);
            if (stop.Outcome is not (StopOutcome.ForceTerminated or StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped))
                return await FailAsync(terminal, request, preparation.WasRunning, ShutdownMethod.Force,
                    stop.Error ?? "The terminal could not be force terminated.");
            shutdown = ShutdownMethod.Force;
        }

        IReadOnlyList<CleanupCategoryResult> results = [];
        string? cleanupError = null;
        try { results = await _cleanupService.CleanAsync(terminal, request.Categories, cancellationToken); }
        catch (Exception exception)
        {
            cleanupError = exception is OperationCanceledException ? "The cleanup was canceled." : exception.Message;
        }

        var restarted = false;
        string? restartError = null;
        if (preparation.WasRunning)
        {
            try { await _processController.StartAsync(terminal, CancellationToken.None); restarted = true; }
            catch (Exception exception) { restartError = exception.Message; }
        }

        var auditOutcome = cleanupError is null ? AuditOutcome.Completed : AuditOutcome.Rejected;
        await AppendAuditAsync(terminal, request, preparation.WasRunning, shutdown, auditOutcome, cleanupError,
            results, restarted, restartError);
        var result = new CleanupResult(preparation.WasRunning, restarted, results, restartError);
        return cleanupError is null
            ? new CleanupOutcome(CleanupOutcomeStatus.Completed, result, null)
            : new CleanupOutcome(CleanupOutcomeStatus.Rejected, result, cleanupError);
    }

    private async Task<CleanupOutcome> RejectAndAuditAsync(CleanupPreparation preparation, string message)
    {
        await AppendAuditAsync(preparation.TerminalSnapshot, preparation.Request, preparation.WasRunning,
            ShutdownMethod.None, AuditOutcome.Rejected, message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.Rejected, EmptyResult(preparation.WasRunning), message);
    }

    private async Task<CleanupOutcome> DeclineAsync(TerminalRegistration terminal, CleanupRequest request, bool wasRunning)
    {
        const string message = "The terminal did not close within the timeout and force termination was declined, so no files were deleted.";
        await AppendAuditAsync(terminal, request, wasRunning, ShutdownMethod.Graceful, AuditOutcome.ForceDeclined,
            message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.ForceDeclined, EmptyResult(wasRunning), message);
    }

    private async Task<CleanupOutcome> FailAsync(TerminalRegistration terminal, CleanupRequest request,
        bool wasRunning, ShutdownMethod shutdownMethod, string message)
    {
        await AppendAuditAsync(terminal, request, wasRunning, shutdownMethod, AuditOutcome.Rejected,
            message, [], false, null);
        return new CleanupOutcome(CleanupOutcomeStatus.Rejected, EmptyResult(wasRunning), message);
    }

    private Task AppendAuditAsync(TerminalRegistration terminal, CleanupRequest request, bool wasRunning,
        ShutdownMethod shutdownMethod, AuditOutcome outcome, string? message,
        IReadOnlyList<CleanupCategoryResult> results, bool restarted, string? restartError) =>
        _auditLogger.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, terminal.Id, terminal.DisplayName,
            CleanupOperation, request.Categories.OrderBy(category => category).ToArray(), wasRunning,
            shutdownMethod, outcome, message,
            results.Select(result => new AuditCategoryOutcome(result.Category, result.DeletedFiles,
                result.DeletedBytes, result.Failures)).ToArray(), restarted, restartError));

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
        var preparation = Prepared(CleanupPreparationStatus.Rejected, request, false, message, terminal, Guid.NewGuid());
        _issuedRejections.TryAdd(preparation.ReservationToken, preparation);
        return preparation;
    }
    private bool TryConsumeIssuedRejection(CleanupPreparation preparation) =>
        _issuedRejections.TryGetValue(preparation.ReservationToken, out var issued) &&
        issued == preparation &&
        _issuedRejections.TryRemove(new KeyValuePair<Guid, CleanupPreparation>(preparation.ReservationToken, issued));
    private static CleanupPreparation Prepared(CleanupPreparationStatus status, CleanupRequest request, bool wasRunning,
        string? message, TerminalRegistration terminal, Guid token) =>
        new(status, request, wasRunning, message, terminal, token);
    private static CleanupResult EmptyResult(bool wasRunning) => new(wasRunning, false, [], null);
    private async Task<TerminalRegistration?> FindTerminalAsync(Guid id, CancellationToken token) =>
        (await _registry.LoadAsync(token)).FirstOrDefault(terminal => terminal.Id == id);
    private SemaphoreSlim GateFor(Guid id) => _gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
    private static TerminalRegistration Snapshot(TerminalRegistration terminal) =>
        terminal with { Arguments = terminal.Arguments.ToArray() };
    private static TerminalRegistration UnknownTerminal(Guid id) =>
        new(id, "Unknown terminal", string.Empty, string.Empty, string.Empty, [], DiscoverySource.Manual, false);
}
