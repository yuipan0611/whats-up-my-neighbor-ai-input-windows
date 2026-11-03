using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    }

    public static string ResolveRealPath(string rawPath)
    {
        AssertSafePathFormat(rawPath);

        string fullPath = Path.GetFullPath(rawPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fullPath = ExpandWindowsShortName(fullPath);
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new DirectoryNotFoundException($"路徑根目錄不存在或無法解析：'{fullPath}'。");
        }

        // 找尋現存的最深祖先目錄
        string? existing = fullPath;
        var missingSegments = new Stack<string>();
        while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
        {
            if (IsSymbolicLink(existing))
            {
                throw new InvalidOperationException($"符號連結指向不存在的目標（懸空符號連結）：'{existing}'。");
            }
            string seg = Path.GetFileName(existing);
            if (string.IsNullOrEmpty(seg))
            {
                break;
            }
            missingSegments.Push(seg);
            existing = Path.GetDirectoryName(existing);
        }

        string resolvedExisting;
        if (!string.IsNullOrEmpty(existing) && (Directory.Exists(existing) || File.Exists(existing)))
        {
            resolvedExisting = ResolveExistingPathViaOs(existing);
        }
        else
        {
            resolvedExisting = fullPath;
        }

        // 接回尚未建立的尾端 segments
        string combined = resolvedExisting;
        while (missingSegments.Count > 0)
        {
            combined = Path.Combine(combined, missingSegments.Pop());
        }

        return Path.GetFullPath(combined).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) != 0;
            }
            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null) return true;
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null) return true;
        }
        catch { }
        return false;
    }

    private static string ResolveExistingPathViaOs(string path)
    {
        if (IsSymbolicLink(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException($"符號連結指向不存在的目標（懸空符號連結）：'{path}'。");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ResolveWindowsFinalPath(path);
        }
        else
        {
            return ResolvePosixRealPath(path);
        }
    }

    private static string ResolvePosixRealPath(string path)
    {
        IntPtr ptr = posix_realpath(path, IntPtr.Zero);
        if (ptr != IntPtr.Zero)
        {
            try
            {
                string? res = Marshal.PtrToStringUTF8(ptr);
                if (!string.IsNullOrEmpty(res))
                {
                    return res;
                }
            }
            finally
            {
                posix_free(ptr);
            }
        }
        else
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 62 || errno == 40)
            {
                throw new InvalidOperationException($"偵測到符號連結迴圈：'{path}'。");
            }
            if (IsSymbolicLink(path))
            {
                throw new InvalidOperationException($"符號連結指向不存在的目標（懸空符號連結）：'{path}'。");
            }
        }

        return ResolveViaDotNetLinkTarget(path);
    }

    private static string ResolveWindowsFinalPath(string path)
    {
        try
        {
            const uint FILE_READ_ATTRIBUTES = 0x0080;
            const uint FILE_SHARE_ALL = 1 | 2 | 4; // READ | WRITE | DELETE
            const uint OPEN_EXISTING = 3;
            const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

            using var handle = CreateFile(
                path,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_ALL,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                var sb = new StringBuilder(1024);
                uint res = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (res > 0 && res < sb.Capacity)
                {
                    string winPath = sb.ToString();
                    if (winPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        return @"\\" + winPath[8..];
                    }
                    if (winPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    {
                        return winPath[4..];
                    }
                    return winPath;
                }
            }
        }
        catch { }

        return ResolveViaDotNetLinkTarget(path);
    }

    private static string ResolveViaDotNetLinkTarget(string path)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileSystemInfo fsi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        int depth = 0;
        while (depth++ < 32)
        {
            string current = fsi.FullName;
            if (!visited.Add(current))
            {
                throw new InvalidOperationException($"偵測到符號連結迴圈：'{current}'。");
            }

            var target = fsi.ResolveLinkTarget(returnFinalTarget: false);
            if (target is null) break;

            if (!target.Exists)
            {
                throw new InvalidOperationException($"符號連結指向不存在的目標（懸空符號連結）：'{current}' -> '{target.FullName}'。");
            }
            fsi = target;
        }

        if (depth >= 32)
        {
            throw new InvalidOperationException($"符號連結解析超過最大深度限制（可能存在迴圈）：'{path}'。");
        }

        return fsi.FullName;
    }

    private static string ExpandWindowsShortName(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return path;
        }

        try
        {
            var sb = new StringBuilder(1024);
            uint res = GetLongPathName(path, sb, (uint)sb.Capacity);
            if (res > 0 && res < sb.Capacity)
            {
                return sb.ToString();
            }
        }
        catch { }
        return path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(
        string lpszShortPath,
        [Out] StringBuilder lpszLongPath,
        uint cchBuffer);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr posix_realpath(string path, IntPtr resolved_path);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void posix_free(IntPtr ptr);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

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
