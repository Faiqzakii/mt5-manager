using System.Text;
using FluentAssertions;
using Mt5Manager.Infrastructure.Bridge;

namespace Mt5Manager.Infrastructure.Tests.Bridge;

/// <summary>
/// Pins the parser against logs captured from a real MetaEditor run: the compiler's exit code is
/// unreliable, so this log format is the only success signal the installer has.
/// </summary>
public sealed class MetaEditorBridgeCompilerTests
{
    [Fact]
    public void A_successful_compile_log_yields_no_errors()
    {
        var result = MetaEditorBridgeCompiler.ReadResult(File.ReadAllBytes(Fixture("compile-success.log")));

        result.Errors.Should().Be(0);
        result.Warnings.Should().Be(0);
        result.FirstDiagnostic.Should().BeNull();
    }

    [Fact]
    public void A_failed_compile_log_yields_the_error_count_and_first_diagnostic()
    {
        var result = MetaEditorBridgeCompiler.ReadResult(File.ReadAllBytes(Fixture("compile-errors.log")));

        result.Errors.Should().Be(3);
        result.Warnings.Should().Be(0);
        result.FirstDiagnostic.Should().Contain("undeclared identifier 'this_is_not_valid'");
    }

    [Fact]
    public void A_log_without_a_summary_is_reported_as_unreadable()
    {
        var result = MetaEditorBridgeCompiler.ReadResult(Encoding.UTF8.GetBytes("MetaEditor was interrupted.\n"));

        result.Errors.Should().Be(-1, "an unreadable log must never be mistaken for a successful compile");
        result.Tail.Should().Contain("interrupted");
    }

    [Fact]
    public void A_bom_less_utf16_log_is_decoded()
    {
        var result = MetaEditorBridgeCompiler.ReadResult(Encoding.Unicode.GetBytes("Result: 2 errors, 1 warning, 30 msec elapsed"));

        result.Errors.Should().Be(2);
        result.Warnings.Should().Be(1);
    }

    [Fact]
    public void A_utf8_log_is_decoded()
    {
        var result = MetaEditorBridgeCompiler.ReadResult(Encoding.UTF8.GetBytes("Result: 0 errors, 0 warnings, 30 msec elapsed"));

        result.Errors.Should().Be(0);
    }

    [Fact]
    public void An_empty_log_is_reported_as_unreadable() =>
        MetaEditorBridgeCompiler.ReadResult([]).Errors.Should().Be(-1);

    [Fact]
    public void Compile_logs_are_written_outside_the_terminal_data_folder()
    {
        var directory = MetaEditorBridgeCompiler.CompileLogDirectory();

        Directory.Exists(directory).Should().BeTrue();
        directory.Should().StartWith(Path.GetTempPath()).And.NotContain("MQL5");
    }

    [Fact]
    public void The_compiler_is_resolved_from_the_terminal_installation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mt5-compiler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var terminal = new Mt5Manager.Domain.Models.TerminalRegistration(
                Guid.NewGuid(), "Broker", Path.Combine(directory, "terminal64.exe"), @"C:\Data", directory, [], Mt5Manager.Domain.Models.DiscoverySource.Manual, true);
            var compiler = new MetaEditorBridgeCompiler();

            compiler.Resolve(terminal).Should().BeNull("MetaEditor is not present beside this fake terminal");

            var path = Path.Combine(directory, "MetaEditor64.exe");
            File.WriteAllBytes(path, []);
            compiler.Resolve(terminal).Should().Be(path);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bridge", name);
        File.Exists(path).Should().BeTrue($"the captured MetaEditor log '{name}' must be deployed beside the tests");
        return path;
    }
}
