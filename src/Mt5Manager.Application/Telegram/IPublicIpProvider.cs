namespace Mt5Manager.Application.Telegram;

public interface IPublicIpProvider
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
}
