using System;
using System.IO;

namespace Nomi
{
    // Small rolling log in %LOCALAPPDATA%\Nomi\nomi.log (kept under ~1 MB).
    static class Log
    {
        static readonly object gate = new object();
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomi");
        static readonly string FilePath = Path.Combine(Folder, "nomi.log");

        public static void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
                catch { }
            }
        }

        public static void Error(string where, Exception e)
        {
            Write(where + ": " + e);
        }
    }
}
