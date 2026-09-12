using System.Security.Cryptography;
using System.Text;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Infrastructure.Security;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUserSecretProtector : ISecretProtector
{
    internal static byte[] Entropy => (byte[])_entropy.Clone();
    private static readonly byte[] _entropy = "Mt5Manager.Telegram.Token.v1"u8.ToArray();

    public ProtectedTelegramToken Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return new ProtectedTelegramToken(Convert.ToBase64String(
                ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(ProtectedTelegramToken protectedValue)
    {
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedValue?.Value!);
        }
        catch (Exception exception) when (exception is ArgumentNullException or FormatException)
        {
            throw new CryptographicException("The protected token payload is invalid.",
                exception as FormatException ?? new FormatException("The protected token payload is missing.", exception));
        }

        var plaintext = ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
