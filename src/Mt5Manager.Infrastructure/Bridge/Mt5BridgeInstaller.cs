using System.Security;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Bridge;

/// <summary>
/// Builds an MQL5 expert into the form the terminal loads. Declared next to its only implementation
/// so the installer can be exercised without MetaEditor.
/// </summary>
public interface IBridgeCompiler
{
    /// <summary>Absolute path of the compiler shipped beside the terminal, or <c>null</c> when unavailable.</summary>
    string? Resolve(TerminalRegistration terminal);

    Task<BridgeCompileResult> CompileAsync(string compilerPath, string sourcePath, CancellationToken cancellationToken);
}

public sealed record BridgeCompileResult(bool Success, string? Detail);

/// <summary>
/// Deploys the bundled bridge expert into a terminal's <c>MQL5\Experts</c> folder and compiles it, so
/// account and Algo Trading state become readable without any manual file copying.
/// </summary>
public sealed class Mt5BridgeInstaller : IBridgeInstaller
{
    internal const string SourceFileName = "Mt5ManagerBridge.mq5";
    internal const string CompiledFileName = "Mt5ManagerBridge.ex5";

    private const string AttachHint = "Drag Mt5ManagerBridge onto any chart";

    private readonly string bundledSourcePath;
    private readonly IBridgeCompiler compiler;
    private readonly SemaphoreSlim compileGate = new(1, 1);

    public Mt5BridgeInstaller(string? bundledSourcePath = null, IBridgeCompiler? compiler = null)
    {
        this.bundledSourcePath = bundledSourcePath ?? Path.Combine(AppContext.BaseDirectory, SourceFileName);
        this.compiler = compiler ?? new MetaEditorBridgeCompiler();
    }

    public Task<BridgeInstallationStatus?> InspectAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return Task.FromResult(Status(terminal, compiler.Resolve(terminal) is not null));
    }

    public async Task<BridgeInstallationResult> InstallAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        var directory = ExpertsDirectory(terminal);
        if (directory is null)
            return new(false, $"Terminal '{terminal.DisplayName}' has no verified data directory, so the bridge cannot be installed.", null);
        if (!File.Exists(bundledSourcePath))
            return new(false, $"The bundled bridge source '{SourceFileName}' is missing from the application folder.", null);

        var source = Path.Combine(directory, SourceFileName);
        var compiled = Path.Combine(directory, CompiledFileName);
        var compilerPath = compiler.Resolve(terminal);
        var before = BridgeInstallationState.NotInstalled;

        try
        {
            Directory.CreateDirectory(directory);
            before = State(source, compiled);
            if (before is BridgeInstallationState.NotInstalled or BridgeInstallationState.SourceOutdated) WriteSource(source);

            if (compilerPath is not null && (before is not BridgeInstallationState.Installed || !IsCompiledFresh(source, compiled)))
            {
                var compile = await CompileAsync(compilerPath, source, cancellationToken).ConfigureAwait(false);
                var compiledNow = Status(terminal, canCompile: true);
                if (!compile.Success) return new(false, $"Bridge compilation failed: {compile.Detail}", compiledNow);
                if (!IsCompiledFresh(source, compiled))
                    return new(false, $"MetaEditor reported a successful compile but '{CompiledFileName}' was not produced.", compiledNow);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return new(false, $"The bridge could not be installed: {exception.Message}", Status(terminal, compilerPath is not null));
        }

        return new(true, Message(before, compilerPath is not null), Status(terminal, compilerPath is not null)!);
    }

    /// <summary>Read-only projection; returns <c>null</c> when the terminal has no addressable MQL5 folder.</summary>
    private BridgeInstallationStatus? Status(TerminalRegistration terminal, bool canCompile)
    {
        var directory = ExpertsDirectory(terminal);
        if (directory is null || !File.Exists(bundledSourcePath)) return null;
        var source = Path.Combine(directory, SourceFileName);
        var compiled = Path.Combine(directory, CompiledFileName);
        return new(State(source, compiled), source, compiled, canCompile);
    }

    /// <summary>
    /// Fails closed: an unverified or unusable data directory yields <c>null</c>, so nothing is ever
    /// written into a terminal whose data folder has not been proven.
    /// </summary>
    private static string? ExpertsDirectory(TerminalRegistration terminal)
    {
        if (!terminal.DataDirectoryVerified || string.IsNullOrWhiteSpace(terminal.DataDirectory)) return null;
        try
        {
            var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(terminal.DataDirectory));
            var mql5 = Path.Combine(data, "MQL5");
            return Directory.Exists(mql5) ? Path.Combine(mql5, "Experts") : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private BridgeInstallationState State(string source, string compiled)
    {
        if (!File.Exists(source)) return BridgeInstallationState.NotInstalled;
        if (IsSourceOutdated(source)) return BridgeInstallationState.SourceOutdated;
        return IsCompiledFresh(source, compiled) ? BridgeInstallationState.Installed : BridgeInstallationState.NotCompiled;
    }

    /// <summary>Byte comparison against the bundled source; no version parsing, so it cannot drift.</summary>
    private bool IsSourceOutdated(string installedSource)
    {
        if (new FileInfo(bundledSourcePath).Length != new FileInfo(installedSource).Length) return true;
        return !File.ReadAllBytes(bundledSourcePath).AsSpan().SequenceEqual(File.ReadAllBytes(installedSource));
    }

    internal static bool IsCompiledFresh(string source, string compiled) =>
        File.Exists(compiled) && File.GetLastWriteTimeUtc(compiled) >= File.GetLastWriteTimeUtc(source);

    private void WriteSource(string source)
    {
        var temp = source + ".tmp";
        try
        {
            File.WriteAllBytes(temp, File.ReadAllBytes(bundledSourcePath));
            File.Move(temp, source, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task<BridgeCompileResult> CompileAsync(string compilerPath, string source, CancellationToken cancellationToken)
    {
        await compileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await compiler.CompileAsync(compilerPath, source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            compileGate.Release();
        }
    }

    private static string Message(BridgeInstallationState before, bool canCompile)
    {
        if (before is BridgeInstallationState.Installed)
            return $"The bridge is already up to date. {AttachHint} if it is not attached yet.";
        if (!canCompile)
            return $"Bridge source installed. Compile {SourceFileName} in MetaEditor (F7), then drag it onto a chart.";

        return before switch
        {
            BridgeInstallationState.NotCompiled => $"Bridge recompiled. {AttachHint} to activate it.",
            BridgeInstallationState.SourceOutdated => $"Bridge updated and compiled. {AttachHint} to activate it.",
            _ => $"Bridge installed and compiled. {AttachHint} to activate it."
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
