using System.Security.Cryptography;
using System.Text;

namespace Mt5Manager.Infrastructure.Persistence;

internal static class InterprocessFileLock
{
    public static async Task<FileStream> AcquireAsync(string protectedPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(protectedPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        var lockDirectory = Path.Combine(Path.GetTempPath(), "Mt5Manager", "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{hash}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
