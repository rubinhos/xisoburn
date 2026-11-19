using System;
using System.IO;
using System.Text;

namespace xISOBurn.Util
{
    /// <summary>
    /// Log to a file (LocalState) and, optionally, to the screen.
    /// The file lives in LocalAppData\xISOBurn_...\LocalState and can be downloaded from the Device Portal.
    ///
    /// The file is opened, appended to and closed on every line instead of being kept open. Reason
    /// (found on the console): while a file is held open, NTFS does not update the size shown in the
    /// folder listing, so the Device Portal showed the running session's log as 0 bytes and downloaded
    /// it empty. Closing after each write keeps the log readable while a long copy is running, and
    /// nothing is lost if the app is killed. The cost is negligible: lines are written at most a few
    /// times per second (one per failed attempt, plus progress every 5%).
    /// </summary>
    internal sealed class Logger : IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly Action<string> _ui;
        private readonly object _lock = new object();

        public string FilePath { get; private set; }

        public Logger(string folder, string prefix, Action<string> ui)
        {
            FilePath = Path.Combine(folder, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllText(FilePath, "", Utf8);
            _ui = ui;
        }

        public void Info(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n";
            lock (_lock)
            {
                try
                {
                    using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var bytes = Utf8.GetBytes(line);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (IOException)
                {
                    // E.g. the Device Portal is reading the file at that moment. Losing one line from
                    // the file is better than stopping a copy; the line still reaches the screen.
                }
            }
            if (_ui != null) _ui(message);
        }

        public void Section(string title)
        {
            Info("");
            Info("=== " + title + " ===");
        }

        public void Dispose()
        {
            // Nothing is kept open.
        }
    }
}
