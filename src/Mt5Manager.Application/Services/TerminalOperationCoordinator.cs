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
    string? Message);

public enum CleanupOutcomeStatus { Completed, ForceDeclined, Rejected }

public sealed record CleanupOutcome(
    CleanupOutcomeStatus Status,
    CleanupResult Result,
    string? Message);

/// <summary>
/// Serializes operations per terminal and implements the stop-clean-start workflow.
/// </summary>
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

    public TerminalOperationCoordinator(
        ITerminalRegistry registry,
        ITerminalProcessController processController,
        ITerminalCleanupService cleanupService,
        IAuditLogger auditLogger,
        TimeSpan? stopTimeout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), _stopTimeout, "The stop timeout must be positive.");
    }

    /// <summary>
    /// Validates the request and, for a running terminal, observes a graceful exit before any deletion.
    /// A terminal that does not exit in time is reported as requiring explicit force consent.
    /// </summary>
    public async Task<CleanupPreparation> PrepareCleanupAsync(
        CleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var terminal = await FindTerminalAsync(request.TerminalId, cancellationToken);
        if (terminal is null) return RejectPreparation(request, "The terminal is no longer registered.");
        if (!terminal.DataDirectoryVerified)
            return RejectPreparation(request, "Cleanup requires a verified terminal data directory.");

        var gate = GateFor(terminal.Id);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await _processController.GetStateAsync(terminal, cancellationToken);
            if (state.State == TerminalState.Error)
                return RejectPreparation(request, state.Error ?? "The terminal state could not be determined.");

            if (state.State == TerminalState.Stopped)
                return new CleanupPreparation(CleanupPreparationStatus.Ready, request, false, null);

            var stop = await _processController.StopAsync(terminal, _stopTimeout, force: false, cancellationToken);
            return stop.Outcome switch
            {
                StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped =>
                    new CleanupPreparation(CleanupPreparationStatus.Ready, request, true, null),
                StopOutcome.TimedOut =>
                    new CleanupPreparation(CleanupPreparationStatus.RequiresForceConfirmation, request, true,
                        "The terminal did not close within the timeout. Force termination is required to continue."),
                _ => RejectPreparation(request, stop.Error ?? "The terminal could not be stopped.", wasRunning: true)
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Completes a prepared cleanup. Deletion only happens once the terminal exit is observed, and a
    /// previously running terminal is restarted even when the cleanup itself reported failures.
    /// </summary>
    public async Task<CleanupOutcome> ContinueCleanupAsync(
        CleanupPreparation preparation,
        bool forceApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Status == CleanupPreparationStatus.Rejected)
            return RejectOutcome(preparation.Request, preparation.WasRunning,
                preparation.Message ?? "The cleanup was rejected.");

        var terminal = await FindTerminalAsync(preparation.Request.TerminalId, cancellationToken);
        if (terminal is null)
            return RejectOutcome(preparation.Request, preparation.WasRunning, "The terminal is no longer registered.");
        if (!terminal.DataDirectoryVerified)
            return RejectOutcome(preparation.Request, preparation.WasRunning,
                "Cleanup requires a verified terminal data directory.");

        var gate = GateFor(terminal.Id);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await CompleteAsync(terminal, preparation, forceApproved, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CleanupOutcome> CompleteAsync(
        TerminalRegistration terminal,
        CleanupPreparation preparation,
        bool forceApproved,
        CancellationToken cancellationToken)
    {
        var request = preparation.Request;
        var wasRunning = preparation.WasRunning;
        var shutdownMethod = wasRunning ? ShutdownMethod.Graceful : ShutdownMethod.None;

        if (preparation.Status == CleanupPreparationStatus.RequiresForceConfirmation)
        {
            if (!forceApproved)
                return await DeclineAsync(terminal, request, wasRunning);

            var stop = await _processController.StopAsync(terminal, _stopTimeout, force: true, cancellationToken);
            if (stop.Outcome is not (StopOutcome.ForceTerminated or StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped))
                return await FailAsync(terminal, request, wasRunning, ShutdownMethod.Force,
                    stop.Error ?? "The terminal could not be force terminated.");

            shutdownMethod = ShutdownMethod.Force;
        }

        IReadOnlyList<CleanupCategoryResult> results = [];
        string? cleanupError = null;
        try
        {
            results = await _cleanupService.CleanAsync(terminal, request.Categories, cancellationToken);
        }
        catch (Exception exception)
        {
            // The terminal may already be stopped, so it is restored before the failure is reported.
            cleanupError = exception is OperationCanceledException
                ? "The cleanup was canceled."
                : exception.Message;
        }

        var restarted = false;
        string? restartError = null;
        if (wasRunning)
        {
            // Restoring a previously running terminal must not be abandoned part-way.
            try
            {
                await _processController.StartAsync(terminal, CancellationToken.None);
                restarted = true;
            }
            catch (Exception exception)
            {
                restartError = exception.Message;
            }
        }

        var outcome = cleanupError is null ? AuditOutcome.Completed : AuditOutcome.Rejected;
        await AppendAuditAsync(terminal, request, wasRunning, shutdownMethod, outcome, cleanupError,
            results, restarted, restartError);

        var result = new CleanupResult(wasRunning, restarted, results, restartError);
        return cleanupError is null
            ? new CleanupOutcome(CleanupOutcomeStatus.Completed, result, null)
            : new CleanupOutcome(CleanupOutcomeStatus.Rejected, result, cleanupError);
    }

    private async Task<CleanupOutcome> DeclineAsync(TerminalRegistration terminal, CleanupRequest request, bool wasRunning)
    {
        const string message = "The terminal did not close within the timeout and force termination was declined, so no files were deleted.";
        await AppendAuditAsync(terminal, request, wasRunning, ShutdownMethod.Graceful, AuditOutcome.ForceDeclined,
            message, [], restarted: false, restartError: null);
        return new CleanupOutcome(CleanupOutcomeStatus.ForceDeclined, EmptyResult(wasRunning), message);
    }

    private async Task<CleanupOutcome> FailAsync(
        TerminalRegistration terminal,
        CleanupRequest request,
        bool wasRunning,
        ShutdownMethod shutdownMethod,
        string message)
    {
        await AppendAuditAsync(terminal, request, wasRunning, shutdownMethod, AuditOutcome.Rejected,
            message, [], restarted: false, restartError: null);
        return new CleanupOutcome(CleanupOutcomeStatus.Rejected, EmptyResult(wasRunning), message);
    }

    private Task AppendAuditAsync(
        TerminalRegistration terminal,
        CleanupRequest request,
        bool wasRunning,
        ShutdownMethod shutdownMethod,
        AuditOutcome outcome,
        string? message,
        IReadOnlyList<CleanupCategoryResult> results,
        bool restarted,
        string? restartError) =>
        _auditLogger.AppendAsync(new AuditRecord(
            DateTimeOffset.UtcNow,
            terminal.Id,
            terminal.DisplayName,
            CleanupOperation,
            request.Categories.OrderBy(category => category).ToArray(),
            wasRunning,
            shutdownMethod,
            outcome,
            message,
            results
                .Select(result => new AuditCategoryOutcome(
                    result.Category, result.DeletedFiles, result.DeletedBytes, result.Failures))
                .ToArray(),
            restarted,
            restartError));

    private static CleanupPreparation RejectPreparation(CleanupRequest request, string message, bool wasRunning = false) =>
        new(CleanupPreparationStatus.Rejected, request, wasRunning, message);

    private static CleanupOutcome RejectOutcome(CleanupRequest request, bool wasRunning, string message) =>
        new(CleanupOutcomeStatus.Rejected, EmptyResult(wasRunning), message);

    private static CleanupResult EmptyResult(bool wasRunning) => new(wasRunning, false, [], null);

    private async Task<TerminalRegistration?> FindTerminalAsync(Guid terminalId, CancellationToken cancellationToken)
    {
        var terminals = await _registry.LoadAsync(cancellationToken);
        return terminals.FirstOrDefault(terminal => terminal.Id == terminalId);
    }

    private SemaphoreSlim GateFor(Guid terminalId) =>
        _gates.GetOrAdd(terminalId, static _ => new SemaphoreSlim(1, 1));
}
