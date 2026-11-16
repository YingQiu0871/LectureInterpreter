using System.IO;

namespace LiveCaptionsTranslator.utils
{
    // A plain text log of unexpected errors, kept next to the settings (error.log). Errors in background work
    // would otherwise vanish without a trace, and a user reporting a problem has nothing to send.
    public static class ErrorLog
    {
        private const string FILE_NAME = "error.log";
        private const long MAX_BYTES = 512 * 1024;

        private static readonly object gate = new();
        private static string? lastEntry;

        public static string FilePath => Path.Combine(AppPaths.DataDirectory, FILE_NAME);

        // Errors nobody caught on the UI thread, to tell a single one from an endless series.
        private static readonly Queue<DateTime> uiErrors = new();

        // An exception nobody caught on the UI thread: logs and shows it. Returns true when the program can go on
        // (one failed handler should not end a class that is being recorded), false for errors one after another.
        public static bool SurviveUiError(Exception ex)
        {
            Write("UI", ex);
            var now = DateTime.Now;
            int count;
            lock (uiErrors)
            {
                uiErrors.Enqueue(now);
                while (now - uiErrors.Peek() > TimeSpan.FromSeconds(10))
                    uiErrors.Dequeue();
                count = uiErrors.Count;
            }
            if (count > 5)
                return false;
            SnackbarHost.Show("[ERROR] 程序出了点问题。", $"{ex.Message}（详情已记到 {FilePath}）",
                SnackbarType.Error, timeout: 6, closeButton: true);
            return true;
        }

        public static void Write(string where, Exception ex)
        {
            Write(where, ex.ToString());
        }

        public static void Write(string where, string message)
        {
            try
            {
                lock (gate)
                {
                    // A loop that fails the same way again and again writes one line, not thousands.
                    string entry = $"{where}: {message}";
                    if (entry == lastEntry)
                        return;
                    lastEntry = entry;

                    string path = FilePath;
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length > MAX_BYTES)
                        File.Move(path, path + ".old", true);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {entry}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // A log that cannot be written must not cause another error.
            }
        }
    }
}
