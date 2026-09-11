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
                CleanTarget(target, cancellationToken, failures, ref deletedFiles, ref deletedBytes);

            results.Add(new CleanupCategoryResult(category, deletedFiles, deletedBytes, failures));
        }

        return Task.FromResult<IReadOnlyList<CleanupCategoryResult>>(results);
    }

    private static void CleanTarget(
        string root,
        CancellationToken cancellationToken,
        List<FileFailure> failures,
        ref long deletedFiles,
        ref long deletedBytes)
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
                        failures.Add(new FileFailure(directory, exception.Message));
                        break;
                    }

                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                            continue;
                        }

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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new FileFailure(directory, exception.Message));
            }
            finally
            {
                entries?.Dispose();
            }
        }
    }
}
