using System;
using System.IO;
using System.Text;

namespace xISOBurn.Util
{
    /// <summary>
    /// Log to a file (LocalState) and, optionally, to the screen.
    /// The file lives in LocalAppData\xISOBurn_...\LocalState and can be downloaded from the Device Portal.
    /// </summary>
    internal sealed class Logger : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Action<string> _ui;
        private readonly object _lock = new object();

        public string FilePath { get; private set; }

        public Logger(string folder, string prefix, Action<string> ui)
        {
            FilePath = Path.Combine(folder, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _ui = ui;
        }

        public void Info(string message)
        {
            lock (_lock)
                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
            if (_ui != null) _ui(message);
        }

        public void Section(string title)
        {
            Info("");
            Info("=== " + title + " ===");
        }

        public void Dispose()
        {
            lock (_lock)
                _writer.Dispose();
        }
    }
}
