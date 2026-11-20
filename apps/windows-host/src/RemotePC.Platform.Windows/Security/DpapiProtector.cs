using System.Security.Cryptography;
using RemotePC.Core.Security;

namespace RemotePC.Platform.Windows.Security;

/// <summary>
/// DPAPI (CurrentUser) protection. Secrets are bound to the Windows user account: copying the portable
/// folder to another PC or user makes them unreadable, and the host then creates a fresh identity
/// (phones must re-pair). That is intentional.
/// </summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static byte[] Entropy(string purpose) => System.Text.Encoding.UTF8.GetBytes("RemotePC-AI|" + purpose);

    public byte[] Protect(byte[] plaintext, string purpose) =>
        ProtectedData.Protect(plaintext, Entropy(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData, string purpose) =>
        ProtectedData.Unprotect(protectedData, Entropy(purpose), DataProtectionScope.CurrentUser);
}
