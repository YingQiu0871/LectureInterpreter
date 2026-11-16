using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        // The title of MainWindow.xaml.
        private const string MAIN_WINDOW_TITLE = "课堂同传助手";

        // Held for as long as the program runs: a second copy would close the first one's Live Captions and both
        // would write the same settings and history files.
        private static Mutex? instanceMutex;

        App()
        {
            if (!IsFirstInstance())
            {
                // Without a window to bring to the front the other copy is only just starting, or it is closing
                // (it tidies up for a moment after its window is gone). When it has ended shortly after, this
                // copy runs: starting the program again right after closing it would otherwise do nothing at all.
                bool shown = ActivateRunningInstance();
                if (shown || !OtherInstanceEnded(TimeSpan.FromSeconds(5)))
                {
                    if (!shown)
                        ActivateRunningInstance();
                    Environment.Exit(0);
                }
            }
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                ErrorLog.Write("Fatal", e.ExceptionObject?.ToString() ?? "unknown error");
                Setting.Flush();
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                ErrorLog.Write("Task", e.Exception);
                e.SetObserved();
            };

            try
            {
                // Settings and history are kept in the user's data folder (see AppPaths), so do not depend on how
                // the program was started.
                Directory.SetCurrentDirectory(AppPaths.DataDirectory);
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                Translator.Setting?.Save();
            }
            catch (Exception ex)
            {
                // Without its settings the program cannot run; say why instead of vanishing.
                var cause = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                ErrorLog.Write("Startup", ex);
                MessageBox.Show($"课堂同传助手无法启动：{cause.Message}\n\n设置文件：{AppPaths.SettingFile}\n" +
                                "如果它被别的程序占用，请关掉那个程序后再试。",
                    "课堂同传助手", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }

            Task.Run(() => Translator.SyncLoop());
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
            Task.Run(() => Summarizer.SummaryLoop());
            Task.Run(() => Refiner.RefineLoop());
            Speaker.Start();
            UpdateChecker.StartAutoCheck();
        }

        private static bool IsFirstInstance()
        {
            try
            {
                instanceMutex = new Mutex(true, @"Local\LectureInterpreter.SingleInstance", out bool createdNew);
                return createdNew;
            }
            catch (Exception)
            {
                // Better two copies than none.
                return true;
            }
        }

        // Waits for the copy that holds the mutex to end. True when it has: the mutex is then held by this copy.
        private static bool OtherInstanceEnded(TimeSpan timeout)
        {
            try
            {
                return instanceMutex != null && instanceMutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing the mutex, as this program always does: that hands it over.
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Brings the main window of the copy that is already running to the front. False if it has none.
        private static bool ActivateRunningInstance()
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                var others = Process.GetProcessesByName(current.ProcessName)
                    .Select(process => process.Id).Where(id => id != current.Id).ToHashSet();
                // By title and owner: other windows may carry the same title (a folder of that name in Explorer),
                // and the process has more windows than the main one (the floating captions).
                nint window = 0;
                while ((window = WindowsAPI.FindWindowEx(0, window, null, MAIN_WINDOW_TITLE)) != 0)
                {
                    WindowsAPI.GetWindowThreadProcessId(window, out int owner);
                    if (!others.Contains(owner))
                        continue;
                    if (WindowsAPI.IsIconic(window))
                        WindowsAPI.ShowWindow(window, WindowsAPI.SW_RESTORE);
                    WindowsAPI.SetForegroundWindow(window);
                    return true;
                }
            }
            catch (Exception)
            {
                // Best effort: the running copy stays where it is.
            }
            return false;
        }

        // An error in one handler should not end a class that is being recorded: the program goes on. Errors before
        // the main window is up, or one after another, are left to end it.
        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            bool survives = ErrorLog.SurviveUiError(e.Exception);
            if (survives && MainWindow?.IsLoaded == true)
                e.Handled = true;
            else
                Setting.Flush();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            try
            {
                var window = Translator.Window;
                if (window != null)
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(window);
                    LiveCaptionsHandler.KillLiveCaptions(window);
                }
            }
            catch (Exception)
            {
                // Live Captions was closed already.
            }
        }
    }
}
