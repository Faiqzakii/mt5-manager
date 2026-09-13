using System.Net;
using System.Net.Sockets;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Infrastructure.Telegram;

public sealed class HttpPublicIpProvider : IPublicIpProvider
{
    static readonly Uri Endpoint = new("https://api.ipify.org");
    readonly HttpClient client;
    readonly TimeSpan timeout;
    readonly SemaphoreSlim lookupLock = new(1, 1);
    string? cached;

    public HttpPublicIpProvider(HttpClient client) : this(client, TimeSpan.FromSeconds(2)) { }

    internal HttpPublicIpProvider(HttpClient client, TimeSpan timeout)
    {
        this.client = client;
        this.timeout = timeout;
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cached is not null) return cached;
        await lookupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is not null) return cached;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                using var response = await client.GetAsync(Endpoint, timeoutSource.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var value = (await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false)).Trim();
                if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                    return null;
                cached = address.ToString();
                return cached;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            catch (HttpRequestException) { return null; }
        }
        finally { lookupLock.Release(); }
    }
}
