using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Application.Services;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Infrastructure.Bridge;
using Mt5Manager.Infrastructure.Deployment;
using Mt5Manager.Infrastructure.Security;
using Mt5Manager.Infrastructure.Telegram;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;

namespace Mt5Manager.Wpf.Tests;

public sealed class AppCompositionTests
{
    [Fact]
    public void ConfigureServices_RegistersTelegramCompositionWithExpectedLifetimes()
    {
        var services = new ServiceCollection();

        App.ConfigureServices(services, new HttpClient());

        services.Should().ContainSingle(x => x.ServiceType == typeof(ITelegramSettingsStore) &&
            x.ImplementationType == typeof(JsonTelegramSettingsStore) && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(ISecretProtector) &&
            x.ImplementationType == typeof(WindowsUserSecretProtector) && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(ITelegramBotApi) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(ITelegramBotService) &&
            x.ImplementationFactory != null && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(TelegramSettingsViewModel) &&
            x.Lifetime == ServiceLifetime.Transient);
        services.Should().ContainSingle(x => x.ServiceType == typeof(TelegramSettingsDialog) &&
            x.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void ConfigureServices_RegistersTheBridgeInstaller()
    {
        var services = new ServiceCollection();

        App.ConfigureServices(services, new HttpClient());

        services.Should().ContainSingle(x => x.ServiceType == typeof(IBridgeInstaller) &&
            x.ImplementationType == typeof(Mt5BridgeInstaller) && x.Lifetime == ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MainViewModel>().Should().NotBeNull();
    }

    [Fact]
    public void ConfigureServices_RegistersThePackageInstaller()
    {
        var services=new ServiceCollection();
        App.ConfigureServices(services,new HttpClient());
        services.Should().ContainSingle(x=>x.ServiceType==typeof(IMt5PackageInstaller)&&x.ImplementationType==typeof(Mt5PackageInstaller)&&x.Lifetime==ServiceLifetime.Singleton);
    }
    [Fact]
    public void ConfigureServices_RegistersRecurringAlgoScheduler()
    {
        var services = new ServiceCollection();

        App.ConfigureServices(services, new HttpClient());

        services.Should().ContainSingle(x => x.ServiceType == typeof(IAlgoScheduleStore) &&
            x.ImplementationType == typeof(JsonAlgoScheduleStore) && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(IAlgoScheduleFailureNotifier) &&
            x.ImplementationType == typeof(TelegramAlgoScheduleFailureNotifier) && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(IAlgoScheduler) &&
            x.ImplementationFactory != null && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(AlgoScheduleViewModel) &&
            x.Lifetime == ServiceLifetime.Transient);
        services.Should().ContainSingle(x => x.ServiceType == typeof(AlgoScheduleDialog) &&
            x.Lifetime == ServiceLifetime.Transient);
    }


    [Fact]
    public async Task ApplicationLifetime_CancelsOperationsBeforeStoppingBot()
    {
        var bot = new RecordingBotService();
        var lifetime = new TelegramApplicationLifetime(bot, TimeSpan.FromSeconds(1));

        await lifetime.StartAsync();
        await lifetime.StopAsync();

        bot.Events.Should().Equal("start", "stop-cancelled");
    }

    [Fact]
    public async Task ApplicationLifetime_StopDoesNotWaitForeverForNonCooperativeInnerService()
    {
        var bot = new NonCooperativeBotService();
        var lifetime = new TelegramApplicationLifetime(bot, TimeSpan.FromMilliseconds(25));

        await lifetime.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));

        bot.StopStarted.Should().BeTrue();
    }

    [Fact]
    public async Task BoundedBotService_DisposeDoesNotWaitForeverForNonCooperativeInnerService()
    {
        var inner = new NonCooperativeBotService();
        var service = new BoundedTelegramBotService(inner, TimeSpan.FromMilliseconds(25));

        var disposal = service.DisposeAsync().AsTask();

        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        inner.DisposeStarted.Should().BeTrue();
    }

    sealed class NonCooperativeBotService : ITelegramBotService
    {
        public bool StopStarted { get; private set; }
        public bool DisposeStarted { get; private set; }
        public TelegramBotState State => TelegramBotState.Stopped;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted = true;
            return Task.Delay(Timeout.InfiniteTimeSpan);
        }
        public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeStarted = true;
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan));
        }
    }

    sealed class RecordingBotService : ITelegramBotService
    {
        CancellationToken applicationToken;
        public List<string> Events { get; } = [];
        public TelegramBotState State => TelegramBotState.Stopped;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            applicationToken = cancellationToken;
            Events.Add("start");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Events.Add(applicationToken.IsCancellationRequested ? "stop-cancelled" : "stop-active");
            return Task.CompletedTask;
        }
        public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
