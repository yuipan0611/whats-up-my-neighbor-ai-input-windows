using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NekoChat.Core;

namespace NekoChat.Windows;

internal sealed record UiTestOptions(string DataDirectory, int Port, string MutexName)
{
    public const string DataArg = "--ui-test-data";
    public const string PortArg = "--ui-test-port";
    public const int DefaultUiTestPort = 8765;

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

        AssertSafePathFormat(dataDir);
        string fullPath = Path.GetFullPath(dataDir);
        AssertSafeDataDirectory(fullPath);

        int resolvedPort = port ?? DefaultUiTestPort;
        string mutexName = DeriveMutexName(fullPath);

        return new UiTestOptions(fullPath, resolvedPort, mutexName);
    }

    public static void AssertSafePathFormat(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("路徑不可為空。");

        // 1. 阻擋 extended-length path prefix (\\?\)
        if (rawPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            rawPath.StartsWith(@"//?/", StringComparison.OrdinalIgnoreCase) ||
            rawPath.Contains(@"\\?\") ||
            rawPath.Contains(@"//?/"))
        {
            throw new InvalidOperationException("不允許使用 extended-length path prefix (\\\\?\\)。");
        }

        // 2. 阻擋 8.3 短檔名特徵 ('~')
        var rawSegments = rawPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in rawSegments)
        {
            if (seg.Contains('~'))
            {
                throw new InvalidOperationException($"不允許使用包含 8.3 短檔名特徵 ('~') 的路徑：'{seg}'。");
            }
        }
    }

    public static string ResolveRealPath(string rawPath)
    {
        AssertSafePathFormat(rawPath);

        string fullPath = Path.GetFullPath(rawPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new DirectoryNotFoundException($"路徑根目錄不存在或無法解析：'{fullPath}'。");
        }

        string current = root;
        string remainder = fullPath[root.Length..];
        var segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        bool stillExisting = true;
        foreach (var seg in segments)
        {
            if (stillExisting)
            {
                string candidate = Path.Combine(current, seg);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    try
                    {
                        FileSystemInfo fsi = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
                        int depth = 0;
                        while (depth++ < 32)
                        {
                            var target = fsi.ResolveLinkTarget(returnFinalTarget: true);
                            if (target is null) break;
                            fsi = target;
                        }
                        current = fsi.FullName;
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        throw new InvalidOperationException($"解析路徑 symlink 失敗：{ex.Message}", ex);
                    }
                }
                else
                {
                    stillExisting = false;
                    current = Path.Combine(current, seg);
                }
            }
            else
            {
                current = Path.Combine(current, seg);
            }
        }

        return Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static void AssertSafeDataDirectory(string dataDirectory)
    {
        string resolvedTarget = ResolveRealPath(dataDirectory);
        string resolvedBase = ResolveRealPath(Settings.DirectoryPath);

        if (IsInsideOrEqual(resolvedTarget, resolvedBase))
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
        string resolved = ResolveRealPath(dataDirectory);
        string normalized = resolved.ToUpperInvariant();
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
