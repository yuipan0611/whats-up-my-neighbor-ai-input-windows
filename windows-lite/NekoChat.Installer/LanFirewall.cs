using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Installer;

/// <summary>Only the short-lived elevated helper changes LAN chat rules.</summary>
internal static class LanFirewall
{
    internal const int Profiles = 2 | 4; // Private + Public, never Domain.
    internal static string RuleName(string path) => "NekoChat.LAN.TCP8768." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];

    internal static void Configure(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.GetFileName(path).Equals("NekoChat.Windows.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidDataException("找不到已安裝的 NekoChat 程式。");

        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        dynamic rules = policy.Rules;
        string name = RuleName(path);
        bool added = false;
        var changed = new List<(object Rule, int Profiles, bool Enabled)>();
        try
        {
            // Managed policy restrictions must not be reported as successful setup.
            if ((int)policy.LocalPolicyModifyState != 0)
                throw new InvalidOperationException("學校的網路管理政策不允許此項設定，請聯絡管理者。");
            var owned = new List<object>();
            foreach (dynamic existing in rules)
                if ((string)existing.Name == name) owned.Add((object)existing);
            if (owned.Count > 1 || owned.Count == 1 && !IsExpected(owned[0], path))
                throw new InvalidOperationException("現有 NekoChat 網路規則需要管理者確認。");
            if (owned.Count == 0)
            {
                dynamic allow = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                allow.Name = name;
                allow.Description = "NekoChat LAN chat: local subnet, TCP 8768, Private/Public only.";
                allow.ApplicationName = path;
                allow.Protocol = 6;
                allow.LocalPorts = "8768";
                allow.RemoteAddresses = "LocalSubnet";
                allow.Direction = 1;
                allow.Profiles = Profiles;
                allow.Action = 1;
                allow.Enabled = true;
                rules.Add(allow);
                added = true;
            }

            // Windows may have created these exact app-name block rules after a
            // dismissed permission prompt. Do not alter custom rules or other apps.
            var defaults = new List<object>();
            foreach (dynamic rule in rules)
                if (IsBlock(rule, path) &&
                    ((string)rule.Name).Equals("nekochat.windows.exe", StringComparison.OrdinalIgnoreCase))
                    defaults.Add((object)rule);
            foreach (dynamic rule in defaults)
            {
                int originalProfiles = (int)rule.Profiles;
                changed.Add(((object)rule, originalProfiles, (bool)rule.Enabled));
                int remaining = originalProfiles & ~Profiles;
                if (remaining == 0) rule.Enabled = false;
                else rule.Profiles = remaining; // Preserve Domain/other profile coverage.
            }

            bool verified = false;
            foreach (dynamic rule in rules)
            {
                if ((string)rule.Name == name && IsExpected((object)rule, path)) verified = true;
                if (IsBlock(rule, path))
                    throw new InvalidOperationException("另有規則封鎖 NekoChat，請管理者協助確認。");
            }
            if (!verified) throw new InvalidOperationException("無法確認網路設定已完成。");
        }
        catch
        {
            // If a custom policy prevents setup, leave the previous rules intact.
            foreach (var snapshot in changed.AsEnumerable().Reverse())
            {
                dynamic rule = snapshot.Rule;
                rule.Profiles = snapshot.Profiles;
                rule.Enabled = snapshot.Enabled;
            }
            if (added) rules.Remove(name);
            throw;
        }
    }

    private static bool IsBlock(dynamic rule, string path) =>
        (bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 0 &&
        ((int)rule.Protocol is 6 or 256) && ((int)rule.Profiles & Profiles) != 0 &&
        string.Equals((string)rule.ApplicationName, path, StringComparison.OrdinalIgnoreCase);

    private static bool IsExpected(object value, string path)
    {
        dynamic rule = value;
        return (bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 1 &&
            (int)rule.Protocol == 6 && (int)rule.Profiles == Profiles &&
            (string)rule.LocalPorts == "8768" &&
            ((string)rule.RemoteAddresses).Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)rule.ApplicationName, path, StringComparison.OrdinalIgnoreCase);
    }

    internal static int RunHelper(string path)
    {
        try { Configure(path); return 0; }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { return 5; }
        catch { return 1; }
    }
}
