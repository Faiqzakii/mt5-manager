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
        return Task.Run<IReadOnlyList<CategoryUsage>>(
            () => Perform(terminal, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<CategoryUsage> Perform(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        var result = new CategoryUsage[Categories.Length];

        for (var index = 0; index < Categories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = Categories[index];
            long fileCount = 0;
            long bytes = 0;

            try
            {
                foreach (var target in targetResolver.Resolve(terminal, category))
                {
                    foreach (var path in EnumerateFiles(target, cancellationToken))
                    {
                        long length;
                        try { length = new FileInfo(path).Length; }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        fileCount++;
                        bytes += length;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A preview must stay useful when one category cannot be resolved: report the
                // remaining categories instead of turning the whole preview into an error.
            }

            result[index] = new CategoryUsage(category, fileCount, bytes);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerator<string>? entries = null;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        path = entries.Current;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }

                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) == 0) yield return path;
                    else if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                }
            }
            finally
            {
                entries?.Dispose();
            }
        }
    }
}
