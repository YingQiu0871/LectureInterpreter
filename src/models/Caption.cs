using System.ComponentModel;
using System.Runtime.CompilerServices;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";
        private string statusHint = ClassSession.IDLE_HINT;

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        // The last finished sentences, oldest first. Written by the logging chain and read by the translation
        // requests and the UI, all on different threads, so it is only touched under its own lock.
        private readonly Queue<TranslationHistoryEntry> contexts = new(MAX_CONTEXTS);

        public int ContextCount
        {
            get
            {
                lock (contexts)
                {
                    return contexts.Count;
                }
            }
        }

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        // The texts on screen. They are assigned many times a second, mostly with the text they already have:
        // only a new text is announced, or every page and floating window would draw itself again each time.
        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                if (displayOriginalCaption == value)
                    return;
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        private string sourceWarning = string.Empty;

        // A problem with what LiveCaptions delivers, shown above the current sentence.
        public string SourceWarning
        {
            get => sourceWarning;
            set
            {
                if (sourceWarning == value)
                    return;
                sourceWarning = value;
                OnPropertyChanged("SourceWarning");
                OnPropertyChanged("SourceWarningVisibility");
                OnPropertyChanged("IsSourceWarningVisible");
            }
        }
#if WINDOWS
        public System.Windows.Visibility SourceWarningVisibility =>
            string.IsNullOrEmpty(sourceWarning) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
#endif
        public bool IsSourceWarningVisible => !string.IsNullOrEmpty(sourceWarning);

        // Shown on the caption page while there is no caption yet.
        public string StatusHint
        {
            get => statusHint;
            set
            {
                if (statusHint == value)
                    return;
                statusHint = value;
                OnPropertyChanged("StatusHint");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                if (displayTranslatedCaption == value)
                    return;
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                if (overlayOriginalCaption == value)
                    return;
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                if (overlayNoticePrefix == value)
                    return;
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                if (overlayCurrentTranslation == value)
                    return;
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        // The original text OverlayCurrentTranslation was made for.
        public string OverlayTranslationSource { get; set; } = string.Empty;

        public string OverlayPreviousTranslation =>
            GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation);

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        private static bool IsUsableContext(TranslationHistoryEntry? entry) =>
            entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
            !entry.TranslatedText.Contains("[ERROR]") && !entry.TranslatedText.Contains("[WARNING]");

        public void AddContext(TranslationHistoryEntry entry)
        {
            lock (contexts)
            {
                while (contexts.Count >= MAX_CONTEXTS)
                    contexts.Dequeue();
                contexts.Enqueue(entry);
            }
            OnContextsChanged();
        }

        public void ClearContexts()
        {
            lock (contexts)
            {
                if (contexts.Count == 0)
                    return;
                contexts.Clear();
            }
            OnContextsChanged();
        }

        private void OnContextsChanged()
        {
            OnPropertyChanged("DisplayLogCards");
            OnPropertyChanged("OverlayPreviousTranslation");
        }

        // A new class starts with an empty page. What the last class left here would otherwise stay on screen
        // and hide the status hints ("正在连接……", a missing key, a failed start) behind its last sentence.
        public void Reset()
        {
            OriginalCaption = string.Empty;
            TranslatedCaption = string.Empty;
            OverlayTranslationSource = string.Empty;
            DisplayOriginalCaption = string.Empty;
            DisplayTranslatedCaption = string.Empty;
            OverlayOriginalCaption = " ";
            OverlayCurrentTranslation = " ";
            OverlayNoticePrefix = " ";
            ClearContexts();
        }

        // The newest `count` contexts, oldest first, as a copy that is safe to read on any thread.
        private TranslationHistoryEntry[] LastContexts(int count)
        {
            lock (contexts)
            {
                if (count <= 0 || contexts.Count == 0)
                    return [];
                return contexts.Skip(Math.Max(0, contexts.Count - count)).ToArray();
            }
        }

        public string GetPreviousText(int count, TextType textType)
        {
            var last = LastContexts(count);
            if (last.Length == 0)
                return string.Empty;

            var prev = last
                .Select(entry => !IsUsableContext(entry) ?
                    "" : (textType == TextType.Caption ? entry.SourceText : entry.TranslatedText))
                .Aggregate((accu, cur) =>
                {
                    if (!string.IsNullOrEmpty(accu))
                    {
                        if (Array.IndexOf(TextUtil.PUNC_EOS, accu[^1]) == -1)
                            accu += TextUtil.isCJChar(accu[^1]) ? "。" : ". ";
                        else
                            accu += TextUtil.isCJChar(accu[^1]) ? "" : " ";
                    }
                    cur = RegexPatterns.NoticePrefix().Replace(cur, "");
                    return accu + cur;
                });

            if (textType == TextType.Translation)
                prev = RegexPatterns.NoticePrefix().Replace(prev, "");
            if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(prev) && prev[^1] < 0x80)
                prev += " ";
            return prev;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            return LastContexts(count).Where(IsUsableContext).ToArray();
        }

        // Without translation the original is all there is.
        public bool IsOriginalVisible =>
            !(Translator.Setting?.Lecture.ShowOriginal == false && Translator.Setting?.Lecture.SkipTranslation == false);

#if WINDOWS
        public System.Windows.Visibility OriginalVisibility =>
            IsOriginalVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
#endif

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
