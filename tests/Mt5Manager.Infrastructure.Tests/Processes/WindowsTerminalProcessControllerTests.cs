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
    private readonly HashSet<int> _spawnedIds = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-process-{Guid.NewGuid():N}");
    private string FixturePath => Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "ExitOnClose", "bin", "Debug", "net8.0-windows", "ExitOnClose.exe");

    [Fact]
    public async Task Start_preserves_arguments_and_working_directory_and_reports_running_state()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "working directory")).FullName;
        var output = Path.Combine(_root, "launch.json");
        var terminal = Registration(workingDirectory, output, "ignore", "argument with spaces", "quoted\"value");

        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));
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

        Track(await _controller.StartAsync(terminal, CancellationToken.None));
        var launch = await ReadLaunchAsync(output);

        launch.WorkingDirectory.Should().Be(rootDirectory);
    }

    [Fact]
    public async Task Natural_exit_releases_tracked_process_without_another_controller_call()
    {
        var terminal = Registration(_root, Path.Combine(_root, "natural-exit.json"), "self-exit");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));

        await WaitUntilAsync(() => TrackedProcessCount() == 0);

        TrackedProcessCount().Should().Be(0);
        Process.GetProcesses().Any(candidate => candidate.Id == pid).Should().BeFalse();
    }

    [Fact]
    public async Task Immediate_exit_releases_tracked_process()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var terminal = Registration(_root, Path.Combine(_root, $"immediate-{attempt}.json"), "exit-now");
            Track(await _controller.StartAsync(terminal, CancellationToken.None));

            await WaitUntilAsync(() => TrackedProcessCount() == 0);
        }

        TrackedProcessCount().Should().Be(0);
    }

    [Fact]
    public async Task Stop_returns_graceful_exit_when_process_accepts_close()
    {
        var terminal = Registration(_root, Path.Combine(_root, "graceful.json"), "exit");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        (await _controller.GetStateAsync(terminal, CancellationToken.None)).State.Should().Be(TerminalState.Stopped);
    }

    [Fact]
    public async Task Stop_times_out_without_force_and_leaves_process_running()
    {
        var terminal = Registration(_root, Path.Combine(_root, "timeout.json"), "ignore");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.TimedOut, null));
        Process.GetProcessById(pid).HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_force_terminates_process_after_timeout()
    {
        var terminal = Registration(_root, Path.Combine(_root, "force.json"), "ignore");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));
        await WaitForMainWindowAsync(pid);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), true, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ForceTerminated, null));
    }

    [Fact]
    public async Task Start_rejects_duplicate_registered_launch()
    {
        var terminal = Registration(_root, Path.Combine(_root, "duplicate.json"), "ignore");
        Track(await _controller.StartAsync(terminal, CancellationToken.None));

        var act = () => _controller.StartAsync(terminal, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Same_executable_name_in_another_install_is_not_a_duplicate()
    {
        var install = CopyFixtureInstall("other-install");
        var first = Registration(_root, Path.Combine(_root, "first.json"), "ignore", "first");
        Track(await _controller.StartAsync(first, CancellationToken.None));
        var second = new TerminalRegistration(Guid.NewGuid(), "Copy", Path.Combine(install, Path.GetFileName(FixturePath)), _root, install,
            [Path.Combine(_root, "second.json"), "ignore", "second"], DiscoverySource.Manual, true);

        var secondPid = Track(await _controller.StartAsync(second, CancellationToken.None));

        secondPid.Should().BePositive();
    }

    [Fact]
    public async Task GetStateAsync_reports_externally_started_terminal_as_running()
    {
        var terminal = Registration(_root, Path.Combine(_root, "external.json"), "ignore");
        var external = StartExternally(terminal);
        await WaitForMainWindowAsync(external.Id);

        var state = await _controller.GetStateAsync(terminal, CancellationToken.None);

        state.Should().Be(new TerminalRuntimeState(TerminalState.Running, external.Id, null));
    }

    [Fact]
    public async Task StopAsync_closes_externally_started_terminal_gracefully()
    {
        var terminal = Registration(_root, Path.Combine(_root, "external-graceful.json"), "exit");
        var external = StartExternally(terminal);
        await WaitForMainWindowAsync(external.Id);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        external.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_rejects_while_externally_started_terminal_runs()
    {
        var terminal = Registration(_root, Path.Combine(_root, "external-duplicate.json"), "ignore");
        var external = StartExternally(terminal);

        var act = () => _controller.StartAsync(terminal, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        external.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task GetStateAsync_reports_stopped_when_no_matching_process_runs()
    {
        var install = CopyFixtureInstall("idle-install");
        var terminal = new TerminalRegistration(Guid.NewGuid(), "Idle", Path.Combine(install, Path.GetFileName(FixturePath)), _root, install,
            [Path.Combine(_root, "idle.json"), "ignore"], DiscoverySource.Manual, true);

        var state = await _controller.GetStateAsync(terminal, CancellationToken.None);

        state.Should().Be(new TerminalRuntimeState(TerminalState.Stopped, null, null));
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

        // The controller may have started a process this test never observed, or a failed test may have leaked one.
        foreach (var leftover in Process.GetProcessesByName("ExitOnClose"))
        {
            if (_spawnedIds.Contains(leftover.Id)) { leftover.Dispose(); continue; }
            try
            {
                leftover.Kill(entireProcessTree: true);
                await leftover.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
            finally { leftover.Dispose(); }
        }

        Environment.CurrentDirectory = Path.GetTempPath();
        for (var attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }

    private TerminalRegistration Registration(string workingDirectory, params string[] arguments) =>
        new(Guid.NewGuid(), "Fixture", FixturePath, _root, workingDirectory, arguments, DiscoverySource.Manual, true);

    private int TrackedProcessCount() =>
        ((System.Collections.IDictionary)typeof(WindowsTerminalProcessController)
            .GetField("_processes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_controller)!).Count;

    private int Track(int pid)
    {
        try
        {
            _spawned.Add(Process.GetProcessById(pid));
            _spawnedIds.Add(pid);
        }
        catch (ArgumentException) { }
        return pid;
    }

    private Process StartExternally(TerminalRegistration terminal)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = terminal.ExecutablePath,
            WorkingDirectory = terminal.WorkingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in terminal.Arguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the external fixture.");
        _spawned.Add(process);
        _spawnedIds.Add(process.Id);
        return process;
    }

    private string CopyFixtureInstall(string name)
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(FixturePath)!))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        return target;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition was not reached.");
    }

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
