using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : UserControl
    {
        private readonly TranscriptView transcript = new();
        private bool followTranscript = true;
        private int captionPending = 0;
        private bool showOriginal = true;
        // Large-caption mode is remembered for this run only (the page is kept alive by the main window).
        private static bool largeMode;
        private const int LARGE_ORIGINAL_SIZE = 15;
        private const int LARGE_TRANSLATED_SIZE = 26;

        public CaptionPage()
        {
            InitializeComponent();
            // The list changes row by row (new sentences at its end).
            TranscriptList.ItemsSource = transcript.Lines;
            transcript.Updated += () => TranscriptEmptyHint.IsVisible = transcript.Lines.Count == 0;
            TranscriptScroll.ScrollChanged += TranscriptScroll_ScrollChanged;
            ApplyLayout();
            LargeModeButton.IsChecked = largeMode;
            LargeModeButton.Click += (s, e) =>
            {
                largeMode = LargeModeButton.IsChecked == true;
                ApplyLayout();
            };

            Splitter.DragCompleted += (s, e) =>
                Translator.Setting.MainWindow.SectionPanelWidth = Math.Round(Root.ColumnDefinitions[2].ActualWidth);

            // The copy handlers sit on the lists, because their items are made from templates.
            TranscriptList.AddHandler(PointerPressedEvent, TextBlock_Pressed, RoutingStrategies.Bubble);
            OriginalCaption.PointerPressed += TextBlock_Pressed;
            TranslatedCaption.PointerPressed += TextBlock_Pressed;
            OriginalCaption.PointerWheelChanged += (s, e) =>
            {
                var window = Translator.Setting.MainWindow;
                AdjustFontOnWheel(e, () => window.OriginalFontSize, size => window.OriginalFontSize = size);
            };
            TranslatedCaption.PointerWheelChanged += (s, e) =>
            {
                var window = Translator.Setting.MainWindow;
                AdjustFontOnWheel(e, () => window.TranslatedFontSize, size => window.TranslatedFontSize = size);
            };
        }

        protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (Shell.Main is MainWindow main)
                main.AutoHeightAdjust(minHeight: (int)main.MinHeight);
            Subscribe(false);
            Subscribe(true);
            ShowCaption();
            OnSummaryStatusChanged();
            await transcript.Reload();
            await LoadSections(scrollToEnd: true);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Subscribe(false);
        }

        private void Subscribe(bool on)
        {
            if (on)
            {
                Summarizer.SectionSummarized += OnSectionSummarized;
                Translator.TranslationLogged += OnTranslationLogged;
                Refiner.ParagraphRefined += OnTranslationLogged;
                ClassSession.StateChanged += OnSessionChanged;
                Summarizer.StatusChanged += OnSummaryStatusChanged;
                Translator.Caption.PropertyChanged += OnCaptionChanged;
            }
            else
            {
                Summarizer.SectionSummarized -= OnSectionSummarized;
                Translator.TranslationLogged -= OnTranslationLogged;
                Refiner.ParagraphRefined -= OnTranslationLogged;
                ClassSession.StateChanged -= OnSessionChanged;
                Summarizer.StatusChanged -= OnSummaryStatusChanged;
                Translator.Caption.PropertyChanged -= OnCaptionChanged;
            }
        }

        // The caption changes on the translation threads, several of its properties at a time and many times a
        // second: the texts are updated on the UI thread, once for all the changes that came in until then.
        private void OnCaptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Interlocked.Exchange(ref captionPending, 1) == 1)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref captionPending, 0);
                ShowCaption();
            });
        }

        private void ShowCaption()
        {
            var caption = Translator.Caption;
            OriginalCaption.Text = caption.DisplayOriginalCaption;
            TranslatedCaption.Text = caption.DisplayTranslatedCaption;
            StatusHint.Text = caption.StatusHint;
            StatusHint.IsVisible = string.IsNullOrEmpty(caption.DisplayOriginalCaption);
            WarningText.Text = caption.SourceWarning;
            WarningBorder.IsVisible = caption.IsSourceWarningVisible;

            // Without translation the original is all there is.
            OriginalCaption.IsVisible = caption.IsOriginalVisible;
            if (showOriginal != caption.IsOriginalVisible)
            {
                showOriginal = caption.IsOriginalVisible;
                // A style hides the originals of the rows, those still to come included.
                TranscriptList.Classes.Set("noOriginal", !showOriginal);
            }
        }

        // The list keeps showing its end while sentences are added, until the reader scrolls up; scrolling back
        // down to the end follows again. Whether the end is in view is only looked at when the reader scrolls:
        // right after rows were added the list has grown and its place has not been moved yet.
        private void TranscriptScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, TranscriptScroll))
                return;
            double scrollable = TranscriptScroll.Extent.Height - TranscriptScroll.Viewport.Height;
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                // The list or the page changed its size.
                if (followTranscript)
                    TranscriptScroll.ScrollToEnd();
                else if (scrollable <= 0)
                    followTranscript = true;    // Nothing to scroll (a new class): its sentences are followed again.
            }
            else if (e.OffsetDelta.Y != 0)
                followTranscript = TranscriptScroll.Offset.Y >= scrollable - 20;
        }

        private void OnTranslationLogged()
        {
            Dispatcher.UIThread.Post(() => _ = transcript.Reload());
        }

        private void OnSummaryStatusChanged()
        {
            Dispatcher.UIThread.Post(() =>
            {
                SummaryStatus.Text = Summarizer.Status;
                SummaryStatus.IsVisible = !string.IsNullOrEmpty(Summarizer.Status);
            });
        }

        private void OnSessionChanged()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await transcript.Reload();
                await LoadSections(scrollToEnd: true);
            });
        }

        private void OnSectionSummarized(SectionEntry section)
        {
            Dispatcher.UIThread.Post(async () => await LoadSections(scrollToEnd: true));
        }

        private async Task LoadSections(bool scrollToEnd = false)
        {
            List<SectionEntry> sections;
            try
            {
                // Only this class's sections: the previous class is in the history.
                sections = ClassSession.FirstHistoryId >= 0
                    ? await SectionLogger.LoadSectionsInRange(ClassSession.FirstHistoryId, long.MaxValue)
                    : await SectionLogger.LoadSections(DateTime.Today);
            }
            catch (Exception)
            {
                return;
            }
            SectionList.ItemsSource = sections;
            SectionEmptyHint.IsVisible = sections.Count == 0;
            if (scrollToEnd)
                Dispatcher.UIThread.Post(() => SectionScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        private async void TextBlock_Pressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (e.Source is not TextBlock textBlock ||
                !(textBlock.Classes.Contains("copy") || textBlock == OriginalCaption || textBlock == TranslatedCaption))
                return;
            if (string.IsNullOrWhiteSpace(textBlock.Text))
                return;
            if (await Shell.CopyText(textBlock.Text))
                SnackbarHost.Show("已复制。", textBlock.Text, SnackbarType.Info);
            else
                SnackbarHost.Show("没能复制：剪贴板被其他程序占用，请再点一次。", string.Empty, SnackbarType.Warning, timeout: 2);
        }

        private void ApplyFontSizes()
        {
            OriginalCaption.FontSize = largeMode ? LARGE_ORIGINAL_SIZE : Translator.Setting.MainWindow.OriginalFontSize;
            TranslatedCaption.FontSize = largeMode ? LARGE_TRANSLATED_SIZE : Translator.Setting.MainWindow.TranslatedFontSize;
        }

        // Large-caption mode hides the section column; the current sentence is shown big at the bottom.
        private void ApplyLayout()
        {
            Root.ColumnDefinitions[2].MinWidth = largeMode ? 0 : 180;
            Root.ColumnDefinitions[2].Width = largeMode
                ? new GridLength(0)
                : new GridLength(Math.Max(Translator.Setting.MainWindow.SectionPanelWidth, 180));
            Splitter.IsVisible = !largeMode;
            RightPane.IsVisible = !largeMode;
            Grid.SetRowSpan(LeftPane, largeMode ? 1 : 2);
            LeftPane.Margin = new Thickness(16, 16, largeMode ? 16 : 0, largeMode ? 8 : 16);
            // The sizes of the large captions are fixed: the tips offer Ctrl + wheel only for the normal ones.
            string tip = largeMode ? "单击复制" : "单击复制，Ctrl+滚轮调整字号";
            ToolTip.SetTip(OriginalCaption, tip);
            ToolTip.SetTip(TranslatedCaption, tip);
            ApplyFontSizes();
        }

        private void EndSectionButton_Click(object? sender, RoutedEventArgs e)
        {
            MainWindow.EndSection();
        }

        // Ctrl + wheel changes a font size.
        private void AdjustFontOnWheel(PointerWheelEventArgs e, Func<int> get, Action<int> set)
        {
            if (largeMode || e.KeyModifiers != KeyModifiers.Control)
                return;
            set(AdjustFontSize(get(), e.Delta.Y));
            ApplyFontSizes();
            e.Handled = true;
        }

        private static int AdjustFontSize(int current, double wheelDelta)
        {
            int next = current + (wheelDelta > 0 ? StyleConsts.DELTA_FONT_SIZE : -StyleConsts.DELTA_FONT_SIZE);
            return Math.Clamp(next, StyleConsts.MIN_FONT_SIZE, StyleConsts.MAX_FONT_SIZE);
        }
    }
}
