using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;
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
        launch.Arguments.Should().Equal("argument with spaces", "quoted\"value", $"/datadir:{_root}");
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
    public async Task Natural_exit_during_active_stop_does_not_deadlock()
    {
        var terminal = Registration(_root, Path.Combine(_root, "natural-stop.json"), "self-exit");
        Track(await _controller.StartAsync(terminal, CancellationToken.None));

        var stop = _controller.StopAsync(terminal, TimeSpan.FromSeconds(10), false, CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));

        finished.Should().BeSameAs(stop, "the exit handler must not deadlock an active stop");
        (await stop).Outcome.Should().BeOneOf(StopOutcome.ExitedGracefully, StopOutcome.AlreadyStopped);
        TrackedProcessCount().Should().Be(0);
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
    public async Task Stop_rejects_unverified_registration_before_process_scan()
    {
        var terminal = Registration(_root, Path.Combine(_root, "unverified.json"), "ignore") with { DataDirectoryVerified = false };
        var controller = new WindowsTerminalProcessController(new ThrowingHandleSource());

        var result = await controller.StopAsync(terminal, TimeSpan.Zero, false, CancellationToken.None);

        result.Outcome.Should().Be(StopOutcome.Failed);
        result.Error.Should().Contain("verified data directory");
    }

    [Fact]
    public async Task Stop_resolves_default_data_directory_when_command_line_has_no_datadir()
    {
        var install = CopyFixtureInstall("default-install");
        var appData = Directory.CreateDirectory(Path.Combine(_root, "app-data")).FullName;
        var candidate = Directory.CreateDirectory(Path.Combine(appData, "installation-hash")).FullName;
        CreateMt5Structure(candidate);
        File.WriteAllText(Path.Combine(candidate, "origin.txt"), install);
        var terminal = new TerminalRegistration(Guid.NewGuid(), "Fixture", Path.Combine(install, Path.GetFileName(FixturePath)),
            candidate, install, [Path.Combine(_root, "default-data.json"), "exit"], DiscoverySource.Manual, true);
        var external = StartExternally(terminal);
        await WaitForMainWindowAsync(external.Id);
        using var controller = new WindowsTerminalProcessController(dataDirectoryResolver: new Mt5DataDirectoryResolver(appData));

        var result = await controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        external.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_refuses_process_when_default_data_directory_is_ambiguous()
    {
        var install = CopyFixtureInstall("ambiguous-install");
        var appData = Directory.CreateDirectory(Path.Combine(_root, "ambiguous-app-data")).FullName;
        foreach (var name in new[] { "first", "second" })
        {
            var candidate = Directory.CreateDirectory(Path.Combine(appData, name)).FullName;
            CreateMt5Structure(candidate);
            File.WriteAllText(Path.Combine(candidate, "origin.txt"), install);
        }
        var registeredData = Path.Combine(appData, "first");
        var terminal = new TerminalRegistration(Guid.NewGuid(), "Fixture", Path.Combine(install, Path.GetFileName(FixturePath)),
            registeredData, install, [Path.Combine(_root, "ambiguous-data.json"), "ignore"], DiscoverySource.Manual, true);
        var external = StartExternally(terminal);
        await WaitForMainWindowAsync(external.Id);
        using var controller = new WindowsTerminalProcessController(dataDirectoryResolver: new Mt5DataDirectoryResolver(appData));

        var result = await controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), false, CancellationToken.None);

        result.Outcome.Should().Be(StopOutcome.Failed);
        result.Error.Should().Contain("data directory");
        external.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_refuses_sole_candidate_when_data_directory_identity_is_unreadable()
    {
        var terminal = Registration(_root, Path.Combine(_root, "missing-identity.json"), "ignore");
        var external = StartExternally(terminal with { Arguments = terminal.Arguments.Where(x => !x.StartsWith("/datadir:", StringComparison.OrdinalIgnoreCase)).ToArray() });
        await WaitForMainWindowAsync(external.Id);

        var result = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), false, CancellationToken.None);

        result.Outcome.Should().Be(StopOutcome.Failed);
        result.Error.Should().Contain("data directory");
        external.HasExited.Should().BeFalse();
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

    [Fact]
    public async Task Stop_immediately_after_start_closes_terminal_without_waiting_for_timeout()
    {
        var terminal = Registration(_root, Path.Combine(_root, "fresh.json"), "exit");
        Track(await _controller.StartAsync(terminal, CancellationToken.None));

        var result = await _controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
    }

    [Fact]
    public async Task Stop_leaves_terminal_of_another_data_directory_running()
    {
        var firstData = DataDirectory("sibling-first");
        var secondData = DataDirectory("sibling-second");
        var first = RegistrationWithData(firstData, _root, Path.Combine(_root, "sibling-first.json"), "exit", $"/datadir:{firstData}");
        var second = RegistrationWithData(secondData, _root, Path.Combine(_root, "sibling-second.json"), "exit", $"/datadir:{secondData}");
        var firstPid = Track(await _controller.StartAsync(first, CancellationToken.None));
        var secondPid = Track(await _controller.StartAsync(second, CancellationToken.None));

        (await _controller.GetStateAsync(first, CancellationToken.None)).Should().Be(new TerminalRuntimeState(TerminalState.Running, firstPid, null));
        var result = await _controller.StopAsync(first, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        Process.GetProcessById(secondPid).HasExited.Should().BeFalse();
        (await _controller.GetStateAsync(second, CancellationToken.None)).Should().Be(new TerminalRuntimeState(TerminalState.Running, secondPid, null));
        var act = () => _controller.StartAsync(second, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Stop_closes_every_instance_of_the_same_record_gracefully()
    {
        var data = DataDirectory("duplicate-exit");
        var terminal = RegistrationWithData(data, _root, Path.Combine(_root, "duplicate-exit.json"), "exit", $"/datadir:{data}");
        var first = StartExternally(terminal);
        var second = StartExternally(WithLaunchRecord(terminal, "duplicate-exit-second.json"));

        var result = await _controller.StopAsync(terminal, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        result.Should().Be(new StopResult(StopOutcome.ExitedGracefully, null));
        first.HasExited.Should().BeTrue();
        second.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_reports_timeout_until_every_instance_of_the_record_is_gone()
    {
        var data = DataDirectory("duplicate-ignore");
        var terminal = RegistrationWithData(data, _root, Path.Combine(_root, "duplicate-ignore.json"), "ignore", $"/datadir:{data}");
        var first = StartExternally(terminal);
        var second = StartExternally(WithLaunchRecord(terminal, "duplicate-ignore-second.json"));

        var timedOut = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(300), false, CancellationToken.None);

        timedOut.Should().Be(new StopResult(StopOutcome.TimedOut, null));
        first.HasExited.Should().BeFalse();
        second.HasExited.Should().BeFalse();

        var forced = await _controller.StopAsync(terminal, TimeSpan.FromMilliseconds(300), true, CancellationToken.None);

        forced.Should().Be(new StopResult(StopOutcome.ForceTerminated, null));
        first.HasExited.Should().BeTrue();
        second.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task Unreadable_process_identity_reports_error_and_refuses_control()
    {
        var terminal = Registration(DataDirectory("opaque"), Path.Combine(_root, "opaque.json"), "ignore");
        var external = StartExternally(terminal);
        var controller = new WindowsTerminalProcessController(new OpaqueHandleSource(external.Id));

        var state = await controller.GetStateAsync(terminal, CancellationToken.None);

        state.State.Should().Be(TerminalState.Error);
        state.Error.Should().NotBeNullOrWhiteSpace();
        var start = () => controller.StartAsync(terminal, CancellationToken.None);
        await start.Should().ThrowAsync<InvalidOperationException>();
        var stop = await controller.StopAsync(terminal, TimeSpan.FromMilliseconds(200), false, CancellationToken.None);
        stop.Outcome.Should().Be(StopOutcome.Failed);
        stop.Error.Should().NotBeNullOrWhiteSpace();
        external.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_releases_tracking_without_terminating_process_and_is_idempotent()
    {
        var terminal = Registration(_root, Path.Combine(_root, "dispose.json"), "ignore");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));

        _controller.Dispose();
        _controller.Dispose();

        TrackedProcessCount().Should().Be(0);
        using var process = Process.GetProcessById(pid);
        process.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Operations_after_dispose_throw_ObjectDisposedException()
    {
        var terminal = Registration(_root, Path.Combine(_root, "disposed.json"), "ignore");
        _controller.Dispose();

        var getState = () => _controller.GetStateAsync(terminal, CancellationToken.None);
        var start = () => _controller.StartAsync(terminal, CancellationToken.None);
        var stop = () => _controller.StopAsync(terminal, TimeSpan.Zero, false, CancellationToken.None);

        await getState.Should().ThrowAsync<ObjectDisposedException>();
        await start.Should().ThrowAsync<ObjectDisposedException>();
        await stop.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_on_blocked_synchronization_context_completes_while_stop_is_inflight()
    {
        var terminal = Registration(_root, Path.Combine(_root, "blocked-dispatcher.json"), "ignore");
        var pid = Track(await _controller.StartAsync(terminal, CancellationToken.None));
        await WaitForMainWindowAsync(pid);

        // Simulates WPF shutdown: an operation started on the dispatcher thread is still in flight when
        // Dispose blocks that thread. If continuations post back to the blocked context instead of the
        // thread pool, the active operation count never reaches zero and Dispose deadlocks.
        Exception? failure = null;
        Task<StopResult>? stopTask = null;
        using var finished = new ManualResetEventSlim(false);
        var dispatcher = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new UndrainedSynchronizationContext());
            try
            {
                stopTask = _controller.StopAsync(terminal, TimeSpan.FromSeconds(1), false, CancellationToken.None);
                _controller.Dispose();
            }
            catch (Exception exception) { failure = exception; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        dispatcher.Start();

        finished.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("Dispose must not wait on continuations posted to the blocked context");
        failure.Should().BeNull();
        dispatcher.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

        stopTask.Should().NotBeNull();
        (await Task.WhenAny(stopTask!, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(stopTask);
        (await stopTask!).Outcome.Should().Be(StopOutcome.TimedOut);
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
        new(Guid.NewGuid(), "Fixture", FixturePath, _root, workingDirectory, [.. arguments, $"/datadir:{_root}"], DiscoverySource.Manual, true);

    private TerminalRegistration RegistrationWithData(string dataDirectory, string workingDirectory, params string[] arguments) =>
        new(Guid.NewGuid(), "Fixture", FixturePath, dataDirectory, workingDirectory, arguments, DiscoverySource.Manual, true);

    private string DataDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    private static void CreateMt5Structure(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        Directory.CreateDirectory(Path.Combine(directory, "bases"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
    }

    // Both instances of one record must share the data directory to count as duplicates, but they
    // cannot write the same launch record concurrently.
    private TerminalRegistration WithLaunchRecord(TerminalRegistration terminal, string fileName)
    {
        var arguments = terminal.Arguments.ToArray();
        arguments[0] = Path.Combine(_root, fileName);
        return terminal with { Arguments = arguments };
    }

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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<LaunchRecord>(stream)!;
            }
            catch (Exception exception) when (attempt < 100 && exception is IOException or JsonException or FileNotFoundException)
            {
                await Task.Delay(25);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mt5Manager.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record LaunchRecord(string[] Arguments, string WorkingDirectory, int ProcessId);

    private sealed class ThrowingHandleSource : IProcessHandleSource
    {
        public nint Open(int processId) => throw new InvalidOperationException("Process scan must not run.");
        public void Close(nint handle) => throw new InvalidOperationException("Process scan must not run.");
    }

    private sealed class OpaqueHandleSource(int opaqueProcessId) : IProcessHandleSource
    {
        private readonly LimitedRightsProcessHandleSource _inner = new();

        public nint Open(int processId) => processId == opaqueProcessId ? nint.Zero : _inner.Open(processId);

        public void Close(nint handle) => _inner.Close(handle);
    }

    // Mimics a blocked WPF dispatcher: continuations queue for the owning thread, which never drains them.
    private sealed class UndrainedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_posted) _posted.Enqueue((callback, state));
        }

        public override void Send(SendOrPostCallback callback, object? state) => callback(state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
