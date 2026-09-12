using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed class AlgoTradingService : IAlgoTradingService
{
    private const string UnknownTerminalName = "Unknown terminal";
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

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
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
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

            try
            {
                var result = await controller.SetAsync(terminal, request.Enable, cancellationToken);
                await AppendAuditAsync(request, terminal.DisplayName, result, cancellationToken);
                return new(request.TerminalId, terminal.DisplayName, request.Enable, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await AppendAuditAsync(request, terminal.DisplayName,
                    new AlgoTradingControlResult(false, exception.Message, null), cancellationToken);
                throw;
            }
        }
        finally
        {
            OperationGate.Release();
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
