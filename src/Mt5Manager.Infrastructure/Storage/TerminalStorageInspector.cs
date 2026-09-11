using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Storage;

public sealed class TerminalStorageInspector(ICleanupTargetResolver targetResolver) : ITerminalStorageInspector
{
    private static readonly CleanupCategory[] Categories =
        [CleanupCategory.Logs, CleanupCategory.Ticks, CleanupCategory.History];

    public Task<IReadOnlyList<CategoryUsage>> InspectAsync(
        TerminalRegistration terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var result = new CategoryUsage[Categories.Length];

        for (var index = 0; index < Categories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = Categories[index];
            long fileCount = 0;
            long bytes = 0;

            foreach (var target in targetResolver.Resolve(terminal, category))
            {
                foreach (var path in EnumerateFiles(target, cancellationToken))
                {
                    fileCount++;
                    bytes += new FileInfo(path).Length;
                }
            }

            result[index] = new CategoryUsage(category, fileCount, bytes);
        }

        return Task.FromResult<IReadOnlyList<CategoryUsage>>(result);
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
