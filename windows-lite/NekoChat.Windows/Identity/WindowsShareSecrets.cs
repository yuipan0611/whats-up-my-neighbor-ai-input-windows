using System.Security.Cryptography;
using System.Text;
using NekoChat.Core.ClassroomFiles;
namespace NekoChat.Windows.Identity;

// Only created when a user shares a classroom file; independent of historical chat identity/pairing.
internal sealed class WindowsShareSecrets:IShareSecrets
{
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("NekoChat.classroom-files.v1.owner");
    public byte[] Protect(byte[] bytes)=>ProtectedData.Protect(bytes,Entropy,DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] bytes)=>ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser);
}
