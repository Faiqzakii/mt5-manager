using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class MainWindowSurfaceTests
{
    const string Warning = "Global Algo Trading affects every EA in this terminal.";

    [Fact]
    public async Task Terminal_detail_renders_global_algo_warning_before_controls()
    {
        var controls = await RunStaAsync(async () =>
        {
            var application = System.Windows.Application.Current as App ?? new App();
            application.InitializeComponent();

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

    static async Task<TResult> RunStaAsync<TResult>(Func<Task<TResult>> action)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
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
}
