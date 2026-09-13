using System.Net;
using FluentAssertions;
using Mt5Manager.Infrastructure.Telegram;

namespace Mt5Manager.Infrastructure.Tests.Telegram;

public sealed class HttpPublicIpProviderTests
{
    [Fact]
    public async Task Valid_IPv4_is_cached_for_process_lifetime()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(" 203.0.113.7\n") });
        var sut = new HttpPublicIpProvider(new HttpClient(handler));

        (await sut.GetAsync()).Should().Be("203.0.113.7");
        (await sut.GetAsync()).Should().Be("203.0.113.7");
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("not-an-ip")]
    public async Task Non_IPv4_response_returns_null(string response)
    {
        var sut = new HttpPublicIpProvider(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) })));
        (await sut.GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Http_and_transport_failures_return_null_without_throwing()
    {
        var http = new HttpPublicIpProvider(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))));
        var transport = new HttpPublicIpProvider(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))));
        (await http.GetAsync()).Should().BeNull();
        (await transport.GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Lookup_timeout_is_bounded_and_returns_null()
    {
        var sut = new HttpPublicIpProvider(new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new HttpResponseMessage(HttpStatusCode.OK); })), TimeSpan.FromMilliseconds(25));
        var lookup = sut.GetAsync();
        (await lookup.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeNull();
    }

    [Fact]
    public async Task Failed_lookup_is_not_cached_so_next_attempt_retries()
    {
        var failing = true;
        var handler = new Handler(_ => failing
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.7") });
        var sut = new HttpPublicIpProvider(new HttpClient(handler));

        (await sut.GetAsync()).Should().BeNull();
        failing = false;
        (await sut.GetAsync()).Should().Be("203.0.113.7");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_without_issuing_request()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.7") });
        var sut = new HttpPublicIpProvider(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Caller_cancellation_mid_lookup_throws_instead_of_returning_null()
    {
        using var cts = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var handler = new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                requestStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.7") };
        });
        var sut = new HttpPublicIpProvider(new HttpClient(handler), TimeSpan.FromSeconds(30));

        var lookup = sut.GetAsync(cts.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await FluentActions.Awaiting(() => lookup).Should().ThrowAsync<OperationCanceledException>();
        (await sut.GetAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("203.0.113.7");
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
        public int Calls { get; private set; }
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : this((request, _) => Task.FromResult(response(request))) { }
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => this.response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; request.RequestUri!.Scheme.Should().Be(Uri.UriSchemeHttps); return response(request, cancellationToken); }
    }
}
