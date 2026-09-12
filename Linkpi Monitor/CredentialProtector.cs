using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Linkpi_Monitor;

internal static class CredentialProtector
{
    internal const string Prefix = "dpapi:user:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Linkpi Monitor device credentials");

    internal static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clearBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    internal static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException(
                "The saved device password could not be decrypted for this Windows account.",
                exception);
        }
    }
}
