using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public enum AlgoOperationSource { Wpf, Telegram, Scheduler }

public sealed record AlgoTradingRequest(Guid TerminalId, bool Enable, AlgoOperationSource Source);

public sealed record AlgoTradingOperationResult(
    Guid TerminalId,
    string TerminalName,
    bool Enable,
    AlgoTradingControlResult Result);

public interface IAlgoTradingService
{
    Task<AlgoTradingOperationResult> SetAsync(
        AlgoTradingRequest request,
        CancellationToken cancellationToken = default);
}
