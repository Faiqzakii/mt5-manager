using System.Collections.Concurrent;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed class AlgoTradingService : IAlgoTradingService
{
    private const string UnknownTerminalName = "Unknown terminal";
    private const string SafeFailureMessage = "The operation could not be completed because of a local error.";
    private static readonly ConcurrentDictionary<Guid, GateState> OperationGates = new();
    internal static int OperationGateCount => OperationGates.Count;

    private readonly ITerminalRegistry registry;
    private readonly ITerminalAlgoTradingController controller;
    private readonly IAuditLogger auditLogger;

    public AlgoTradingService(
        ITerminalRegistry registry,
        ITerminalAlgoTradingController controller,
        IAuditLogger auditLogger)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
    }

    public async Task<AlgoTradingOperationResult> SetAsync(
        AlgoTradingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var operationGate = await AcquireGateAsync(request.TerminalId, cancellationToken);
            var terminals = await registry.LoadAsync(cancellationToken);
            var terminal = terminals.FirstOrDefault(candidate => candidate.Id == request.TerminalId);
            if (terminal is null)
            {
                var result = new AlgoTradingControlResult(
                    false,
                    $"Terminal {request.TerminalId} was not found in the registry.",
                    null);
                await AppendAuditAsync(request, UnknownTerminalName, result, cancellationToken);
                return new(request.TerminalId, UnknownTerminalName, request.Enable, result);
            }

            AlgoTradingControlResult controlResult;
            try
            {
                controlResult = await controller.SetAsync(terminal, request.Enable, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await AppendAuditAsync(request, terminal.DisplayName,
                    new AlgoTradingControlResult(false, SafeFailureMessage, null), cancellationToken);
                throw;
            }

            try
            {
                await AppendAuditAsync(request, terminal.DisplayName, controlResult, cancellationToken);
            }
            catch when (controlResult.Success)
            {
                // The controller already changed external state. Preserve that success so callers do not
                // retry a non-idempotent operation merely because local audit persistence failed.
            }
            return new(request.TerminalId, terminal.DisplayName, request.Enable, controlResult);
    }

    private static async ValueTask<GateLease> AcquireGateAsync(
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = OperationGates.GetOrAdd(terminalId, static _ => new GateState());
            lock (state)
            {
                if (state.Retired)
                    continue;

                state.ReferenceCount++;
            }

            try
            {
                await state.Semaphore.WaitAsync(cancellationToken);
                return new GateLease(terminalId, state);
            }
            catch
            {
                ReleaseGate(terminalId, state, acquired: false);
                throw;
            }
        }
    }

    private static void ReleaseGate(Guid terminalId, GateState state, bool acquired)
    {
        if (acquired)
            state.Semaphore.Release();

        lock (state)
        {
            state.ReferenceCount--;
            if (state.ReferenceCount != 0)
                return;

            state.Retired = true;
            OperationGates.TryRemove(new KeyValuePair<Guid, GateState>(terminalId, state));
        }

        state.Semaphore.Dispose();
    }

    private sealed class GateState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class GateLease(Guid terminalId, GateState state) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                ReleaseGate(terminalId, state, acquired: true);

            return ValueTask.CompletedTask;
        }
    }

    private Task AppendAuditAsync(
        AlgoTradingRequest request,
        string terminalName,
        AlgoTradingControlResult result,
        CancellationToken cancellationToken) =>
        auditLogger.AppendAsync(new AuditRecord(
            DateTimeOffset.UtcNow,
            request.TerminalId,
            terminalName,
            request.Enable ? "Enable Algo" : "Disable Algo",
            [],
            false,
            ShutdownMethod.None,
            result.Success ? AuditOutcome.Completed : AuditOutcome.Rejected,
            result.Message,
            [],
            false,
            null,
            request.Source), cancellationToken);
}
