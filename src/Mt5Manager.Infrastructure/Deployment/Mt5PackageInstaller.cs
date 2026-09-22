using System.Security;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Deployment;

public sealed class Mt5PackageInstaller : IMt5PackageInstaller
{
    public async Task<Mt5PackageInstallResult> InstallAsync(
        string sourcePath,
        Mt5PackageKind kind,
        IReadOnlyList<TerminalRegistration> terminals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(terminals);
        cancellationToken.ThrowIfCancellationRequested();

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Rejected(sourcePath, kind, terminals, $"The source path is invalid: {exception.Message}");
        }

        if (!string.Equals(Path.GetExtension(fullSource), ".ex5", StringComparison.OrdinalIgnoreCase))
            return Rejected(fullSource, kind, terminals, "The source file must have the .ex5 extension.");
        if (!File.Exists(fullSource))
            return Rejected(fullSource, kind, terminals, $"The source file '{fullSource}' does not exist.");

        var outcomes = new List<Mt5PackageInstallOutcome>(terminals.Count);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var terminal in terminals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = ResolveDestination(terminal, kind, Path.GetFileName(fullSource));
            if (resolution.Error is not null)
            {
                outcomes.Add(Failure(terminal, resolution.Error));
                continue;
            }

            var destination = resolution.Path!;
            if (!destinations.Add(destination))
            {
                outcomes.Add(Failure(terminal, $"The destination '{destination}' is already targeted by another selected terminal."));
                continue;
            }

            outcomes.Add(await CopyAsync(fullSource, destination, terminal, cancellationToken).ConfigureAwait(false));
        }

        return new(fullSource, kind, outcomes);
    }

    private static async Task<Mt5PackageInstallOutcome> CopyAsync(
        string source,
        string destination,
        TerminalRegistration terminal,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return new(terminal.Id, terminal.DisplayName, true,
                $"Installed '{Path.GetFileName(source)}' to '{destination}'.", destination);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            TryDelete(temporary);
            return new(terminal.Id, terminal.DisplayName, false,
                $"Could not install to '{destination}': {exception.Message}", destination);
        }
    }

    private static (string? Path, string? Error) ResolveDestination(
        TerminalRegistration terminal,
        Mt5PackageKind kind,
        string fileName)
    {
        if (!terminal.DataDirectoryVerified || string.IsNullOrWhiteSpace(terminal.DataDirectory))
            return (null, $"Terminal '{terminal.DisplayName}' does not have a verified data directory.");

        try
        {
            var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(terminal.DataDirectory));
            if (!Directory.Exists(dataDirectory))
                return (null, $"Terminal '{terminal.DisplayName}' data directory '{dataDirectory}' does not exist.");
            if (ContainsReparsePoint(dataDirectory))
                return (null, $"Terminal '{terminal.DisplayName}' data directory crosses a reparse point and cannot be verified safely.");

            var mql5Root = Path.GetFullPath(Path.Combine(dataDirectory, "MQL5"));
            if (!IsContained(dataDirectory, mql5Root) || !Directory.Exists(mql5Root))
                return (null, $"Terminal '{terminal.DisplayName}' has no existing MQL5 root at '{mql5Root}'.");
            if (ContainsReparsePoint(mql5Root))
                return (null, $"Terminal '{terminal.DisplayName}' MQL5 root crosses a reparse point and cannot be verified safely.");

            var folderName = kind switch
            {
                Mt5PackageKind.ExpertAdvisor => "Experts",
                Mt5PackageKind.Indicator => "Indicators",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported MT5 package kind.")
            };
            var targetDirectory = Path.GetFullPath(Path.Combine(mql5Root, folderName));
            if (!IsContained(mql5Root, targetDirectory) || !Directory.Exists(targetDirectory))
                return (null, $"Terminal '{terminal.DisplayName}' has no existing {folderName} directory at '{targetDirectory}'.");
            if (ContainsReparsePoint(targetDirectory))
                return (null, $"Terminal '{terminal.DisplayName}' {folderName} directory crosses a reparse point and cannot be verified safely.");

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
            return IsContained(targetDirectory, destination)
                ? (destination, null)
                : (null, $"The destination for terminal '{terminal.DisplayName}' escapes its {folderName} directory.");
        }
        catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentOutOfRangeException)
        {
            return (null, $"Terminal '{terminal.DisplayName}' destination could not be verified: {exception.Message}");
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsContained(string parent, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static Mt5PackageInstallResult Rejected(
        string sourcePath,
        Mt5PackageKind kind,
        IReadOnlyList<TerminalRegistration> terminals,
        string message) =>
        new(sourcePath, kind, terminals.Select(terminal => Failure(terminal, message)).ToArray());

    private static Mt5PackageInstallOutcome Failure(TerminalRegistration terminal, string message) =>
        new(terminal.Id, terminal.DisplayName, false, message, null);

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;

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
