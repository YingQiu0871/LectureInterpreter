using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    // The bilingual floating window: the last sentence pairs (original above, translation below) and the sentence
    // in progress, on a semi-transparent panel that can stay above everything.
    public partial class BilingualWindow : Window
    {
        private const int MIN_FONT_SIZE = 12;
        private const int MAX_FONT_SIZE = 40;
        private const int MIN_OPACITY = 25;
        private const int MAX_OPACITY = 250;
        private const int DELTA_OPACITY = 25;

        private static readonly IBrush OriginalBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xC8, 0xC8, 0xC8));
        private static readonly IBrush TranslationBrush = Brushes.White;
        private static readonly IBrush PinnedBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x80, 0x8F, 0xD1, 0x8F));
        private static readonly IBrush UnpinnedBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        private readonly BilingualFeed feed;

        private static BilingualWindowState Look => Translator.Setting.BilingualWindow;

        public BilingualWindow()
        {
            InitializeComponent();

            Look.Sentences = Math.Clamp(Look.Sentences, BilingualFeed.MIN_SENTENCES, BilingualFeed.MAX_SENTENCES);
            feed = new BilingualFeed(Look.Sentences);
            feed.Changed += () => Dispatcher.UIThread.Post(Render);

            WindowBounds.Restore(this, "BilingualWindow", new Size(720, 240));
            if (Look.Height > 0)
            {
                // The user has chosen a height: stop following the content (it scrolls to the newest line).
                double room = Screens.ScreenFromWindow(this) is { } screen ? screen.WorkingArea.Height / screen.Scaling : 2000;
                Height = Math.Clamp(Look.Height, MinHeight, Math.Max(MinHeight, room));
                SizeToContent = SizeToContent.Manual;
            }
            else
            {
                // The height follows the text.
                Height = double.NaN;
            }
            WindowBounds.Track(this, "BilingualWindow");

            PositionChanged += (s, e) => LimitHeight();
            Opened += (s, e) =>
            {
                LimitHeight();
                feed.Start();
                Render();
            };
            Closed += (s, e) => feed.Dispose();

            FloatLock.Changed += ApplyLock;
            Closed += (s, e) => FloatLock.Changed -= ApplyLock;
            Opened += (s, e) => ApplyLock();
            // Once the user has set a height, remember it.
            Resized += (s, e) =>
            {
                // X11 reports a window-manager resize as Unspecified.
                if (SizeToContent == SizeToContent.Manual && e.Reason is WindowResizeReason.User or WindowResizeReason.Unspecified)
                    Look.Height = e.ClientSize.Height;
            };

            PointerEntered += (s, e) => ControlPanel.IsVisible = !FloatLock.Locked;
            PointerExited += (s, e) => ControlPanel.IsVisible = false;
            PanelBorder.PointerPressed += (s, e) =>
            {
                if (!FloatLock.Locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                    BeginMoveDrag(e);
            };
            Resizer(LeftEdge, WindowEdge.West);
            Resizer(RightEdge, WindowEdge.East);
            Resizer(TopEdge, WindowEdge.North);
            Resizer(BottomEdge, WindowEdge.South);
            Resizer(TopLeftEdge, WindowEdge.NorthWest);
            Resizer(TopRightEdge, WindowEdge.NorthEast);
            Resizer(BottomLeftEdge, WindowEdge.SouthWest);
            Resizer(BottomRightEdge, WindowEdge.SouthEast);

            ShowPinState();
            ShowSentences();
            ApplyBackgroundOpacity();
        }

        private void Resizer(Border edge, WindowEdge direction)
        {
            edge.PointerPressed += (s, e) =>
            {
                if (FloatLock.Locked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    return;
                // A height change fixes the height: the window no longer follows the content.
                if (direction != WindowEdge.West && direction != WindowEdge.East && SizeToContent != SizeToContent.Manual)
                {
                    Height = Bounds.Height;
                    SizeToContent = SizeToContent.Manual;
                }
                BeginResizeDrag(direction, e);
            };
        }

        private void ApplyLock()
        {
            ClickThrough.Set(this, FloatLock.Locked);
            if (FloatLock.Locked)
                ControlPanel.IsVisible = false;
        }

        private void LockButton_Click(object? sender, RoutedEventArgs e)
        {
            FloatLock.Locked = true;
        }

        // The window may grow down to the bottom of the work area; longer content scrolls to its newest line.
        private void LimitHeight()
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null)
                return;
            double scale = screen.Scaling > 0 ? screen.Scaling : 1;
            double room = (screen.WorkingArea.Bottom - Position.Y) / scale;
            MaxHeight = Math.Max(120, room);
            // Panel margin (2 x 5) and scroll area margin (2 x 12).
            PairScroll.MaxHeight = Math.Max(40, MaxHeight - 34);
        }

        private void Render()
        {
            var items = new List<BilingualFeed.Pair>(feed.Finished);
            var current = feed.Current;
            if (current != null)
                items.Add(current);

            PairPanel.Children.Clear();
            if (items.Count == 0)
            {
                HintText.FontSize = Look.FontSize * 0.8;
                HintText.Text = feed.SessionStarted ? "正在听……" : "点击「开始」后这里显示原文和译文";
                HintText.IsVisible = true;
                PairScroll.ScrollToHome();
                return;
            }
            HintText.IsVisible = false;

            bool originalOnly = feed.OriginalOnly;
            double translationSize = Look.FontSize;
            double originalSize = originalOnly ? translationSize : Math.Round(translationSize * 0.8);
            for (int i = 0; i < items.Count; i++)
            {
                var pair = items[i];
                bool hasTranslation = !originalOnly && pair.Translation.Length > 0;
                PairPanel.Children.Add(new TextBlock
                {
                    Text = pair.Original,
                    FontSize = originalSize,
                    Foreground = originalOnly ? TranslationBrush : OriginalBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, i == 0 ? 0 : 8, 0, hasTranslation ? 1 : 0),
                });
                if (hasTranslation)
                {
                    PairPanel.Children.Add(new TextBlock
                    {
                        Text = pair.Translation,
                        FontSize = translationSize,
                        Foreground = TranslationBrush,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
            PairScroll.ScrollToEnd();
        }

        private void ShowPinState()
        {
            Topmost = Look.Topmost;
            PinButton.Background = Look.Topmost ? PinnedBrush : UnpinnedBrush;
        }

        private void ShowSentences()
        {
            SentencesButton.Content = $"{Look.Sentences} 句";
        }

        private void ApplyBackgroundOpacity()
        {
            PanelBorder.Background = new SolidColorBrush(
                Avalonia.Media.Color.FromArgb((byte)Math.Clamp(Look.Opacity, 0, 255), 0x11, 0x18, 0x27));
        }

        private void PinButton_Click(object? sender, RoutedEventArgs e)
        {
            Look.Topmost = !Look.Topmost;
            ShowPinState();
        }

        private void FontIncrease_Click(object? sender, RoutedEventArgs e)
        {
            Look.FontSize = Math.Min(Look.FontSize + 2, MAX_FONT_SIZE);
            Render();
        }

        private void FontDecrease_Click(object? sender, RoutedEventArgs e)
        {
            Look.FontSize = Math.Max(Look.FontSize - 2, MIN_FONT_SIZE);
            Render();
        }

        private void OpacityIncrease_Click(object? sender, RoutedEventArgs e)
        {
            Look.Opacity = Math.Min(Look.Opacity + DELTA_OPACITY, MAX_OPACITY);
            ApplyBackgroundOpacity();
        }

        private void OpacityDecrease_Click(object? sender, RoutedEventArgs e)
        {
            Look.Opacity = Math.Max(Look.Opacity - DELTA_OPACITY, MIN_OPACITY);
            ApplyBackgroundOpacity();
        }

        private void SentencesButton_Click(object? sender, RoutedEventArgs e)
        {
            Look.Sentences = Look.Sentences >= BilingualFeed.MAX_SENTENCES
                ? BilingualFeed.MIN_SENTENCES
                : Look.Sentences + 1;
            feed.Sentences = Look.Sentences;
            ShowSentences();
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
