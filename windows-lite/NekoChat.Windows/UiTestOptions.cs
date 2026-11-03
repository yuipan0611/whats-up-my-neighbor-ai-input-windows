using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NekoChat.Core;

namespace NekoChat.Windows;

internal sealed record UiTestOptions(string DataDirectory, int Port, string MutexName)
{
    public const string DataArg = "--ui-test-data";
    public const string PortArg = "--ui-test-port";

    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public string DatabaseDirectory => Path.Combine(DataDirectory, "data");
    public string DatabaseFilePath => Path.Combine(DatabaseDirectory, "nekochat.db");

    public static UiTestOptions? Parse(string[] args)
    {
        string? dataDir = null;
        int? port = null;
        bool hasDataArg = false;
        bool hasPortArg = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == DataArg)
            {
                if (hasDataArg) throw new ArgumentException("重複指定 --ui-test-data 參數。");
                if (i + 1 >= args.Length) throw new ArgumentException("缺少 --ui-test-data 參數的目錄路徑。");
                dataDir = args[++i];
                hasDataArg = true;
            }
            else if (args[i] == PortArg)
            {
                if (hasPortArg) throw new ArgumentException("重複指定 --ui-test-port 參數。");
                if (i + 1 >= args.Length) throw new ArgumentException("缺少 --ui-test-port 參數的埠號數值。");
                string portStr = args[++i];
                if (!int.TryParse(portStr, out int parsedPort) || parsedPort < 1 || parsedPort > 65535)
                {
                    throw new ArgumentException($"無效的 --ui-test-port 埠號：'{portStr}'。必須為 1 到 65535 之間的整數。");
                }
                port = parsedPort;
                hasPortArg = true;
            }
            else
            {
                // 若包含 ui-test 參數，但不支援其他多餘引數，拋出參數例外
                if (hasDataArg || hasPortArg || Array.Exists(args, a => a is DataArg or PortArg))
                {
                    throw new ArgumentException($"不支援的啟動參數：'{args[i]}'。");
                }
                return null;
            }
        }

        if (!hasDataArg)
        {
            if (hasPortArg)
            {
                throw new ArgumentException("指定 --ui-test-port 時必須同時指定 --ui-test-data。");
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new ArgumentException("UI 測試資料目錄不可為空。");
        }

        string fullPath = Path.GetFullPath(dataDir);
        AssertSafeDataDirectory(fullPath);

        int resolvedPort = port ?? DirectLanMessaging.DefaultPort;
        string mutexName = DeriveMutexName(fullPath);

        return new UiTestOptions(fullPath, resolvedPort, mutexName);
    }

    public static void AssertSafeDataDirectory(string dataDirectory)
    {
        if (IsInsideOrEqual(dataDirectory, Settings.DirectoryPath))
        {
            throw new InvalidOperationException($"UI 測試資料目錄不可設於正式資料目錄（{Settings.DirectoryPath}）底下。");
        }
    }

    public static bool IsInsideOrEqual(string targetPath, string basePath)
    {
        string fullTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullTarget, fullBase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = fullBase + Path.DirectorySeparatorChar;
        if (fullTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            string altPrefix = fullBase + Path.AltDirectorySeparatorChar;
            if (fullTarget.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string DeriveMutexName(string dataDirectory)
    {
        string full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalized = full.ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string hex = Convert.ToHexString(hash)[..16];
        return $"Local\\NekoChat.WindowsLite.UiTest.{hex}";
    }

    internal static string ResolveRootDirectory(Settings source, bool isolated)
    {
        if (isolated && source.PersistencePath is not null)
        {
            string dir = Path.GetDirectoryName(source.PersistencePath) ?? "";
            string fileName = Path.GetFileName(source.PersistencePath);
            if (string.Equals(fileName, "settings.json", StringComparison.OrdinalIgnoreCase))
                return dir;
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(source.PersistencePath) + "-data");
        }
        return Settings.DirectoryPath;
    }
}
