using System.Security.Cryptography;
using System.Text;

namespace HookScope.Security;

public static class GitHubSignatureValidator
{
    private const string Prefix = "sha256=";

    public static bool IsValid(
        ReadOnlySpan<byte> payload,
        string secret,
        string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)
            || !signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> hexadecimalSignature = signatureHeader.AsSpan(Prefix.Length);
        if (hexadecimalSignature.Length != 64)
        {
            return false;
        }

        Span<byte> suppliedSignature = stackalloc byte[32];

        try
        {
            Convert.FromHexString(hexadecimalSignature, suppliedSignature, out _, out int bytesWritten);
            if (bytesWritten != suppliedSignature.Length)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        Span<byte> expectedSignature = stackalloc byte[32];
        HMACSHA256.HashData(secretBytes, payload, expectedSignature);

        return CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature);
    }
}
