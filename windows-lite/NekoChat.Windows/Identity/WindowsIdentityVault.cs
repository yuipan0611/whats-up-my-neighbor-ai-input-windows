using System.Security.Cryptography;
using NekoChat.Core.Identity;

namespace NekoChat.Windows.Identity;

internal static class WindowsIdentityVault
{
    internal static IIdentityKeyVault Create(string identityDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows identity protection is required.");
        return new IdentityFileVault(identityDirectory, new CurrentUserProtector());
    }
    private sealed class CurrentUserProtector : IIdentityProtector
    {
        public byte[] Protect(byte[] privateKey, byte[] entropy) => ProtectedData.Protect(privateKey, entropy, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] protectedKey, byte[] entropy) => ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
    }
}
