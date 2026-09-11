using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Processes;

namespace Mt5Manager.Infrastructure.Tests.Processes;

public sealed class WindowsTerminalProcessControllerTests : IAsyncLifetime
{
    private readonly WindowsTerminalProcessController _controller = new();
    private readonly List<Process> _spawned = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-process-{Guid.NewGuid():N}");
    private string FixturePath => Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "ExitOnClose", "bin", "Debug", "net8.0-windows", "ExitOnClose.exe");

    [Fact]
    public async Task Start_preserves_arguments_and_working_directory_and_reports_running_state()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "working directory")).FullName;
        var output = Path.Combine(_root, "launch.json");
        var terminal = Registration(workingDirectory, output, "ignore", "argument with spaces", "quoted\"value");

        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);
        var launch = await ReadLaunchAsync(output);
        var state = await _controller.GetStateAsync(terminal, CancellationToken.None);

        launch.WorkingDirectory.Should().Be(Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar));
        launch.Arguments.Should().Equal("argument with spaces", "quoted\"value");
        launch.ProcessId.Should().Be(pid);
        state.Should().Be(new TerminalRuntimeState(TerminalState.Running, pid, null));
    }

    [Fact]
    public async Task Start_preserves_root_working_directory()
    {
        var rootDirectory = Path.GetPathRoot(FixturePath)!;
        var output = Path.Combine(_root, "root-launch.json");
        var terminal = Registration(rootDirectory, output, "ignore");

        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);
        var launch = await ReadLaunchAsync(output);

        launch.WorkingDirectory.Should().Be(rootDirectory);
    }

    [Fact]
    public async Task Stop_returns_graceful_exit_when_process_accepts_close()
    {
        var terminal = Registration(_root, Path.Combine(_root, "graceful.json"), "exit");
        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        (await _controller.GetStateAsync(terminal, CancellationToken.None)).State.Should().Be(TerminalState.Stopped);
    }

    [Fact]
    public async Task Stop_times_out_without_force_and_leaves_process_running()
    {
        var terminal = Registration(_root, Path.Combine(_root, "timeout.json"), "ignore");
        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.TimedOut, null));
        Process.GetProcessById(pid).HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_force_terminates_process_after_timeout()
    {
        var terminal = Registration(_root, Path.Combine(_root, "force.json"), "ignore");
        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), true, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ForceTerminated, null));
    }

    [Fact]
    public async Task Start_rejects_duplicate_registered_launch()
    {
        var terminal = Registration(_root, Path.Combine(_root, "duplicate.json"), "ignore");
        var pid = await _controller.StartAsync(terminal, CancellationToken.None);
        Track(pid);

        var act = () => _controller.StartAsync(terminal, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Same_executable_with_different_launch_identity_is_not_a_duplicate()
    {
        var first = Registration(_root, Path.Combine(_root, "first.json"), "ignore", "first");
        var second = Registration(_root, Path.Combine(_root, "second.json"), "ignore", "second");
        Track(await _controller.StartAsync(first, CancellationToken.None));

        var secondPid = await _controller.StartAsync(second, CancellationToken.None);
        Track(secondPid);

        secondPid.Should().BePositive();
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.Exists(FixturePath).Should().BeTrue($"fixture should be built at {FixturePath}");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var process in _spawned)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }

        Environment.CurrentDirectory = Path.GetTempPath();
        for (var attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(50); }
        }
    }

    private TerminalRegistration Registration(string workingDirectory, params string[] arguments) =>
        new(Guid.NewGuid(), "Fixture", FixturePath, _root, workingDirectory, arguments, DiscoverySource.Manual, true);

    private void Track(int pid) => _spawned.Add(Process.GetProcessById(pid));

    private static async Task WaitForMainWindowAsync(int pid)
    {
        using var process = Process.GetProcessById(pid);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Fixture did not create a window.");
    }

    private static async Task<LaunchRecord> ReadLaunchAsync(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++) await Task.Delay(25);
        return JsonSerializer.Deserialize<LaunchRecord>(await File.ReadAllTextAsync(path))!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mt5Manager.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record LaunchRecord(string[] Arguments, string WorkingDirectory, int ProcessId);
}
