using System;
using System.IO;
using System.Text;
using System.Threading;

namespace EqlMetrics.Core
{
    /// <summary>
    /// Tails an EverQuest log file in real time. Opens the file with shared
    /// read/write access so it works while the game is actively writing to it.
    /// Emits complete lines only (buffers any partial trailing line), and
    /// recovers if the file is truncated or rotated.
    /// </summary>
    public sealed class LogTailer : IDisposable
    {
        private readonly string _path;
        private readonly bool _fromStart;
        private readonly Action<string> _onLine;
        private readonly int _pollMs;
        private Thread? _thread;
        private volatile bool _run;

        public LogTailer(string path, bool fromStart, Action<string> onLine, int pollMs = 250)
        {
            _path = path;
            _fromStart = fromStart;
            _onLine = onLine;
            _pollMs = pollMs;
        }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "EqlLogTailer" };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            _thread?.Join(1000);
            _thread = null;
        }

        private void Loop()
        {
            var decoder = Encoding.UTF8.GetDecoder();
            byte[] bytes = new byte[16384];
            char[] chars = new char[16384];
            var sb = new StringBuilder();

            while (_run)
            {
                try
                {
                    using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    long lastLen = _fromStart ? 0 : fs.Length;
                    fs.Position = lastLen;

                    while (_run)
                    {
                        // detect truncation / rotation
                        if (fs.Length < lastLen)
                        {
                            fs.Position = 0;
                            lastLen = 0;
                            sb.Clear();
                            decoder.Reset();
                        }

                        int n;
                        bool any = false;
                        while ((n = fs.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            any = true;
                            int cc = decoder.GetChars(bytes, 0, n, chars, 0);
                            sb.Append(chars, 0, cc);
                            lastLen = fs.Position;

                            int nl;
                            while ((nl = IndexOf(sb, '\n')) >= 0)
                            {
                                string line = sb.ToString(0, nl).TrimEnd('\r');
                                sb.Remove(0, nl + 1);
                                if (line.Length > 0)
                                {
                                    try { _onLine(line); } catch { /* never let one bad line kill the tail */ }
                                }
                            }
                        }

                        if (!any) Thread.Sleep(_pollMs);
                    }
                }
                catch (Exception)
                {
                    // file vanished or lock hiccup: wait and re-open
                    Thread.Sleep(500);
                }
            }
        }

        private static int IndexOf(StringBuilder sb, char c)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == c) return i;
            return -1;
        }

        public void Dispose() => Stop();
    }
}
