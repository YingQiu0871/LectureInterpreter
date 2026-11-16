using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        // Large-text mode is remembered for this run only.
        private static bool bigMode = false;
        private const int BIG_ORIGINAL_FONT_SIZE = 15;
        private const int BIG_TRANSLATED_FONT_SIZE = 26;
        // The least width in which the status of the summaries is still a line of words and not a column of them.
        private const double MIN_STATUS_WIDTH = 140;

        // The sentences of this class. The page is kept alive between visits, and so is its list.
        private readonly TranscriptView transcript = new();
        private bool followTranscript = true;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Translator.Caption;

            SectionColumn.Width = new GridLength(Math.Max(Translator.Setting.MainWindow.SectionPanelWidth, 180));
            ScrollHelper.UseOwnScrollViewer(this, TranscriptScroll, SectionScroll);

            TranscriptList.ItemsSource = transcript.Lines;
            transcript.Updated += () =>
                TranscriptEmptyHint.Visibility = transcript.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TranscriptScroll.ScrollChanged += TranscriptScroll_ScrollChanged;
            SectionPanel.SizeChanged += (s, e) => PlaceStatus();

            Loaded += async (s, e) =>
            {
                AutoHeight();
                Subscribe(false);
                Subscribe(true);
                OnSummaryStatusChanged();
                await transcript.Reload();
                await LoadSections(scrollToEnd: true);
            };
            Unloaded += (s, e) => Subscribe(false);

            ApplyFontSizes();
            if (bigMode)
                BigModeToggle.IsChecked = true;
        }

        // The list keeps showing its end while sentences are added, until the reader scrolls up; scrolling back
        // down to the end follows again. Whether the end is in view is only looked at when the reader scrolls:
        // right after rows were added the list has grown and its place has not been moved yet.
        private void TranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, TranscriptScroll))
                return;
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            {
                // The list or the page changed its size.
                if (followTranscript)
                    TranscriptScroll.ScrollToEnd();
                else if (TranscriptScroll.ScrollableHeight <= 0)
                    followTranscript = true;    // Nothing to scroll (a new class): its sentences are followed again.
            }
            else if (e.VerticalChange != 0)
                followTranscript = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 20;
        }

        private void BigModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            bigMode = BigModeToggle.IsChecked == true;
            if (bigMode)
            {
                SectionColumn.MinWidth = 0;
                SectionColumn.Width = new GridLength(0);
                SectionPanel.Visibility = Visibility.Collapsed;
                SectionSplitter.Visibility = Visibility.Collapsed;
                StatusHost.Visibility = Visibility.Collapsed;
                Grid.SetRowSpan(LeftPane, 1);
                LeftPane.Margin = new Thickness(16, 16, 16, 12);
            }
            else
            {
                SectionColumn.MinWidth = 180;
                SectionColumn.Width = new GridLength(Math.Max(Translator.Setting.MainWindow.SectionPanelWidth, 180));
                SectionPanel.Visibility = Visibility.Visible;
                SectionSplitter.Visibility = Visibility.Visible;
                StatusHost.Visibility = Visibility.Visible;
                Grid.SetRowSpan(LeftPane, 2);
                LeftPane.Margin = new Thickness(16, 16, 0, 16);
            }
            // The sizes of the large captions are fixed: the tips offer Ctrl + wheel only for the normal ones.
            string tip = bigMode ? "单击复制" : "单击复制，Ctrl+滚轮调整字号";
            OriginalCaption.ToolTip = tip;
            TranslatedCaption.ToolTip = tip;
            ApplyFontSizes();
        }

        private void EndSectionButton_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceStatus();

        // The status of the summaries stands to the left of the button that ends a section and keeps clear of
        // it. In a column too narrow for words next to the button it stands above the button.
        private void PlaceStatus()
        {
            bool beside = SectionPanel.ActualWidth - EndSectionButton.ActualWidth - 12 >= MIN_STATUS_WIDTH;
            StatusHost.Margin = new Thickness(0, 0, beside ? EndSectionButton.ActualWidth + 28 : 16, 16);
            SummaryStatus.Margin = new Thickness(0, 0, 0, beside ? 0 : EndSectionButton.ActualHeight + 8);
        }

        // The page listens to the session only while it is shown. (Taken off before being put on: `Loaded` can
        // come twice in a row, and every sentence would then be handled twice.)
        private void Subscribe(bool on)
        {
            if (on)
            {
                Summarizer.SectionSummarized += OnSectionSummarized;
                Translator.TranslationLogged += OnTranslationLogged;
                Refiner.ParagraphRefined += OnTranslationLogged;
                ClassSession.StateChanged += OnSessionChanged;
                Summarizer.StatusChanged += OnSummaryStatusChanged;
            }
            else
            {
                Summarizer.SectionSummarized -= OnSectionSummarized;
                Translator.TranslationLogged -= OnTranslationLogged;
                Refiner.ParagraphRefined -= OnTranslationLogged;
                ClassSession.StateChanged -= OnSessionChanged;
                Summarizer.StatusChanged -= OnSummaryStatusChanged;
            }
        }

        private void EndSectionButton_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.EndSection();
        }

        private void OnTranslationLogged()
        {
            Dispatcher.InvokeAsync(() => transcript.Reload());
        }

        private void OnSummaryStatusChanged()
        {
            Dispatcher.InvokeAsync(() =>
            {
                SummaryStatus.Text = Summarizer.Status;
                SummaryStatus.Visibility = string.IsNullOrEmpty(Summarizer.Status) ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        private void OnSessionChanged()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await transcript.Reload();
                await LoadSections(scrollToEnd: true);
            });
        }

        private void OnSectionSummarized(SectionEntry section)
        {
            Dispatcher.InvokeAsync(async () => await LoadSections(scrollToEnd: true));
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
            SectionEmptyHint.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (scrollToEnd)
                SectionScroll.ScrollToEnd();
        }

        private void SectionSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            Translator.Setting.MainWindow.SectionPanelWidth = Math.Round(SectionColumn.ActualWidth);
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            {
                // Another program (clipboard manager, remote desktop, IME) may hold the clipboard for a moment.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Clipboard.SetDataObject(textBlock.Text, true);
                        SnackbarHost.Show("已复制。", textBlock.Text, SnackbarType.Info, 100);
                        break;
                    }
                    catch (Exception) when (attempt < 4)
                    {
                        await Task.Delay(150);
                    }
                    catch (Exception)
                    {
                        SnackbarHost.Show("没能复制：剪贴板被其他程序占用，请再点一次。", string.Empty, SnackbarType.Warning, timeout: 2);
                        break;
                    }
                }
            }
        }

        private void ApplyFontSizes()
        {
            // Large-text mode has fixed sizes; the saved ones (changed with Ctrl + wheel) are those of normal mode.
            OriginalCaption.FontSize = bigMode ? BIG_ORIGINAL_FONT_SIZE : Translator.Setting.MainWindow.OriginalFontSize;
            TranslatedCaption.FontSize = bigMode ? BIG_TRANSLATED_FONT_SIZE : Translator.Setting.MainWindow.TranslatedFontSize;
        }

        private void OriginalCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var window = Translator.Setting.MainWindow;
            AdjustFontOnWheel(e, () => window.OriginalFontSize, size => window.OriginalFontSize = size);
        }

        private void TranslatedCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var window = Translator.Setting.MainWindow;
            AdjustFontOnWheel(e, () => window.TranslatedFontSize, size => window.TranslatedFontSize = size);
        }

        // Ctrl + wheel changes a font size.
        private void AdjustFontOnWheel(MouseWheelEventArgs e, Func<int> get, Action<int> set)
        {
            if (bigMode || Keyboard.Modifiers != ModifierKeys.Control)
                return;
            set(AdjustFontSize(get(), e.Delta));
            ApplyFontSizes();
            e.Handled = true;
        }

        private static int AdjustFontSize(int current, int wheelDelta)
        {
            int next = current + (wheelDelta > 0 ? StyleConsts.DELTA_FONT_SIZE : -StyleConsts.DELTA_FONT_SIZE);
            return Math.Clamp(next, StyleConsts.MIN_FONT_SIZE, StyleConsts.MAX_FONT_SIZE);
        }

        public void AutoHeight()
        {
            (App.Current.MainWindow as MainWindow).AutoHeightAdjust(
                minHeight: (int)App.Current.MainWindow.MinHeight);
        }
    }
}
