using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Bridge;

/// <summary>
/// Drives the MetaEditor command-line compiler that ships beside <c>terminal64.exe</c>. Resolving the
/// compiler from the terminal's own installation keeps the generated <c>.ex5</c> build-compatible with
/// the terminal that will load it.
/// </summary>
public sealed partial class MetaEditorBridgeCompiler : IBridgeCompiler
{
    internal const string CompilerFileName = "MetaEditor64.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public string? Resolve(TerminalRegistration terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(terminal.ExecutablePath));
            if (directory is null) return null;
            var candidate = Path.Combine(directory, CompilerFileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<BridgeCompileResult> CompileAsync(string compilerPath, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var logPath = Path.Combine(CompileLogDirectory(), $"{Guid.NewGuid():N}.log");
        try
        {
            using var process = Start(compilerPath, sourcePath, logPath);
            if (process is null) return new(false, $"'{CompilerFileName}' could not be started.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                return new(false, $"'{CompilerFileName}' did not finish within {Timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            if (!File.Exists(logPath)) return new(false, $"'{CompilerFileName}' produced no compile log.");

            // The compiler's exit code is not trustworthy: it has been observed returning 1 on success
            // and 0 on failure. The log is the only reliable signal.
            var result = ReadResult(File.ReadAllBytes(logPath));
            return result.Errors == 0
                ? new(true, null)
                : new(false, result.FirstDiagnostic ?? $"{result.Errors} errors, {result.Warnings} warnings");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, exception.Message);
        }
        finally
        {
            TryDelete(logPath);
        }
    }

    /// <summary>
    /// Starts MetaEditor with a literal command line. The compiler requires its <c>/compile:</c> and
    /// <c>/log:</c> paths to be individually double-quoted, and a raw <see cref="ProcessStartInfo.Arguments"/>
    /// string preserves those quotes; <see cref="ProcessStartInfo.ArgumentList"/> escapes them away and
    /// makes MetaEditor exit silently without compiling.
    /// </summary>
    private static Process? Start(string compilerPath, string sourcePath, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = compilerPath,
            Arguments = $"/compile:\"{sourcePath}\" /log:\"{logPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(startInfo);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
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

    /// <summary>Compile logs stay out of the user's terminal data folder.</summary>
    internal static string CompileLogDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mt5Manager", "bridge-compile");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static CompileLog ReadResult(byte[] log)
    {
        var text = Decode(log);
        var summary = Summary().Match(text);
        if (!summary.Success) return new(-1, 0, null, text.Trim());

        var diagnostic = text
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains(" : error ", StringComparison.Ordinal));
        return new(int.Parse(summary.Groups["errors"].Value), int.Parse(summary.Groups["warnings"].Value), diagnostic, null);
    }

    internal readonly record struct CompileLog(int Errors, int Warnings, string? FirstDiagnostic, string? Tail);

    /// <summary>MetaEditor writes UTF-16LE with a BOM; the heuristic covers a BOM-less log.</summary>
    private static string Decode(byte[] log)
    {
        if (log.Length == 0) return string.Empty;
        if (log.Length >= 2 && log[0] == 0xFF && log[1] == 0xFE) return Encoding.Unicode.GetString(log, 2, log.Length - 2);
        return LooksLikeUtf16Le(log) ? Encoding.Unicode.GetString(log) : Encoding.UTF8.GetString(log);
    }

    private static bool LooksLikeUtf16Le(byte[] log)
    {
        var pairs = log.Length / 2;
        if (pairs == 0) return false;
        var zeros = 0;
        for (var index = 1; index < pairs * 2; index += 2)
        {
            if (log[index] == 0) zeros++;
        }
        return zeros * 4 >= pairs * 3;
    }

    [GeneratedRegex(@"Result:\s*(?<errors>\d+)\s+errors?,\s*(?<warnings>\d+)\s+warnings?")]
    private static partial Regex Summary();
}
