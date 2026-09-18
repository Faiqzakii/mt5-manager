using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Infrastructure.Discovery;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Infrastructure.Bridge;
using Mt5Manager.Infrastructure.Processes;
using Mt5Manager.Infrastructure.Security;
using Mt5Manager.Infrastructure.Storage;
using Mt5Manager.Infrastructure.Telegram;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;

namespace Mt5Manager.Wpf;

public partial class App : System.Windows.Application
{
    static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    ServiceProvider? provider;
    HttpClient? httpClient;
    TelegramApplicationLifetime? telegramLifetime;
    Mutex? singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = new Mutex(initiallyOwned: true, @"Local\Mt5Manager.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("MT5 Manager is already running in this session.", "MT5 Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }

        httpClient = new HttpClient();
        var services = new ServiceCollection();
        ConfigureServices(services, httpClient);
        provider = services.BuildServiceProvider();
        telegramLifetime = provider.GetRequiredService<TelegramApplicationLifetime>();
        try
        {
            await telegramLifetime.StartAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show("Telegram could not be started. Review the saved Telegram settings.\n\n" +
                exception.Message, "Telegram Bot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        provider.GetRequiredService<MainWindow>().Show();
    }

    internal static void ConfigureServices(IServiceCollection services, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(httpClient);

        services.AddSingleton<ITerminalRegistry, JsonTerminalRegistry>();
        services.AddSingleton<ITerminalProcessController, WindowsTerminalProcessController>();
        services.AddSingleton<IAuditLogger, JsonLinesAuditLogger>();
        services.AddSingleton<ICleanupTargetResolver, CleanupTargetResolver>();
        services.AddSingleton<ITerminalStorageInspector, TerminalStorageInspector>();
        services.AddSingleton<ITerminalCleanupService, TerminalCleanupService>();
        services.AddSingleton<ITerminalRuntimeInspector, Mt5Manager.Infrastructure.Runtime.Mt5RuntimeSnapshotReader>();
        services.AddSingleton<ITerminalAlgoTradingController, WindowsTerminalAlgoTradingController>();
        services.AddSingleton<IBridgeInstaller, Mt5BridgeInstaller>();
        services.AddSingleton<IAlgoTradingService, AlgoTradingService>();
        services.AddSingleton<ITerminalDiscoverySource, ProcessDiscoverySource>();
        services.AddSingleton<ITerminalDiscoverySource, ShortcutDiscoverySource>();
        services.AddSingleton<ITerminalDiscoverySource, StandardLocationDiscoverySource>();
        services.AddSingleton<IMt5DataDirectoryResolver, Mt5DataDirectoryResolver>();
        services.AddSingleton<ITerminalDiscovery, TerminalDiscovery>();
        services.AddSingleton<TerminalOperationCoordinator>();

        services.AddSingleton<ITelegramSettingsStore, JsonTelegramSettingsStore>();
        services.AddSingleton<ISecretProtector, WindowsUserSecretProtector>();
        services.AddSingleton(httpClient);
        services.AddSingleton<ITelegramBotApi>(sp => new TelegramBotApiClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IPublicIpProvider>(sp => new HttpPublicIpProvider(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDelay, SystemDelay>();
        services.AddSingleton<ITelegramBotService>(sp => new BoundedTelegramBotService(
            new TelegramBotService(sp.GetRequiredService<ITelegramSettingsStore>(),
                sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<ITelegramBotApi>(),
                sp.GetRequiredService<ITerminalRegistry>(), sp.GetRequiredService<ITerminalRuntimeInspector>(),
                sp.GetRequiredService<IAlgoTradingService>(), sp.GetRequiredService<IPublicIpProvider>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IDelay>()), ShutdownTimeout));
        services.AddSingleton(sp => new TelegramApplicationLifetime(
            sp.GetRequiredService<ITelegramBotService>(), ShutdownTimeout));
        services.AddTransient<TelegramSettingsViewModel>();
        services.AddTransient<TelegramSettingsDialog>();

        services.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<ITerminalDiscovery>(), sp.GetRequiredService<ITerminalRegistry>(),
            sp.GetRequiredService<ITerminalProcessController>(), sp.GetRequiredService<ITerminalRuntimeInspector>(),
            sp.GetRequiredService<IAlgoTradingService>(), sp.GetRequiredService<ITerminalStorageInspector>(),
            sp.GetRequiredService<IAuditLogger>(), sp.GetRequiredService<IBridgeInstaller>()));
        services.AddSingleton(sp => new MainWindow(
            sp.GetRequiredService<MainViewModel>(), sp.GetRequiredService<ITerminalStorageInspector>(),
            sp.GetRequiredService<TerminalOperationCoordinator>(),
            () => sp.GetRequiredService<TelegramSettingsDialog>(),
            sp.GetRequiredService<TelegramApplicationLifetime>()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (telegramLifetime is not null)
        {
            try { telegramLifetime.StopAsync().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        httpClient?.Dispose();
        if (singleInstance is not null)
        {
            singleInstance.ReleaseMutex();
            singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    sealed class SystemDelay : IDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
public sealed class TelegramApplicationLifetime
{
    readonly ITelegramBotService botService;
    readonly TimeSpan shutdownTimeout;
    readonly CancellationTokenSource operations = new();

    internal TelegramApplicationLifetime(ITelegramBotService botService, TimeSpan shutdownTimeout)
    {
        this.botService = botService;
        this.shutdownTimeout = shutdownTimeout;
    }

    internal Task StartAsync() => botService.StartAsync(operations.Token);
    internal void CancelOperations() => operations.Cancel();

    internal async Task StopAsync()
    {
        CancelOperations();
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        try
        {
            await botService.StopAsync(timeout.Token).WaitAsync(shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
    }
}

public sealed class BoundedTelegramBotService : ITelegramBotService
{
    readonly ITelegramBotService inner;
    readonly TimeSpan shutdownTimeout;

    internal BoundedTelegramBotService(ITelegramBotService inner, TimeSpan shutdownTimeout)
    {
        this.inner = inner;
        this.shutdownTimeout = shutdownTimeout;
    }

    public TelegramBotState State => inner.State;
    public event EventHandler? StateChanged
    {
        add => inner.StateChanged += value;
        remove => inner.StateChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
    public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => inner.ApplySettingsAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var disposal = inner.DisposeAsync().AsTask();
        try { await disposal.WaitAsync(shutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
