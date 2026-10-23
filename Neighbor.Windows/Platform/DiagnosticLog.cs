using System;
using System.IO;
using System.Text;

namespace Neighbor.Windows.Platform
{
    // Records why an optional feature failed. Only exception type, message and stack are written:
    // never keys, dictation text, audio or other user content.
    public static class DiagnosticLog
    {
        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsUpMyNeighbor", "diagnostics.log"); }
        }

        public static void Record(string path, string component, Exception error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string entry = DateTime.Now.ToString("s") + " " + component + " " + error.GetType().FullName + ": " + error.Message + Environment.NewLine + error + Environment.NewLine;
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
            catch
            {
                // Logging must never become a second failure while the app is already handling one.
            }
        }
    }
}
