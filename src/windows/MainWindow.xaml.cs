using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;
using Button = Wpf.Ui.Controls.Button;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        public BilingualWindow? BilingualWindow { get; set; } = null;
        private static bool bilingualHintShown = false;
        private const double DEFAULT_WIDTH = 900;
        private const double DEFAULT_HEIGHT = 560;

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int HOTKEY_END_SECTION = 0x5301;
        private const int HOTKEY_TOGGLE_SPEECH = 0x5302;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_SETTINGCHANGE = 0x001A;
        private static ApplicationTheme appliedTheme = ApplicationTheme.Unknown;

        public MainWindow()
        {
            InitializeComponent();
            ApplySavedTheme();
            SetUiScale(Translator.Setting.MainWindow.UiScale);

            SourceInitialized += (s, e) =>
            {
                // (Not for the check of the pages: the keys belong to the copy that is in use.)
                if (!UiCheck.Active)
                    RegisterHotKeys();
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ThemeHook);
            };
            FloatLock.Changed += OnFloatLockChanged;
            Closing += MainWindow_Closing;
            Closed += (s, e) =>
            {
                UnregisterHotKeys();
                FloatLock.Changed -= OnFloatLockChanged;
                BilingualWindow?.Close();
            };
            Speaker.MutedChanged += muted => Dispatcher.InvokeAsync(() => ShowSpeakerState(muted));
            ClassSession.StateChanged += () => Dispatcher.InvokeAsync(ShowSessionState);

            Loaded += (s, e) => LoadLogo();
            Loaded += (s, e) =>
            {
                // Applied once more on the loaded window, as a manual theme switch does (that is known to put a
                // wrong text colour right); the constructor call only avoids a flash of the default theme.
                ApplySavedTheme();
                RootNavigation.Navigate(typeof(CaptionPage));
                CheckForFirstUse();
            };

            // Where it was the last time, on whichever screen; in the middle of the main screen when that place
            // is gone (a screen was unplugged) or nothing usable was saved.
            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            if (WindowHandler.IsOnScreen(windowState))
                WindowHandler.RestoreState(this, windowState);
            else
            {
                WindowHandler.RestoreState(this, new Rect(
                    (SystemParameters.PrimaryScreenWidth - DEFAULT_WIDTH) / 2,
                    (SystemParameters.PrimaryScreenHeight - DEFAULT_HEIGHT) / 2, DEFAULT_WIDTH, DEFAULT_HEIGHT));
            }
            // A saved window shorter than the minimum height (e.g. a thin caption strip) gets the default height.
            if (Height < MinHeight)
                Height = DEFAULT_HEIGHT;
            KeepOnScreen();

            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowSpeakerState(Speaker.Muted);
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        // The theme the saved setting asks for. "跟随系统" reads the Windows app mode (AppsUseLightTheme) directly:
        // WPF-UI's own detection goes by the name of the theme file first and takes the stock "aero" theme for
        // light even when the app mode is dark.
        private static ApplicationTheme ResolveSavedTheme()
        {
            string theme = Translator.Setting?.MainWindow.Theme ?? "System";
            if (theme == "Dark")
                return ApplicationTheme.Dark;
            if (theme == "Light")
                return ApplicationTheme.Light;
            if (SystemParameters.HighContrast)
                return ApplicationTheme.HighContrast;
            try
            {
                object? value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int light && light == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }
            catch (Exception)
            {
                return ApplicationTheme.Light;
            }
        }

        // Theme resources are application-wide, so applying once covers every window.
        public static void ApplySavedTheme()
        {
            appliedTheme = ResolveSavedTheme();
            ApplicationThemeManager.Apply(appliedTheme);
        }

        // WM_SETTINGCHANGE arrives (several times) when the Windows colour mode changes: with "跟随系统" follow it,
        // but only re-apply when the result really differs.
        private IntPtr ThemeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SETTINGCHANGE && (Translator.Setting?.MainWindow.Theme ?? "System") == "System")
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if ((Translator.Setting?.MainWindow.Theme ?? "System") == "System" &&
                        ResolveSavedTheme() != appliedTheme)
                        ApplySavedTheme();
                });
            }
            return IntPtr.Zero;
        }

        public void SetUiScale(double scale)
        {
            UiScaleTransform.ScaleX = scale;
            UiScaleTransform.ScaleY = scale;
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            var state = Translator.Setting.MainWindow;
            state.UiScale = Math.Round(state.UiScale + Math.Sign(e.Delta) * 0.05, 2);
            SetUiScale(state.UiScale);
            e.Handled = true;
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!this.Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (OverlayWindow != null)
            {
                OverlayWindow.Close();
                return;
            }

            var overlay = new OverlayWindow();
            overlay.SizeChanged += (s, args) => WindowHandler.SaveState(overlay, Translator.Setting);
            overlay.LocationChanged += (s, args) => WindowHandler.SaveState(overlay, Translator.Setting);
            // However it is closed (the menu, Alt+F4): the menu and the buttons follow.
            overlay.Closed += (s, args) =>
            {
                if (ReferenceEquals(OverlayWindow, overlay))
                    OverlayWindow = null;
                ShowOverlayState();
            };

            var windowState = WindowHandler.LoadState(overlay, Translator.Setting);
            if (WindowHandler.IsOnScreen(windowState))
            {
                // A saved width of exactly 650 means the former default: use the current, smaller default.
                if (Math.Abs(windowState.Width - 650) < 1)
                    windowState.Width = 480;
                WindowHandler.RestoreState(overlay, windowState);
            }
            else
            {
                WindowHandler.RestoreState(overlay, new Rect(
                    (SystemParameters.PrimaryScreenWidth - 480) / 2,
                    SystemParameters.PrimaryScreenHeight * 5 / 6 - 100, 480, 100));
            }

            OverlayWindow = overlay;
            overlay.Show();
            ShowOverlayState();
        }

        private void ShowOverlayState()
        {
            bool open = OverlayWindow != null;
            if (OverlayModeButton.Icon is SymbolIcon icon)
            {
                icon.Symbol = open ? SymbolRegular.ClosedCaption24 : SymbolRegular.ClosedCaptionOff24;
                icon.Filled = open;
            }
            UpdateFloatMenuButton();
        }

        private void BilingualModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (BilingualWindow != null)
            {
                BilingualWindow.Close();
                return;
            }

            BilingualWindow = new BilingualWindow();
            BilingualWindow.Closed += (s, args) =>
            {
                BilingualWindow = null;
                if (BilingualModeButton.Icon is SymbolIcon icon)
                    icon.Filled = false;
                UpdateFloatMenuButton();
            };
            if (BilingualModeButton.Icon is SymbolIcon symbolIcon)
                symbolIcon.Filled = true;
            BilingualWindow.Show();
            UpdateFloatMenuButton();

            if (!bilingualHintShown && Translator.Setting.Lecture.Engine == models.RecognitionEngine.LiveCaptions)
            {
                bilingualHintShown = true;
                SnackbarHost.Show("双语浮窗按阿里云百炼引擎设计",
                    "系统字幕引擎下也能用，但原文更新可能较慢。", SnackbarType.Info, timeout: 3, closeButton: true);
            }
        }

        private void LogOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (Translator.LogOnlyFlag)
            {
                Translator.LogOnlyFlag = false;
                symbolIcon.Filled = false;
            }
            else
            {
                Translator.LogOnlyFlag = true;
                symbolIcon.Filled = true;
            }

            if (button != null)
                button.Appearance = Translator.LogOnlyFlag ? ControlAppearance.Caution : ControlAppearance.Secondary;

            Translator.ClearContexts();
        }

        private void FloatMenuButton_Click(object sender, RoutedEventArgs e)
        {
            FloatMenu.PlacementTarget = FloatMenuButton;
            FloatMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            FloatMenu.IsOpen = true;
        }

        private void FloatMenu_Opened(object sender, RoutedEventArgs e)
        {
            OverlayMenuItem.IsChecked = OverlayWindow != null;
            BilingualMenuItem.IsChecked = BilingualWindow != null;
            LockMenuItem.IsChecked = FloatLock.Locked;
            UpdateFloatMenuButton();
        }

        // The 浮窗 button looks "on" while a floating window is open.
        private void UpdateFloatMenuButton()
        {
            FloatMenuButton.Appearance = OverlayWindow != null || BilingualWindow != null
                ? ControlAppearance.Primary
                : ControlAppearance.Secondary;
        }

        private void OverlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OverlayModeButton_Click(OverlayModeButton, e);
        }

        private void BilingualMenuItem_Click(object sender, RoutedEventArgs e)
        {
            BilingualModeButton_Click(BilingualModeButton, e);
        }

        // IsCheckable has already toggled the item, so take the state from it.
        private void LockMenuItem_Click(object sender, RoutedEventArgs e)
        {
            FloatLock.Locked = LockMenuItem.IsChecked;
        }

        private void OnFloatLockChanged()
        {
            LockMenuItem.IsChecked = FloatLock.Locked;
            if (FloatLock.Locked)
            {
                SnackbarHost.Show("浮窗已锁定", "鼠标可以点到下面的页面；在「浮窗」菜单里取消锁定。",
                    SnackbarType.Info, timeout: 3, closeButton: true);
            }
        }

        // The app icon is embedded in the exe (ApplicationIcon); show it next to the title.
        private void LoadLogo()
        {
            try
            {
                // The app icon is embedded as a WPF resource; pick its largest frame so it stays sharp when scaled.
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri("pack://application:,,,/LectureInterpreter;component/src/LectureInterpreter.ico"),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                LogoImage.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                LogoImage.Visibility = Visibility.Visible;
                LogoFallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
            }
        }

        private void EndSectionButton_Click(object sender, RoutedEventArgs e)
        {
            EndSection();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassSession.IsRunning)
                await StopAndSave();
            else
            {
                RootNavigation.Navigate(typeof(CaptionPage));
                await ClassSession.Start();
            }
        }

        // The save flow that follows "停止", while it runs (it asks for a name, and waits for the last summary).
        private Task? saving;
        private bool closeAsked = false;
        private bool closeConfirmed = false;

        private async Task StopAndSave()
        {
            long lectureId = ClassSession.CurrentLectureId;
            ClassSession.Stop();
            var task = LectureSaving.SaveAfterStop(lectureId);
            saving = task;
            try
            {
                await task;
            }
            finally
            {
                if (ReferenceEquals(saving, task))
                    saving = null;
            }
        }

        // Closing the window in the middle of a class would end the program with the class unsaved: the sentences
        // still being translated, the summary of the last section and the Markdown file would be missing. So the
        // class is stopped and saved first, as "停止" does, and the window closes when that is done.
        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (closeConfirmed || (!ClassSession.IsRunning && !ClassSession.IsStarting && saving == null))
                return;
            e.Cancel = true;
            // Closing again while the question is open, or while the class is being saved, changes nothing.
            if (closeAsked)
                return;
            closeAsked = true;
            // No new class from here on (see ShowSessionState).
            ShowSessionState();
            try
            {
                if (ClassSession.IsRunning || ClassSession.IsStarting)
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    if (!await LectureDialogs.Confirm("正在上课", "退出前会先停止并保存本节课。", "停止并退出"))
                        return;
                    // A class that was only just starting has to be there before it can be stopped (or has
                    // failed to start, and then there is nothing to save).
                    if (ClassSession.IsStarting)
                        SnackbarHost.Show("这节课还在启动", "启动完成后会停止、保存并退出。", SnackbarType.Info, timeout: 3);
                    while (ClassSession.IsStarting)
                        await Task.Delay(100);
                    // (Not running now: it did not start after all.)
                    if (ClassSession.IsRunning)
                        await StopAndSave();
                }
                if (saving is Task pending)
                {
                    SnackbarHost.Show("正在保存这节课", "保存完会自动退出。", SnackbarType.Info, timeout: 3);
                    await pending;
                }
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the window can still be closed.
                ErrorLog.Write("MainWindow.Closing", ex);
            }
            finally
            {
                closeAsked = false;
                ShowSessionState();
            }
            closeConfirmed = true;
            // Not from inside the closing that was cancelled above, which WPF refuses: with nothing left to wait
            // for, this point is reached without having left it.
            _ = Dispatcher.InvokeAsync(Close);
        }

        private void ShowSessionState()
        {
            // Not while the class is being saved for the exit either: the window closes when that is done, and a
            // class started in between would end with it, unsaved.
            StartButton.IsEnabled = !ClassSession.IsStarting && !closeAsked;
            StartButton.Content = ClassSession.IsStarting ? "启动中…" : ClassSession.IsRunning ? "停止" : "开始";
            // Red only where it says "停止": a class counts as running a moment before it has finished starting.
            bool stops = ClassSession.IsRunning && !ClassSession.IsStarting;
            StartButton.Appearance = stops ? ControlAppearance.Danger : ControlAppearance.Primary;
            if (StartButton.Icon is SymbolIcon icon)
                icon.Symbol = stops ? SymbolRegular.Stop16 : SymbolRegular.Play16;
        }

        private void SpeechSwitch_Click(object sender, RoutedEventArgs e)
        {
            Speaker.Muted = SpeechSwitch.IsChecked != true;
        }

        public static void EndSection()
        {
            Summarizer.RequestEndSection();
            SnackbarHost.Show("正在总结本节……", "", SnackbarType.Info, timeout: 1);
        }

        private void ShowSpeakerState(bool muted)
        {
            SpeechSwitch.IsChecked = !muted;
        }

        private void RegisterHotKeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HotKeyHook);
            // Ctrl+Alt+S: end the current section; Ctrl+Alt+M: mute / unmute speech.
            bool ok = RegisterHotKey(handle, HOTKEY_END_SECTION, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x53);
            ok &= RegisterHotKey(handle, HOTKEY_TOGGLE_SPEECH, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4D);
            if (!ok)
                SnackbarHost.Show("[WARNING] 快捷键注册失败。",
                    "Ctrl+Alt+S / Ctrl+Alt+M 可能被其他程序占用。", SnackbarType.Warning,
                    timeout: 3, closeButton: true);
        }

        private void UnregisterHotKeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_END_SECTION);
            UnregisterHotKey(handle, HOTKEY_TOGGLE_SPEECH);
        }

        private IntPtr HotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY)
                return IntPtr.Zero;

            switch (wParam.ToInt32())
            {
                case HOTKEY_END_SECTION:
                    EndSection();
                    handled = true;
                    break;
                case HOTKEY_TOGGLE_SPEECH:
                    Speaker.Muted = !Speaker.Muted;
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private void CaptionLogButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.MainWindow.CaptionLogEnabled = !Translator.Setting.MainWindow.CaptionLogEnabled;
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            var window = sender as Window;
            WindowHandler.SaveState(window, Translator.Setting);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindow_LocationChanged(sender, e);
        }

        public void ToggleTopmost(bool enabled)
        {
            var button = TopmostButton as Button;
            var symbolIcon = button?.Icon as SymbolIcon;
            symbolIcon.Filled = enabled;
            if (button != null)
                button.Appearance = enabled ? ControlAppearance.Primary : ControlAppearance.Transparent;
            this.Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private void CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            RootNavigation.Navigate(typeof(LecturePage));
            try
            {
                if (Translator.Window != null)
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
            }
            catch (Exception)
            {
                // Live Captions was closed in the meantime: the welcome window explains how to show it.
            }

            Dispatcher.InvokeAsync(() =>
            {
                var welcomeWindow = new WelcomeWindow
                {
                    Owner = this
                };
                welcomeWindow.Show();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public void ShowLogCard(bool enabled)
        {
            if (CaptionLogButton.Icon is SymbolIcon icon)
            {
                if (enabled)
                    icon.Symbol = SymbolRegular.History24;
                else
                    icon.Symbol = SymbolRegular.HistoryDismiss24;
            }
        }

        // Only grows the window when a page needs more room. Shrinking it to a thin caption strip
        // would hide most of the navigation; the overlay window serves that purpose.
        public void AutoHeightAdjust(int minHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
            {
                Height = minHeight;
                KeepOnScreen();
            }
        }

        private void KeepOnScreen()
        {
            // The work area of the main screen is the one known here: a window on another screen stays as it is.
            var workArea = SystemParameters.WorkArea;
            if (Left < workArea.Left - 50 || Left >= workArea.Right || Top < workArea.Top - 50 || Top >= workArea.Bottom)
                return;
            double height = Math.Max(Height, MinHeight);
            if (height > workArea.Height)
                Height = height = workArea.Height;
            if (Top + height > workArea.Bottom)
                Top = Math.Max(workArea.Top, workArea.Bottom - height);
        }
    }
}
