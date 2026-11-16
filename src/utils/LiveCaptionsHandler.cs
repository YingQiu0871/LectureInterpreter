using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;

        private const string WINDOW_CLASS = "LiveCaptionsDesktopWindow";
        private static readonly TimeSpan LAUNCH_TIMEOUT = TimeSpan.FromSeconds(30);

        public static AutomationElement LaunchLiveCaptions()
        {
            // Init
            captionsTextBlock = null;
            IsHidden = false;
            KillAllProcessesByPName(PROCESS_NAME);
            Process? process;
            try
            {
                process = Process.Start(PROCESS_NAME);
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "这台电脑上没有 Windows 实时辅助字幕（需要 Windows 11 22H2 或更新的系统）。", ex);
            }
            if (process == null)
                throw new InvalidOperationException("实时辅助字幕没有启动。");

            // Search for window: it appears a moment after the process starts.
            var waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var window = FindWindowByPId(process.Id);
                    if (window != null && window.Current.ClassName == WINDOW_CLASS)
                        return window;
                }
                catch (ElementNotAvailableException)
                {
                    // The window found went away again: keep looking.
                }
                if (process.HasExited)
                    throw new InvalidOperationException("实时辅助字幕启动后马上退出了。");
                if (waited.Elapsed > LAUNCH_TIMEOUT)
                    throw new InvalidOperationException(
                        $"等了 {LAUNCH_TIMEOUT.TotalSeconds:0} 秒，实时辅助字幕的窗口还是没有出现。");
                Thread.Sleep(30);
            }
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            // Search for process
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
            var process = Process.GetProcessById(processId);

            // Kill process. Not waited for without end: this runs under the lock LiveCaptions is started under
            // (see Translator.EnsureLiveCaptions), and when the program ends.
            process.Kill();
            process.WaitForExit(5000);
        }

        public static bool IsHidden { get; private set; } = false;

        // Extended styles that make the window transparent and click-through. LiveCaptions draws itself in a way
        // that ignores window transparency, so these styles are cleared if present rather than applied.
        private const int OLD_HIDDEN_STYLES = WindowsAPI.WS_EX_LAYERED | WindowsAPI.WS_EX_TRANSPARENT |
                                              WindowsAPI.WS_EX_NOACTIVATE;

        // A thin strip right above the taskbar, centered, in physical pixels.
        private static (int Left, int Top, int Width, int Height) DockRect(nint hWnd)
        {
            if (!WindowsAPI.SystemParametersInfo(WindowsAPI.SPI_GETWORKAREA, 0, out RECT area, 0))
                return (800, 600, 600, 200);
            uint dpi = 96;
            try
            {
                dpi = Math.Max(WindowsAPI.GetDpiForWindow(hWnd), 96);
            }
            catch (Exception)
            {
                // Best effort: fall back to the default DPI of 96.
            }
            int areaWidth = area.Right - area.Left;
            int width = Math.Min(areaWidth, Math.Max((int)(areaWidth * 0.6), (int)(700 * dpi / 96.0)));
            // LiveCaptions keeps its own minimum height if this is smaller.
            int height = (int)(100 * dpi / 96.0);
            int left = area.Left + (areaWidth - width) / 2;
            int top = area.Bottom - height;
            return (left, top, width, height);
        }

        // Where the user dragged the hidden strip to: kept when it is hidden again.
        private static RECT? hiddenRect = null;

        private static void Dock(nint hWnd)
        {
            if (hiddenRect is RECT rect && rect.Left > -10000 && rect.Top > -10000 &&
                rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 40)
            {
                WindowsAPI.MoveWindow(hWnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true);
                return;
            }
            var (left, top, width, height) = DockRect(hWnd);
            WindowsAPI.MoveWindow(hWnd, left, top, width, height, true);
        }

        private static void ClearOldStyles(nint hWnd, ref int exStyle)
        {
            if ((exStyle & WindowsAPI.WS_EX_LAYERED) != 0)
                WindowsAPI.SetLayeredWindowAttributes(hWnd, 0, 255, WindowsAPI.LWA_ALPHA);
            exStyle &= ~OLD_HIDDEN_STYLES;
        }

        // LiveCaptions stops updating its text when its window is minimized or off screen, so it can't really be
        // hidden. "Hidden" is a thin caption strip right above the taskbar, kept on top and out of the taskbar;
        // it can still be dragged elsewhere.
        public static void HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);
            ClearOldStyles(hWnd, ref exStyle);

            // With Alibaba Cloud recognition its text is not used at all: just get it out of the way. A running
            // class goes by the engine it started with, whatever the settings say by now: minimized, LiveCaptions
            // would stop writing the captions that class reads.
            var engine = ClassSession.IsRunning ? ClassSession.Engine : Translator.Setting?.Lecture.Engine;
            if (engine == RecognitionEngine.Aliyun)
            {
                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
                WindowsAPI.SetWindowPos(hWnd, WindowsAPI.HWND_NOTOPMOST, 0, 0, 0, 0,
                    WindowsAPI.SWP_NOMOVE | WindowsAPI.SWP_NOSIZE | WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_FRAMECHANGED);
                WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
                IsHidden = true;
                return;
            }

            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
            Dock(hWnd);
            WindowsAPI.SetWindowPos(hWnd, WindowsAPI.HWND_TOPMOST, 0, 0, 0, 0,
                WindowsAPI.SWP_NOMOVE | WindowsAPI.SWP_NOSIZE | WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_FRAMECHANGED);
            IsHidden = true;
        }

        // Shown: the same strip, as a normal window to change LiveCaptions' settings.
        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);
            ClearOldStyles(hWnd, ref exStyle);
            if (IsHidden && WindowsAPI.GetWindowRect(hWnd, out RECT current))
                hiddenRect = current;

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
            WindowsAPI.SetWindowPos(hWnd, WindowsAPI.HWND_NOTOPMOST, 0, 0, 0, 0,
                WindowsAPI.SWP_NOMOVE | WindowsAPI.SWP_NOSIZE | WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_FRAMECHANGED);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(hWnd);
            IsHidden = false;
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            // Placed on purpose while hidden, see `HideLiveCaptions`.
            if (IsHidden)
                return;
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);

            RECT rect;
            if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                throw new Exception("Unable to get the window rectangle of LiveCaptions!");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            int x = rect.Left;
            int y = rect.Top;

            bool isSuccess = true;
            if (x < 0 || y < 0 || width < 100 || height < 100)
                isSuccess = WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
            if (!isSuccess)
                throw new Exception("Failed to fix LiveCaptions!");
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
        }

        // LiveCaptions only creates its text area once it is set up, so allow it a few seconds.
        public static bool HasCaptionsTextBlock(AutomationElement window)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                captionsTextBlock ??= FindElementByAId(window, "CaptionsTextBlock");
                if (captionsTextBlock != null)
                    return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(AutomationElement window, string automationId)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            var settingsButton = FindElementByAId(window, "SettingsButton");
            if (settingsButton != null)
            {
                var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static void KillAllProcessesByPName(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return;
            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // It ended by itself in the meantime, or it is not ours to end: go on with the others.
                }
            }
        }
    }
}
