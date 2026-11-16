using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    // The part of the speaker that is the same on every platform: what is read aloud, in which order, and when
    // captions are ignored because our own speech plays on the device the recognizer listens to. The playback
    // itself is in src/utils/Speaker.cs (Windows) and desktop/platform/Speaker.cs (Linux / macOS); each runs
    // `Worker` on the thread started here and takes texts with `TryDequeue`.
    public static partial class Speaker
    {
        // Translations older than this backlog are dropped, so that speech never lags far behind the lecture.
        private const int MAX_TRANSLATION_BACKLOG = 2;

        private static readonly object queueLock = new();
        private static readonly LinkedList<(string Text, bool IsSummary)> queue = new();
        private static readonly AutoResetEvent signal = new(false);

        // Keep ignoring captions a little after speech ends: the recognizer lags behind the audio.
        private static readonly TimeSpan SUPPRESS_TAIL = TimeSpan.FromMilliseconds(1500);

        private static volatile bool stopRequested = false;
        private static volatile bool speakingOnDefaultOutput = false;
        private static DateTime suppressUntil = DateTime.MinValue;
        private static string lastSpokenSource = string.Empty;
        public static event Action<bool>? MutedChanged;

        public static bool Muted
        {
            get => Lecture?.SpeechMuted ?? false;
            set
            {
                if (Lecture != null)
                    Lecture.SpeechMuted = value;
                if (value)
                    StopAll();
                MutedChanged?.Invoke(value);
            }
        }

        private static LectureState? Lecture => Translator.Setting?.Lecture;

        // True while our speech plays on the default output device, which the recognizer listens to.
        public static bool SuppressCaptions => speakingOnDefaultOutput || DateTime.Now < suppressUntil;

        public static void Start()
        {
            var thread = new Thread(Worker) { IsBackground = true, Name = "Speaker" };
            thread.Start();
        }

        public static void EnqueueSummary(string summary)
        {
            if (Muted || Lecture == null || Lecture.SpeakMode is SpeakMode.Off or SpeakMode.Interpretation)
                return;
            string text = CleanForSpeech(summary);
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (queueLock)
            {
                // Summaries go before any pending translation.
                var node = queue.First;
                while (node != null && node.Value.IsSummary)
                    node = node.Next;
                if (node == null)
                    queue.AddLast((text, true));
                else
                    queue.AddBefore(node, (text, true));
            }
            signal.Set();
        }

        // A refined paragraph, read as one piece.
        public static void EnqueueParagraph(string translatedText)
        {
            // The last paragraph is refined a moment after "停止": the earphones stay quiet then.
            if (Muted || Lecture == null || Lecture.SpeakMode != SpeakMode.SummaryAndTranslation ||
                !ClassSession.IsRunning)
                return;
            string text = CleanForSpeech(translatedText);
            if (string.IsNullOrWhiteSpace(text))
                return;
            lock (queueLock)
            {
                queue.AddLast((text, false));
            }
            signal.Set();
        }

        public static void EnqueueTranslation(string sourceText, string translatedText)
        {
            // Translations still on their way when the class stops are logged, but no longer read aloud.
            if (Muted || Lecture == null || !ClassSession.IsRunning)
                return;
            // Interpretation reads every sentence at once. Otherwise, with paragraph refinement on, the refined
            // paragraph is read instead of each raw sentence (a few seconds later, but more accurate).
            bool read = Lecture.SpeakMode == SpeakMode.Interpretation ||
                        (Lecture.SpeakMode == SpeakMode.SummaryAndTranslation && !Refiner.Enabled);
            if (!read)
                return;
            if (string.IsNullOrWhiteSpace(translatedText) ||
                translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                return;

            // The recognizer often revises a finished sentence; do not read the same sentence twice.
            if (lastSpokenSource.Length > 0 &&
                TextUtil.Similarity(sourceText, lastSpokenSource) > TextUtil.SIM_THRESHOLD)
                return;
            lastSpokenSource = sourceText;

            string text = CleanForSpeech(RegexPatterns.NoticePrefix().Replace(translatedText, string.Empty));
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (queueLock)
            {
                queue.AddLast((text, false));
                int backlog = queue.Count(item => !item.IsSummary);
                var node = queue.First;
                while (backlog > MAX_TRANSLATION_BACKLOG && node != null)
                {
                    var next = node.Next;
                    if (!node.Value.IsSummary)
                    {
                        queue.Remove(node);
                        backlog--;
                    }
                    node = next;
                }
            }
            signal.Set();
        }

        public static void Speak(string text)
        {
            lock (queueLock)
                queue.AddFirst((CleanForSpeech(text), true));
            signal.Set();
        }

        // The next text to read; false when the queue is empty.
        private static bool TryDequeue(out string text)
        {
            lock (queueLock)
            {
                if (queue.First == null)
                {
                    text = string.Empty;
                    return false;
                }
                text = queue.First.Value.Text;
                queue.RemoveFirst();
                return true;
            }
        }

        private static void ClearQueue()
        {
            lock (queueLock)
                queue.Clear();
        }

        // Only called by the worker. A problem that stays (earphones that are not connected) fails for every
        // sentence: it is shown once in a while, not once a sentence.
        private static readonly TimeSpan FAILURE_NOTE_INTERVAL = TimeSpan.FromMinutes(1);
        private static string lastFailure = string.Empty;
        private static DateTime lastFailureTime = DateTime.MinValue;

        private static void ReportFailure(string title, string message, SnackbarType type)
        {
            var now = DateTime.Now;
            string failure = title + message;
            if (failure == lastFailure && now - lastFailureTime < FAILURE_NOTE_INTERVAL)
                return;
            lastFailure = failure;
            lastFailureTime = now;
            SnackbarHost.Show(title, message, type, timeout: 4, closeButton: true);
        }

        // The culture of what is spoken: the lecture language in 原语言模式, otherwise the target language.
        private static string SpeechLanguage() => Translator.Setting?.Lecture.OriginalOnly == true
            ? LectureLanguages.Current?.Culture ?? "en-US"
            : Translator.Setting?.TargetLanguage ?? "zh-CN";

        private static string CleanForSpeech(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimStart('-', '*', '•', '#', ' '))
                .Where(line => line.Length > 0);
            return string.Join("。", lines).Replace("。。", "。");
        }
    }
}
