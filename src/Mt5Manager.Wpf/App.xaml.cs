using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Infrastructure.Discovery;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Infrastructure.Processes;
using Mt5Manager.Infrastructure.Storage;
using Mt5Manager.Wpf.ViewModels;
namespace Mt5Manager.Wpf;
public partial class App:System.Windows.Application
{
 ServiceProvider? provider; Mutex? singleInstance;
 protected override void OnStartup(StartupEventArgs e){base.OnStartup(e);singleInstance=new Mutex(initiallyOwned:true,@"Local\Mt5Manager.SingleInstance",out var isFirstInstance);if(!isFirstInstance){MessageBox.Show("MT5 Manager is already running in this session.","MT5 Manager",MessageBoxButton.OK,MessageBoxImage.Information);singleInstance.Dispose();singleInstance=null;Shutdown();return;}var services=new ServiceCollection();
  services.AddSingleton<ITerminalRegistry,JsonTerminalRegistry>();services.AddSingleton<ITerminalProcessController,WindowsTerminalProcessController>();services.AddSingleton<IAuditLogger,JsonLinesAuditLogger>();services.AddSingleton<ICleanupTargetResolver,CleanupTargetResolver>();services.AddSingleton<ITerminalStorageInspector,TerminalStorageInspector>();services.AddSingleton<ITerminalCleanupService,TerminalCleanupService>();
  services.AddSingleton<ITerminalRuntimeInspector,Mt5Manager.Infrastructure.Runtime.Mt5RuntimeSnapshotReader>();services.AddSingleton<ITerminalAlgoTradingController,WindowsTerminalAlgoTradingController>();
  services.AddSingleton<ITerminalDiscoverySource,ProcessDiscoverySource>();services.AddSingleton<ITerminalDiscoverySource,ShortcutDiscoverySource>();services.AddSingleton<ITerminalDiscoverySource,StandardLocationDiscoverySource>();services.AddSingleton<IMt5DataDirectoryResolver,Mt5DataDirectoryResolver>();services.AddSingleton<ITerminalDiscovery,TerminalDiscovery>();services.AddSingleton<TerminalOperationCoordinator>();services.AddSingleton<MainViewModel>(sp=>new MainViewModel(sp.GetRequiredService<ITerminalDiscovery>(),sp.GetRequiredService<ITerminalRegistry>(),sp.GetRequiredService<ITerminalProcessController>(),sp.GetRequiredService<ITerminalRuntimeInspector>(),sp.GetRequiredService<ITerminalAlgoTradingController>()));services.AddSingleton<MainWindow>();provider=services.BuildServiceProvider();provider.GetRequiredService<MainWindow>().Show();}
 protected override void OnExit(ExitEventArgs e){provider?.Dispose();if(singleInstance is not null){singleInstance.ReleaseMutex();singleInstance.Dispose();}base.OnExit(e);}
}
