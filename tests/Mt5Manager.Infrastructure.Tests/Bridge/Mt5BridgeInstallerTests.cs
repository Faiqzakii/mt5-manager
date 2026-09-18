using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Bridge;

namespace Mt5Manager.Infrastructure.Tests.Bridge;

public sealed class Mt5BridgeInstallerTests : IDisposable
{
    private const string BundledSource = "// Mt5ManagerBridge v2\n#property version \"2.00\"\n";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mt5-bridge-{Guid.NewGuid():N}");
    private readonly string bundledSource;
    private readonly FakeCompiler compiler = new();

    public Mt5BridgeInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "MQL5"));
        bundledSource = Path.Combine(root, "bundled", "Mt5ManagerBridge.mq5");
        Directory.CreateDirectory(Path.GetDirectoryName(bundledSource)!);
        File.WriteAllText(bundledSource, BundledSource);
    }

    private string ExpertsDirectory => Path.Combine(root, "MQL5", "Experts");
    private string InstalledSource => Path.Combine(ExpertsDirectory, "Mt5ManagerBridge.mq5");
    private string InstalledExpert => Path.Combine(ExpertsDirectory, "Mt5ManagerBridge.ex5");

    private TerminalRegistration Terminal(bool verified = true) =>
        new(Guid.NewGuid(), "Broker", @"C:\Apps\Broker\terminal64.exe", root, root, [], DiscoverySource.Manual, verified);

    private Mt5BridgeInstaller Installer() => new(bundledSource, compiler);

    private void WriteInstalledSource(string content = BundledSource)
    {
        Directory.CreateDirectory(ExpertsDirectory);
        File.WriteAllText(InstalledSource, content);
    }

    private void WriteInstalledExpert(DateTime? writeTime = null)
    {
        Directory.CreateDirectory(ExpertsDirectory);
        File.WriteAllBytes(InstalledExpert, [1, 2, 3]);
        File.SetLastWriteTimeUtc(InstalledExpert, writeTime ?? File.GetLastWriteTimeUtc(InstalledSource).AddSeconds(1));
    }

    [Fact]
    public async Task Inspect_is_unavailable_when_the_data_directory_is_unverified()
    {
        var status = await Installer().InspectAsync(Terminal(verified: false));

        status.Should().BeNull();
        compiler.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspect_is_unavailable_when_the_terminal_has_no_mql5_folder()
    {
        Directory.Delete(Path.Combine(root, "MQL5"));

        var status = await Installer().InspectAsync(Terminal());

        status.Should().BeNull("an absent MQL5 folder means the bridge cannot be addressed at all");
    }

    [Fact]
    public async Task Inspect_reports_every_installation_state()
    {
        var installer = Installer();

        (await installer.InspectAsync(Terminal()))!.State.Should().Be(BridgeInstallationState.NotInstalled);

        WriteInstalledSource();
        (await installer.InspectAsync(Terminal()))!.State.Should().Be(BridgeInstallationState.NotCompiled);

        WriteInstalledExpert();
        (await installer.InspectAsync(Terminal()))!.State.Should().Be(BridgeInstallationState.Installed);

        WriteInstalledSource("// stale bridge\n");
        (await installer.InspectAsync(Terminal()))!.State.Should().Be(BridgeInstallationState.SourceOutdated);
    }

    [Fact]
    public async Task Inspect_reports_whether_the_terminal_can_compile()
    {
        compiler.CompilerPath = null;

        var status = await Installer().InspectAsync(Terminal());

        status!.CanCompile.Should().BeFalse();
        status.SourcePath.Should().Be(InstalledSource);
        status.CompiledPath.Should().Be(InstalledExpert);
    }

    [Fact]
    public async Task Install_refuses_an_unverified_data_directory_and_writes_nothing()
    {
        var result = await Installer().InstallAsync(Terminal(verified: false));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("verified data directory");
        result.Status.Should().BeNull();
        Directory.Exists(ExpertsDirectory).Should().BeFalse();
        compiler.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_copies_the_bundled_source_and_compiles_it()
    {
        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeTrue();
        result.Status!.State.Should().Be(BridgeInstallationState.Installed);
        File.ReadAllText(InstalledSource).Should().Be(BundledSource);
        result.Message.Should().Contain("installed and compiled");
        compiler.Tasks.Should().ContainSingle().Which.Should().Be(InstalledSource);
        Directory.GetFiles(ExpertsDirectory, "*.tmp").Should().BeEmpty("the staging file must not survive the install");
    }

    [Fact]
    public async Task Install_is_a_no_op_when_the_bridge_is_already_current()
    {
        var installer = Installer();
        await installer.InstallAsync(Terminal());
        var expert = File.GetLastWriteTimeUtc(InstalledExpert);

        var result = await installer.InstallAsync(Terminal());

        result.Success.Should().BeTrue();
        result.Status!.State.Should().Be(BridgeInstallationState.Installed);
        result.Message.Should().Contain("already up to date");
        compiler.Tasks.Should().ContainSingle("an up-to-date bridge must not be recompiled");
        File.GetLastWriteTimeUtc(InstalledExpert).Should().Be(expert);
    }

    [Fact]
    public async Task Install_replaces_an_outdated_source_and_recompiles()
    {
        WriteInstalledSource("// Mt5ManagerBridge v1\n");
        WriteInstalledExpert();

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("updated and compiled");
        File.ReadAllText(InstalledSource).Should().Be(BundledSource);
        compiler.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Install_recompiles_when_the_expert_is_older_than_the_source()
    {
        WriteInstalledSource();
        WriteInstalledExpert(File.GetLastWriteTimeUtc(InstalledSource).AddSeconds(-5));

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("recompiled");
        compiler.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Install_does_not_rewrite_an_installed_source_that_matches()
    {
        WriteInstalledSource();
        var source = File.GetLastWriteTimeUtc(InstalledSource);

        await Installer().InstallAsync(Terminal());

        File.GetLastWriteTimeUtc(InstalledSource).Should().Be(source, "the bundled and installed sources are identical");
    }

    [Fact]
    public async Task Install_reports_manual_compile_when_the_terminal_ships_no_compiler()
    {
        compiler.CompilerPath = null;

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeTrue("the source is installed; only the compile step degraded");
        result.Status!.State.Should().Be(BridgeInstallationState.NotCompiled);
        result.Status.CanCompile.Should().BeFalse();
        result.Message.Should().Contain("MetaEditor (F7)");
        File.Exists(InstalledSource).Should().BeTrue();
        File.Exists(InstalledExpert).Should().BeFalse();
    }

    [Fact]
    public async Task Install_fails_when_the_compiler_reports_errors()
    {
        compiler.Failure = "Mt5ManagerBridge.mq5(12,4) : error 256: undeclared identifier 'x'";
        compiler.WriteExpert = false;

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("compilation failed").And.Contain("undeclared identifier");
        result.Status!.State.Should().Be(BridgeInstallationState.NotCompiled);
    }

    [Fact]
    public async Task Install_fails_when_the_compiler_reports_success_but_produces_no_expert()
    {
        compiler.WriteExpert = false;

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("was not produced");
        result.Status!.State.Should().Be(BridgeInstallationState.NotCompiled);
    }

    [Fact]
    public async Task Install_fails_when_the_bundled_source_is_missing()
    {
        File.Delete(bundledSource);

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("missing from the application folder");
    }

    [Fact]
    public async Task Install_surfaces_a_defective_mql5_folder_as_a_failure()
    {
        Directory.CreateDirectory(ExpertsDirectory);
        // A file where the Experts folder is expected makes every write fail.
        Directory.Delete(ExpertsDirectory);
        File.WriteAllText(ExpertsDirectory, "blocked");

        var result = await Installer().InstallAsync(Terminal());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("could not be installed");
    }

    [Fact]
    public async Task Install_serializes_concurrent_compiles()
    {
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(secondRoot, "MQL5"));
        var second = new TerminalRegistration(Guid.NewGuid(), "Second", @"C:\Apps\Broker2\terminal64.exe", secondRoot, secondRoot, [], DiscoverySource.Manual, true);
        var installer = Installer();

        await Task.WhenAll(installer.InstallAsync(Terminal()), installer.InstallAsync(second));

        compiler.Tasks.Should().HaveCount(2);
        compiler.PeakConcurrency.Should().Be(1, "compiles share temporary log storage and must be serialized");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeCompiler : IBridgeCompiler
    {
        private readonly object sync = new();
        private int active;

        public string? CompilerPath { get; set; } = @"C:\Apps\Broker\MetaEditor64.exe";
        public bool WriteExpert { get; set; } = true;
        public string? Failure { get; set; }
        public List<string> Tasks { get; } = [];
        public int PeakConcurrency { get; private set; }

        public string? Resolve(TerminalRegistration terminal) => CompilerPath;

        public async Task<BridgeCompileResult> CompileAsync(string compilerPath, string sourcePath, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                Tasks.Add(sourcePath);
                PeakConcurrency = Math.Max(PeakConcurrency, ++active);
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                if (Failure is not null) return new(false, Failure);
                if (WriteExpert)
                {
                    var expert = Path.ChangeExtension(sourcePath, ".ex5");
                    File.WriteAllBytes(expert, [9]);
                    File.SetLastWriteTimeUtc(expert, File.GetLastWriteTimeUtc(sourcePath).AddSeconds(1));
                }
                return new(true, null);
            }
            finally
            {
                lock (sync) active--;
            }
        }
    }
}
