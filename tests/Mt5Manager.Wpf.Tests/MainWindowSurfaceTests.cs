using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;

namespace Mt5Manager.Wpf.Tests;

public sealed class MainWindowSurfaceTests
{
    const string Warning = "Global Algo Trading affects every EA in this terminal.";

    [Fact]
    public async Task Terminal_detail_renders_the_bridge_status_and_install_button()
    {
        var rendered = await RunStaAsync(async () =>
        {
            var application = EnsureApplication();

            var terminal = new TerminalRegistration(
                Guid.NewGuid(), "Alpha", @"C:\terminal64.exe", @"C:\Data", @"C:\", [], DiscoverySource.Manual, true);
            var bridge = new Bridge();
            var viewModel = new MainViewModel(new Discovery([terminal]), new Registry(), new Process(), bridge: bridge);
            await viewModel.RefreshAsync();
            var window = new MainWindow(viewModel, new Inspector(), new TerminalOperationCoordinator(new Registry(), new Process(), new Cleaner(), new Audit()));
            window.WindowStyle = WindowStyle.None;
            window.ShowInTaskbar = false;
            window.Left = -10000;
            window.Top = -10000;
            window.Width = 1280;
            window.Height = 800;
            window.Show();
            window.UpdateLayout();

            try
            {
                var status = FindTextBlock(window, x => x.Text == "Bridge source installed · not compiled");
                status.Should().NotBeNull("the terminal detail surface must report the bridge installation state");

                var button = FindDescendants<Button>(window).FirstOrDefault(x => (x.Content as string) == "Install bridge");
                button.Should().NotBeNull("the user needs a control that installs the bridge");

                button!.IsEnabled.Should().BeTrue();
                button.Command.CanExecute(null).Should().BeTrue();
                return status!.Text;
            }
            finally
            {
                window.Close();
            }
        });

        rendered.Should().Be("Bridge source installed · not compiled");
    }

    [Fact]
    public async Task Terminal_detail_renders_global_algo_warning_before_controls()
    {
        var controls = await RunStaAsync(async () =>
        {
            var application = EnsureApplication();

            var terminal = new TerminalRegistration(
                Guid.NewGuid(), "Alpha", @"C:\terminal64.exe", @"C:\Data", @"C:\", [], DiscoverySource.Manual, true);
            var viewModel = new MainViewModel(new Discovery([terminal]), new Registry(), new Process());
            await viewModel.RefreshAsync();
            var window = new MainWindow(viewModel, new Inspector(), new TerminalOperationCoordinator(new Registry(), new Process(), new Cleaner(), new Audit()));
            window.WindowStyle = WindowStyle.None;
            window.ShowInTaskbar = false;
            window.Left = -10000;
            window.Top = -10000;
            window.Width = 1280;
            window.Height = 800;
            window.Show();
            window.UpdateLayout();

            try
            {
                var warning = FindTextBlock(window, x => x.Text == Warning);
                warning.Should().NotBeNull("the warning must be rendered in the terminal detail surface");

                var container = FindAncestor<StackPanel>(warning!)!;
                container.Should().NotBeNull("the warning must be rendered inside the Algo control panel");

                var children = container!.Children.OfType<FrameworkElement>().ToList();
                var warningIndex = children.IndexOf(warning!);
                var enableIndex = children.FindIndex(x => x is Button { Content: "Enable Algo" });
                var disableIndex = children.FindIndex(x => x is Button { Content: "Disable Algo" });

                return (warningIndex, enableIndex, disableIndex);
            }
            finally
            {
                window.Close();
            }
        });

        controls.warningIndex.Should().Be(0);
        controls.enableIndex.Should().Be(1);
        controls.disableIndex.Should().Be(2);
    }
    [Fact]
    public async Task Scheduler_dialog_discloses_runtime_retry_and_Telegram_behavior()
    {
        var rendered = await RunStaAsync(async () =>
        {
            EnsureApplication();
            var terminal = new TerminalRegistration(
                Guid.NewGuid(), "Alpha", @"C:\terminal64.exe", @"C:\Data", @"C:\", [], DiscoverySource.Manual, true);
            var viewModel = new AlgoScheduleViewModel(new ScheduleService(terminal));
            await viewModel.LoadAsync();
            var window = new AlgoScheduleDialog(viewModel)
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000
            };
            window.Show();
            window.UpdateLayout();
            try
            {
                var disclosure = FindTextBlock(window, text =>
                    text.Text.Contains("must remain running", StringComparison.Ordinal) &&
                    text.Text.Contains("retried once after 5 minutes", StringComparison.Ordinal) &&
                    text.Text.Contains("Telegram", StringComparison.Ordinal));
                var save = FindDescendants<Button>(window).SingleOrDefault(button => Equals(button.Content, "Save schedule"));
                return (Disclosure: disclosure?.Text, SaveButtonFound: save is not null);
            }
            finally
            {
                window.Close();
            }
        });

        rendered.Disclosure.Should().NotBeNull();
        rendered.SaveButtonFound.Should().BeTrue();
    }


    static App EnsureApplication()
    {
        var application = System.Windows.Application.Current as App ?? new App();
        // These tests close their window before the harness thread exits. The default
        // OnLastWindowClose mode would shut the shared application down and leave later
        // surface tests with a window that never realizes its visual tree.
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        application.InitializeComponent();
        return application;
    }

    static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in Children(root))
        {
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    static readonly Dispatcher UiDispatcher = StartUiThread();

    static Dispatcher StartUiThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    static async Task<TResult> RunStaAsync<TResult>(Func<Task<TResult>> action)
    {
        // WPF allows a single Application per process, bound to the thread that created it.
        // Every surface test therefore runs on one shared, pumped STA thread; a per-test
        // thread would leave later tests with a window that never realizes its visual tree.
        var inner = await UiDispatcher.InvokeAsync(action).Task.WaitAsync(TimeSpan.FromSeconds(20));
        return await inner.WaitAsync(TimeSpan.FromSeconds(20));
    }

    static TextBlock? FindTextBlock(DependencyObject root, Func<TextBlock, bool> predicate)
    {
        if (root is TextBlock text && predicate(text)) return text;
        foreach (var child in Children(root))
        {
            if (FindTextBlock(child, predicate) is { } found) return found;
        }
        return null;
    }

    static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(current); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match) return match;
        }
        return null;
    }

    static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++) yield return VisualTreeHelper.GetChild(parent, index);
    }

    sealed class Discovery(IReadOnlyList<TerminalRegistration> items) : ITerminalDiscovery
    {
        public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(items);
    }

    sealed class Registry : ITerminalRegistry
    {
        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TerminalRegistration>>([]);
        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class Process : ITerminalProcessController
    {
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(new TerminalRuntimeState(TerminalState.Running, 42, null));
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(42);
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) => Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
    }

    sealed class Inspector : ITerminalStorageInspector
    {
        public Task<IReadOnlyList<CategoryUsage>> InspectAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CategoryUsage>>([]);
    }

    sealed class Cleaner : ITerminalCleanupService
    {
        public Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(TerminalRegistration terminal, IReadOnlySet<CleanupCategory> categories, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CleanupCategoryResult>>([]);
    }

    sealed class Audit : IAuditLogger
    {
        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class Bridge : IBridgeInstaller
    {
        public Task<BridgeInstallationStatus?> InspectAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult<BridgeInstallationStatus?>(new BridgeInstallationStatus(BridgeInstallationState.NotCompiled, @"C:\Data\MQL5\Experts\Mt5ManagerBridge.mq5", @"C:\Data\MQL5\Experts\Mt5ManagerBridge.ex5", true));

        public Task<BridgeInstallationResult> InstallAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BridgeInstallationResult(true, "Bridge recompiled.", null));
    }
    sealed class ScheduleService(TerminalRegistration terminal) : IAlgoScheduler
    {
        public bool IsRunning => true;
        public DateTimeOffset? LastEvaluationAt => null;
        public string? LastError => null;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AlgoSchedulerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AlgoSchedulerSnapshot([], [], [terminal], true, null, null, "UTC"));
        public Task<AlgoSchedule> SaveAsync(AlgoSchedule schedule, CancellationToken cancellationToken = default) =>
            Task.FromResult(schedule);
        public Task RemoveAsync(Guid scheduleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
