using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Infrastructure.Persistence;
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
            x.ImplementationType == typeof(TelegramBotService) && x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x => x.ServiceType == typeof(TelegramSettingsViewModel) &&
            x.Lifetime == ServiceLifetime.Transient);
        services.Should().ContainSingle(x => x.ServiceType == typeof(TelegramSettingsDialog) &&
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
