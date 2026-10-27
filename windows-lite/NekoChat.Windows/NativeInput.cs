using System.Runtime.InteropServices;
using System.Windows;

namespace NekoChat.Windows;

internal static class NativeInput
{
    internal static string Copy(string text, string reason = "手動複製")
    {
        try
        {
            Clipboard.SetText(text);
            return $"{reason}；已複製到剪貼簿。";
        }
        catch (COMException)
        {
            return "剪貼簿忙碌中，請稍後重試。";
        }
    }
}
