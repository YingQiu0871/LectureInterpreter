using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : Window
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        public BilingualWindow? BilingualWindow { get; set; } = null;
        private const double DEFAULT_WIDTH = 900;
        private const double DEFAULT_HEIGHT = 560;

        // The labels of the navigation, in order. The caption and about pages are kept; the others are rebuilt
        // every time (they read their data when shown).
        private static readonly (string Name, Func<Control> Create, bool Cache)[] PAGES =
        {
            ("字幕", () => new CaptionPage(), true),
            ("设置", () => new LecturePage(), false),
            ("时间线", () => new TimelinePage(), false),
            ("历史", () => new HistoryPage(), false),
            ("关于", () => new InfoPage(), true),
        };

        private readonly Dictionary<string, Control> cachedPages = new();
        private readonly DispatcherTimer toastTimer = new();

        public MainWindow()
        {
            InitializeComponent();

            NavList.ItemsSource = PAGES.Select(page => page.Name).ToList();
            toastTimer.Tick += (s, e) => HideToast();

            // The "浮窗" button lights up while one of the two floating windows is on.
            OverlayModeButton.IsCheckedChanged += (s, e) => ShowFloatState();
            BilingualModeButton.IsCheckedChanged += (s, e) => ShowFloatState();
            FloatLock.Changed += OnFloatLockChanged;

            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt),
                Command = new ActionCommand(EndSection),
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.M, KeyModifiers.Control | KeyModifiers.Alt),
                Command = new ActionCommand(() => Speaker.Muted = !Speaker.Muted),
            });

            Speaker.MutedChanged += muted => Dispatcher.UIThread.Post(() => ShowSpeakerState(muted));
            ClassSession.StateChanged += () => Dispatcher.UIThread.Post(ShowSessionState);
            SnackbarHost.Shown += ShowToast;

            Opened += (s, e) =>
            {
                Navigate("字幕");
                CheckForFirstUse();
            };
            Closing += MainWindow_Closing;
            Closed += (s, e) =>
            {
                SnackbarHost.Shown -= ShowToast;
                FloatLock.Changed -= OnFloatLockChanged;
                OverlayWindow?.Close();
                BilingualWindow?.Close();
            };

            WindowBounds.Restore(this, "MainWindow", new Size(DEFAULT_WIDTH, DEFAULT_HEIGHT));
            // A height below the minimum falls back to the default.
            if (Height < MinHeight)
                Height = DEFAULT_HEIGHT;
            WindowBounds.Track(this, "MainWindow");

            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowSpeakerState(Speaker.Muted);
            ShowSessionState();
            string microphone = DesktopSettings.MicrophoneId;
            CloudAsr.MicrophoneSource = string.IsNullOrEmpty(microphone) ? null : microphone;
        }

        private void ShowFloatState()
        {
            FloatButton.Classes.Set("on", OverlayModeButton.IsChecked == true || BilingualModeButton.IsChecked == true);
        }

        public void Navigate(string name)
        {
            int index = Array.FindIndex(PAGES, page => page.Name == name);
            if (index < 0)
                return;
            if (NavList.SelectedIndex == index)
                ShowPage(index);
            else
                NavList.SelectedIndex = index;
        }

        private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedIndex >= 0)
                ShowPage(NavList.SelectedIndex);
        }

        private void ShowPage(int index)
        {
            var (name, create, cache) = PAGES[index];
            Control page;
            if (!cache)
                page = create();
            else if (!cachedPages.TryGetValue(name, out page!))
                cachedPages[name] = page = create();
            PageHost.Content = page;
        }

        private void TopmostButton_Click(object? sender, RoutedEventArgs e)
        {
            ToggleTopmost(!Topmost);
        }

        private void OverlayModeButton_Click(object? sender, RoutedEventArgs e)
        {
            if (OverlayWindow != null)
            {
                OverlayWindow.Close();
                return;
            }

            var overlay = new OverlayWindow();
            // However it is closed (the menu, the window manager): the menu follows.
            overlay.Closed += (s, args) =>
            {
                if (ReferenceEquals(OverlayWindow, overlay))
                    OverlayWindow = null;
                OverlayModeButton.IsChecked = false;
            };
            OverlayWindow = overlay;
            OverlayModeButton.IsChecked = true;
            overlay.Show();
        }

        private void BilingualModeButton_Click(object? sender, RoutedEventArgs e)
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
                BilingualModeButton.IsChecked = false;
            };
            BilingualModeButton.IsChecked = true;
            BilingualWindow.Show();
        }

        private void LockModeButton_Click(object? sender, RoutedEventArgs e)
        {
            FloatLock.Locked = LockModeButton.IsChecked == true;
        }

        private void OnFloatLockChanged()
        {
            LockModeButton.IsChecked = FloatLock.Locked;
            if (FloatLock.Locked)
            {
                SnackbarHost.Show("浮窗已锁定", "鼠标可以点到下面的页面；在「浮窗」菜单里取消锁定。",
                    SnackbarType.Info, timeout: 3, closeButton: true);
            }
        }

        private void LogOnlyButton_Click(object? sender, RoutedEventArgs e)
        {
            Translator.LogOnlyFlag = !Translator.LogOnlyFlag;
            LogOnlyButton.IsChecked = Translator.LogOnlyFlag;
            Translator.ClearContexts();
        }

        private async void StartButton_Click(object? sender, RoutedEventArgs e)
        {
            if (ClassSession.IsRunning)
                await StopAndSave();
            else
            {
                Navigate("字幕");
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
        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
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
            Close();
        }

        private void ShowSessionState()
        {
            // Not while the class is being saved for the exit either: the window closes when that is done, and a
            // class started in between would end with it, unsaved.
            StartButton.IsEnabled = !ClassSession.IsStarting && !closeAsked;
            StartButton.Content = ClassSession.IsStarting ? "启动中…" : ClassSession.IsRunning ? "停止" : "开始";
            // Red only where it says "停止": a class counts as running a moment before it has finished starting.
            bool stops = ClassSession.IsRunning && !ClassSession.IsStarting;
            StartButton.Classes.Set("accent", !stops);
            StartButton.Classes.Set("danger", stops);
        }

        private void SpeechSwitch_Click(object? sender, RoutedEventArgs e)
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

        public void ToggleTopmost(bool enabled)
        {
            TopmostButton.IsChecked = enabled;
            Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private void CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            Navigate("设置");
            Dispatcher.UIThread.Post(() => new WelcomeWindow().Show(this), DispatcherPriority.Background);
        }

        // Only grows the window when a page needs more room.
        public void AutoHeightAdjust(int minHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
                Height = minHeight;
        }

        private void ShowToast(string title, string message, SnackbarType type, int timeout, bool closeButton)
        {
            var (background, border) = type switch
            {
                SnackbarType.Warning => ("#FF5C4700", "#FFFFB900"),
                SnackbarType.Error => ("#FF5A1D1D", "#FFE5484D"),
                SnackbarType.Success => ("#FF1B4D2B", "#FF30A46C"),
                _ => ("#FF2B3A4E", "#FF5B8DEF"),
            };
            Toast.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(background));
            Toast.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(border));
            ToastTitle.Text = title;
            ToastTitle.IsVisible = !string.IsNullOrEmpty(title);
            ToastMessage.Text = message;
            ToastMessage.IsVisible = !string.IsNullOrEmpty(message);
            ToastClose.IsVisible = closeButton;
            foreach (var block in new[] { ToastTitle, ToastMessage })
                block.Foreground = Brushes.White;
            ToastClose.Foreground = Brushes.White;
            Toast.IsVisible = true;

            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(Math.Max(timeout, 2));
            toastTimer.Start();
        }

        private void HideToast()
        {
            toastTimer.Stop();
            Toast.IsVisible = false;
        }

        private void ToastClose_Click(object? sender, RoutedEventArgs e)
        {
            HideToast();
        }
    }
}
