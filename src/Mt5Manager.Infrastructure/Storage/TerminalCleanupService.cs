using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Storage;

public sealed class TerminalCleanupService(ICleanupTargetResolver targetResolver) : ITerminalCleanupService
{
    private static readonly CleanupCategory[] Categories =
        [CleanupCategory.Logs, CleanupCategory.Ticks, CleanupCategory.History];

    public Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(
        TerminalRegistration terminal,
        IReadOnlySet<CleanupCategory> categories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(categories);
        var results = new List<CleanupCategoryResult>(categories.Count);

        foreach (var category in Categories)
        {
            if (!categories.Contains(category)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            long deletedFiles = 0;
            long deletedBytes = 0;
            var failures = new List<FileFailure>();

            foreach (var target in targetResolver.Resolve(terminal, category))
            {
                foreach (var path in EnumerateFiles(target, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var length = new FileInfo(path).Length;
                        File.Delete(path);
                        deletedFiles++;
                        deletedBytes += length;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new FileFailure(path, exception.Message));
                    }
                }
            }

            results.Add(new CleanupCategoryResult(category, deletedFiles, deletedBytes, failures));
        }

        return Task.FromResult<IReadOnlyList<CleanupCategoryResult>>(results);
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                    yield return path;
                else if ((attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(path);
            }
        }
    }
}
