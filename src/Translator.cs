using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
#if WINDOWS
using System.Windows.Automation;
#endif

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
#if WINDOWS
        private static AutomationElement? window = null;
#endif
        private static Caption? caption = null;
        private static Setting? setting = null;

        // `Final`: a finished sentence that goes into the transcript; otherwise a preview of the sentence being spoken.
        private static readonly ConcurrentQueue<(string Text, bool Final)> pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();

#if WINDOWS
        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
#endif
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        // Complete sentences already sent for translation, newest last. Used so that every sentence
        // is translated exactly once, even when LiveCaptions finishes several sentences between two reads.
        private static readonly LinkedList<string> committedSentences = new();
        private const int MAX_COMMITTED = 40;
        private const int MAX_NEW_PER_READ = 8;
        private static bool sentencesSeeded = false;

        // Called when a class starts: whatever LiveCaptions already shows belongs to before the class.
        public static void ResetSentences()
        {
            lock (cloudLock)
            {
                lock (committedSentences)
                {
                    committedSentences.Clear();
                    sentencesSeeded = false;
                    heldFragment = string.Empty;
                    cloudPartial = string.Empty;
                    lastPreviewLength = 0;
                    fragmentTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    recentChinese.Clear();
                    if (Caption != null)
                        Caption.SourceWarning = string.Empty;
                }
            }
        }

        // `DisplayLoop` puts a result on screen in several steps; a reset must not fall in between them.
        private static readonly object displayLock = new();

        // Called when a class starts: the page and the floating windows show nothing of the class before.
        public static void ResetDisplay()
        {
            lock (displayLock)
            {
                translationTaskQueue.Reset();
                Caption?.Reset();
            }
        }

        private const double REVISION_SIMILARITY = 0.75;

        // Same as TextUtil.Similarity(...) > REVISION_SIMILARITY, but the length difference alone rules most pairs
        // out before the O(n*m) edit distance, which otherwise runs for every sentence on every read of the captions.
        private static bool IsRevision(string committed, string sentence)
        {
            // The same prefix rule as TextUtil.Similarity.
            if (committed.StartsWith(sentence) || sentence.StartsWith(committed))
                return true;
            int longer = Math.Max(committed.Length, sentence.Length);
            if (Math.Abs(committed.Length - sentence.Length) > longer * (1 - REVISION_SIMILARITY))
                return false;
            return TextUtil.Similarity(committed, sentence) > REVISION_SIMILARITY;
        }

        private static bool IsCommitted(string sentence)
        {
            foreach (var committed in committedSentences)
            {
                if (committed.EndsWith(sentence, StringComparison.Ordinal))
                    return true;
            }
            // LiveCaptions keeps revising recent sentences; a revised one is not a new sentence. The text may
            // also start in the middle of a sentence that scrolled away, so compare with the same-length ending too.
            int checkedCount = 0;
            for (var node = committedSentences.Last; node != null && checkedCount < 10; node = node.Previous, checkedCount++)
            {
                string committed = node.Value;
                if (IsRevision(committed, sentence))
                    return true;
                if (sentence.Length >= 12 && committed.Length > sentence.Length &&
                    IsRevision(committed[^sentence.Length..], sentence))
                    return true;
            }
            return false;
        }

        public const string ALREADY_CHINESE_WARNING =
            "系统字幕送过来的已经是中文，多半是打开了系统实时辅助字幕自带的“翻译”。它会漏句、延迟，" +
            "中外对照也看不到外语原文。请到“设置”页点“显示系统实时辅助字幕”，在它的 ⚙️ 菜单里关掉“翻译”" +
            "（或把翻译语言设为“无”），再点“隐藏”。如果老师本来就讲中文，可以不管这条提示。";
        private static readonly Queue<bool> recentChinese = new();

        // LiveCaptions can translate by itself (Windows 11 24H2+). Then it hands over Chinese text, which hides the
        // original and loses sentences, so tell the user when the target is Chinese and so is everything we read.
        // Only asked for what LiveCaptions delivers: the cloud recognizer has no translation of its own, and the
        // advice of the warning would not apply to it.
        private static void CheckAlreadyTranslated(string sentence)
        {
            if (Setting?.Lecture.OriginalOnly == true ||
                LectureLanguages.Code(Setting?.Lecture.AsrLanguage) is "zh" or "yue" ||
                !(Setting?.TargetLanguage ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                recentChinese.Clear();
                ClearAlreadyTranslatedWarning();
                return;
            }
            int letters = sentence.Count(char.IsLetter);
            int han = sentence.Count(ch => ch >= '\u4E00' && ch <= '\u9FFF');
            recentChinese.Enqueue(letters > 0 && han * 2 > letters);
            while (recentChinese.Count > 6)
                recentChinese.Dequeue();
            int chinese = recentChinese.Count(isChinese => isChinese);
            if (recentChinese.Count >= 4 && chinese >= recentChinese.Count - 1)
                Caption.SourceWarning = ALREADY_CHINESE_WARNING;
            else if (chinese <= 1)
                ClearAlreadyTranslatedWarning();
        }

        // Only this warning: whatever else is shown in its place is not for a sentence to take away.
        private static void ClearAlreadyTranslatedWarning()
        {
            if (Caption.SourceWarning == ALREADY_CHINESE_WARNING)
                Caption.SourceWarning = string.Empty;
        }

        private static void Commit(string sentence)
        {
            committedSentences.AddLast(sentence);
            while (committedSentences.Count > MAX_COMMITTED)
                committedSentences.RemoveFirst();
        }

        // The complete sentences (ending with EOS punctuation) in the text, in order.
        private static List<string> CompleteSentences(string text)
        {
            var sentences = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (Array.IndexOf(TextUtil.PUNC_EOS, text[i]) == -1)
                    continue;
                // Keep runs like "?!" or "..." together.
                while (i + 1 < text.Length && Array.IndexOf(TextUtil.PUNC_EOS, text[i + 1]) != -1)
                    i++;
                // "3.5" or "e.g." is not the end of a sentence: Western punctuation must be followed by a space.
                if (text[i] < 0x80 && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
                    continue;
                string sentence = text[start..(i + 1)].Trim();
                if (sentence.Length > 1)
                    sentences.Add(sentence);
                start = i + 1;
            }
            return sentences;
        }

        // Queues every sentence LiveCaptions finished since the last read, oldest first.
        private static bool QueueNewSentences(string fullText)
        {
            var sentences = CompleteSentences(fullText);
            bool queued = false;
            lock (committedSentences)
            {
                if (!sentencesSeeded)
                {
                    foreach (var sentence in sentences)
                        Commit(sentence);
                    sentencesSeeded = true;
                    return false;
                }
                // Even a first sentence cut off at the top of LiveCaptions' text is kept, unless it is
                // the end of a sentence we already have.
                int first = Math.Max(0, sentences.Count - MAX_NEW_PER_READ);
                for (int i = first; i < sentences.Count; i++)
                {
                    if (IsCommitted(sentences[i]))
                        continue;
                    Commit(sentences[i]);
                    CheckAlreadyTranslated(sentences[i]);
                    pendingTextQueue.Enqueue((sentences[i], true));
                    queued = true;
                }
            }
            return queued;
        }

        private static int lastPreviewLength = 0;

        // A very short "sentence" from the cloud recognizer ("I.", "So."), usually cut off by a pause: it is held
        // and put in front of the next sentence instead of becoming a line of its own.
        private static readonly object cloudLock = new();
        private static string heldFragment = string.Empty;
        // The sentence being spoken, as far as the cloud recognizer has it (with the held fragment in front).
        private static string cloudPartial = string.Empty;
        private static readonly System.Threading.Timer fragmentTimer = new(_ => FlushFragment());
        private static readonly TimeSpan FRAGMENT_HOLD = TimeSpan.FromSeconds(4);

        private static bool HasCjk(string text) => text.Any(c => c >= 0x3000);

        private static bool IsFragment(string text) => HasCjk(text)
            ? text.Length < 6
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4;

        private static string JoinFragment(string text)
        {
            if (string.IsNullOrEmpty(heldFragment))
                return text;
            return heldFragment + (HasCjk(text) ? string.Empty : " ") + text;
        }

        // Text from the cloud recognizer: partial results while a sentence is spoken, then the final sentence.
        private static void OnCloudRecognized(string text, bool sentenceEnd)
        {
            if (Caption == null)
                return;
            lock (cloudLock)
            {
                // Asked under the lock: once "停止" has taken what was heard of the last sentence
                // (`FinishCurrentSentence`), a result that is still on its way must not add it a second time.
                if (!ClassSession.IsRunning)
                    return;
                ClassSession.LastCaptionTime = DateTime.Now;
                text = JoinFragment(text);
                Caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(text, TextUtil.VERYLONG_THRESHOLD);
                Caption.OverlayOriginalCaption = text;

                cloudPartial = sentenceEnd ? string.Empty : text;
                if (sentenceEnd)
                {
                    lastPreviewLength = 0;
                    if (IsFragment(text))
                    {
                        heldFragment = text;
                        fragmentTimer.Change(FRAGMENT_HOLD, Timeout.InfiniteTimeSpan);
                        return;
                    }
                    heldFragment = string.Empty;
                    fragmentTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    CommitCloudSentence(text);
                }
                else if (text.Length - lastPreviewLength >= 40)
                {
                    // Translate a preview now and then, so a long sentence doesn't leave the translation empty.
                    lastPreviewLength = text.Length;
                    Caption.OriginalCaption = text;
                    pendingTextQueue.Enqueue((text, false));
                }
            }
        }

        // Nothing followed the short fragment: keep it after all.
        private static void FlushFragment()
        {
            lock (cloudLock)
            {
                if (string.IsNullOrEmpty(heldFragment) || !ClassSession.IsRunning || Caption == null)
                    return;
                string text = heldFragment;
                heldFragment = string.Empty;
                CommitCloudSentence(text);
            }
        }

        private static void CommitCloudSentence(string text)
        {
            Caption.OriginalCaption = text;
            lock (committedSentences)
            {
                Commit(text);
            }
            pendingTextQueue.Enqueue((text, true));
        }

        // The class was stopped in the middle of a sentence: what was heard of it so far (it is on screen already)
        // becomes the last line of the transcript instead of being dropped.
        public static void FinishCurrentSentence(RecognitionEngine engine)
        {
            if (Caption == null)
                return;
            if (engine == RecognitionEngine.Aliyun)
            {
                lock (cloudLock)
                {
                    fragmentTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    string text = (cloudPartial.Length > 0 ? cloudPartial : heldFragment).Trim();
                    cloudPartial = string.Empty;
                    heldFragment = string.Empty;
                    lastPreviewLength = 0;
                    if (text.Length > 0)
                        CommitCloudSentence(text);
                }
                return;
            }

            string sentence = (Caption.OriginalCaption ?? string.Empty).Trim();
            if (sentence.Length == 0 || Array.IndexOf(TextUtil.PUNC_EOS, sentence[^1]) != -1)
                return;    // Finished sentences were queued when they were read.
            lock (committedSentences)
            {
                if (Encoding.UTF8.GetByteCount(sentence) >= TextUtil.SHORT_THRESHOLD && !IsCommitted(sentence))
                {
                    Commit(sentence);
                    pendingTextQueue.Enqueue((sentence, true));
                }
            }
        }

        static Translator()
        {
            if (!File.Exists(AppPaths.SettingFile))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
            CloudAsr.Recognized += OnCloudRecognized;

#if WINDOWS
            // LiveCaptions runs its own speech recognition all the time, so it is not started at all when the
            // cloud engine is used.
            if (setting.Lecture.Engine == RecognitionEngine.LiveCaptions)
            {
                try
                {
                    EnsureLiveCaptions();
                }
                catch (Exception ex)
                {
                    // E.g. a Windows without Live Captions. The program must still start: the other engine works
                    // without it, and starting a class says what is wrong.
                    ErrorLog.Write("LiveCaptions", ex);
                }
            }
#endif
        }

        // Reading LiveCaptions over UI Automation costs CPU on both sides, so it is read often only while the text
        // is changing. `idleCount` counts in steps of 25 ms whatever the actual interval.
        private const int ACTIVE_POLL_MS = 50;
        private const int IDLE_POLL_MS = 150;
        private const int FAST_POLL_TICKS = 16;

#if WINDOWS
        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;
            bool pauseHandled = false;
            string lastRawText = string.Empty;

            // The sentence still being spoken: translate a preview now and then, so the user is not waiting
            // for the full stop; when the speaker pauses without LiveCaptions adding a full stop, the sentence is over.
            void HandlePause()
            {
                if (Caption.OriginalCaption.Length == 0)
                    return;
                bool unfinished = Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) == -1;
                if (unfinished && idleCount >= Setting.MaxIdleInterval && !pauseHandled)
                {
                    pauseHandled = true;
                    syncCount = 0;
                    string sentence = Caption.OriginalCaption.Trim();
                    lock (committedSentences)
                    {
                        if (Encoding.UTF8.GetByteCount(sentence) >= TextUtil.SHORT_THRESHOLD && !IsCommitted(sentence))
                        {
                            Commit(sentence);
                            CheckAlreadyTranslated(sentence);
                            pendingTextQueue.Enqueue((sentence, true));
                        }
                    }
                }
                else if (unfinished && syncCount > Setting.MaxSyncInterval)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue((Caption.OriginalCaption, false));
                }
            }

            // The window read below may be replaced or closed by other threads at any time (a restart after the
            // microphone changed, the cloud engine closing LiveCaptions): work on the one read at the start of a round.
            void Forget(AutomationElement gone)
            {
                Interlocked.CompareExchange(ref window, null, gone);
            }

            int failures = 0;
            while (true)
            {
                var liveCaptions = Window;
                if (liveCaptions == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }
                if (!ClassSession.IsRunning || ClassSession.Engine != RecognitionEngine.LiveCaptions)
                {
                    Thread.Sleep(500);
                    continue;
                }

                try
                {
                    string fullText = string.Empty;
                    try
                    {
                        // Check LiveCaptions.exe still alive
                        _ = liveCaptions.Current.Name;
                        // Get the text recognized by LiveCaptions (10-20ms)
                        fullText = LiveCaptionsHandler.GetCaptions(liveCaptions);
                    }
                    catch (ElementNotAvailableException)
                    {
                        Forget(liveCaptions);
                        continue;
                    }
                    if (string.IsNullOrEmpty(fullText))
                    {
                        Thread.Sleep(IDLE_POLL_MS);
                        continue;
                    }
                    ClassSession.LastCaptionTime = DateTime.Now;

                    int pollMs = idleCount < FAST_POLL_TICKS ? ACTIVE_POLL_MS : IDLE_POLL_MS;
                    // Nothing changed (a pause, or silence): skip the parsing and only count the idle time.
                    if (string.CompareOrdinal(fullText, lastRawText) == 0)
                    {
                        idleCount += pollMs / 25;
                        HandlePause();
                        Thread.Sleep(pollMs);
                        continue;
                    }
                    lastRawText = fullText;

                    // Preprocess
                    fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                    fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                    fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                    fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                    // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                    // Replace redundant `\n` within sentences with comma or period.
                    fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                    // Prevent adding the last sentence from previous running to log cards
                    // before the first sentence is completed.
                    if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1)
                        ClearContexts();

                    // Get the last sentence.
                    int lastEOSIndex;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                        lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                    else
                        lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                    string latestCaption = fullText.Substring(lastEOSIndex + 1);

                    // If the last sentence is too short, extend it by adding the previous sentence.
                    // Note: LiveCaptions may generate multiple characters including EOS at once.
                    if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                    {
                        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                        latestCaption = fullText.Substring(lastEOSIndex + 1);
                    }

                    // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                    Caption.OverlayOriginalCaption = latestCaption;
                    for (int historyCount = Math.Min(Setting.DisplaySentences, Caption.ContextCount);
                         historyCount > 0 && lastEOSIndex > 0;
                         historyCount--)
                    {
                        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                        Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                    }

                    // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                    if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                    {
                        Caption.DisplayOriginalCaption = latestCaption;
                        // If the last sentence is too long, truncate it when displayed.
                        Caption.DisplayOriginalCaption =
                            TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                    }

                    // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                    int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                    if (lastEOS != -1)
                        latestCaption = latestCaption.Substring(0, lastEOS + 1);
                    // `OriginalCaption`: The sentence to be really translated.
                    if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                    {
                        Caption.OriginalCaption = latestCaption;

                        idleCount = 0;
                        pauseHandled = false;
                        if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) == -1 &&
                            Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                            syncCount++;
                    }
                    else
                        idleCount += pollMs / 25;

                    // Every finished sentence is translated once, including ones that scrolled by between reads.
                    if (QueueNewSentences(fullText))
                        syncCount = 0;

                    HandlePause();

                    failures = 0;
                    Thread.Sleep(pollMs);
                }
                catch (Exception ex)
                {
                    // One bad read must not end the loop: no caption would be read again until the program restarts.
                    ErrorLog.Write("SyncLoop", ex);
                    if (++failures >= 5)
                    {
                        // LiveCaptions is in a state it does not recover from: `TranslateLoop` starts it again.
                        failures = 0;
                        Forget(liveCaptions);
                    }
                    Thread.Sleep(500);
                }
            }
        }
#endif

        public static async Task TranslateLoop()
        {
#if WINDOWS
            DateTime nextRelaunch = DateTime.MinValue;
#endif
            while (true)
            {
                try
                {
#if WINDOWS
                    // Check LiveCaptions.exe still alive. It is only needed (and only kept running, since its own
                    // speech recognition costs power) for a class that uses the system engine.
                    if (Window == null && ClassSession.IsRunning &&
                        ClassSession.Engine == RecognitionEngine.LiveCaptions && DateTime.Now >= nextRelaunch)
                    {
                        Caption.DisplayTranslatedCaption = "[WARNING] 实时辅助字幕意外关闭，正在重启…";
                        try
                        {
                            EnsureLiveCaptions();
                            Caption.DisplayTranslatedCaption = "";
                        }
                        catch (Exception ex)
                        {
                            // Not again at once: every attempt starts a process and waits for its window.
                            nextRelaunch = DateTime.Now.AddSeconds(15);
                            ErrorLog.Write("LiveCaptions", ex);
                            Caption.DisplayTranslatedCaption = $"[WARNING] 实时辅助字幕没能重启（{ex.Message}），稍后再试…";
                        }
                    }
#endif

                    // Translate. `handling` is raised before the sentence leaves the queue, so that
                    // `WaitUntilIdle` never sees an empty queue while a sentence is on its way to the log.
                    Interlocked.Increment(ref handling);
                    try
                    {
                        if (pendingTextQueue.TryDequeue(out var pending))
                            await Handle(pending.Text, pending.Final);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref handling);
                    }
                }
                catch (Exception ex)
                {
                    // One bad sentence must not end the loop: nothing would be translated or logged again.
                    ErrorLog.Write("TranslateLoop", ex);
                }

                await Task.Delay(ClassSession.IsRunning || !pendingTextQueue.IsEmpty ? 40 : 300);
            }
        }

        private static int handling = 0;

        private static async Task Handle(string original, bool isFinal)
        {
            // LiveCaptions also hears our own speech when it plays on the default output device.
            if (Speaker.SuppressCaptions)
                return;

            if (LogOnlyFlag)
            {
                if (isFinal)
                    await LogOnly(original);
            }
            else if (Setting.Lecture.SkipTranslation)
            {
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = string.Empty;
                if (isFinal)
                    await Log(original, string.Empty);
            }
            else
                translationTaskQueue.Enqueue(token => Task.Run(
                    () => Translate(original, token), token), original, isFinal);
        }

        // Waits (up to `timeout`) until every sentence recognized so far is in the history: translated, or failed,
        // and logged. Translations take a moment, so right after a class stops its last sentences are not there yet.
        public static async Task WaitUntilIdle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;
                if (!pendingTextQueue.IsEmpty || Volatile.Read(ref handling) > 0)
                {
                    await Task.Delay(40);
                    continue;
                }
                var logged = translationTaskQueue.Logged;
                if (await Task.WhenAny(logged, Task.Delay(remaining)) != logged)
                    return;
                // Nothing new came in while waiting: done.
                if (pendingTextQueue.IsEmpty && Volatile.Read(ref handling) == 0 &&
                    ReferenceEquals(logged, translationTaskQueue.Logged))
                    return;
            }
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                bool hold = false;
                try
                {
                    lock (displayLock)
                    {
                        hold = ShowNewestTranslation();
                    }
                }
                catch (Exception ex)
                {
                    // One bad result must not end the loop: no translation would be shown again.
                    ErrorLog.Write("DisplayLoop", ex);
                }

                // The translation of a finished sentence stays for a moment, so that it can be read before the
                // preview of the next sentence takes its place. Once for every sentence: while it is the newest
                // result the loop keeps looking, or the next one would wait for a pause that has long been over.
                if (hold)
                    await Task.Delay(720);
                // Translations still arrive for a moment after stopping; after that nothing changes here.
                await Task.Delay(ClassSession.IsRunning || translationTaskQueue.IsBusy ? 40 : 300);
            }
        }

        // Puts the newest translation on the caption page and the floating windows. Returns whether that was the
        // translation of a finished sentence which was not on screen yet.
        private static bool ShowNewestTranslation()
        {
            var (translatedText, isFinished, source) = translationTaskQueue.Output;

            if (Setting.Lecture.SkipTranslation)
            {
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = string.Empty;
                Caption.OverlayNoticePrefix = string.Empty;
                Caption.OverlayCurrentTranslation = string.Empty;
                return false;
            }
            if (LogOnlyFlag)
            {
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = "[已暂停]";
                Caption.OverlayNoticePrefix = "[已暂停]";
                Caption.OverlayCurrentTranslation = string.Empty;
                return false;
            }
            if (string.CompareOrdinal(Caption.TranslatedCaption, translatedText) == 0)
            {
                // A new original can get the same translation, e.g. two short acknowledgements.
                Caption.OverlayTranslationSource = source;
                return false;
            }
            if (string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(translatedText, string.Empty).Trim()))
                return false;

            // Main page
            Caption.TranslatedCaption = translatedText;
            Caption.DisplayTranslatedCaption =
                TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

            // Overlay window
            Caption.OverlayTranslationSource = source;
            if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
            {
                Caption.OverlayNoticePrefix = string.Empty;
                Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
            }
            else
            {
                var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
            }
            return isFinished;
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = text.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Not cancelled from here: the request ran into its own time limit. The sentence gets a result
                // like any other failure, or it would not be shown and never reach the transcript.
                return ("[ERROR] 翻译失败：请求超时，请换一个更快的接口或检查网络。", isChoke);
            }
            catch (Exception ex)
            {
                return ($"[ERROR] 翻译失败：{ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 写入历史记录失败。", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText)
        {
            try
            {
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 写入历史记录失败。", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            // The row before the first sentence of a class is the end of an earlier class, not context for this one.
            if (lastLog == null || lastLog.Id <= ClassSession.FirstHistoryId)
                return;
            Caption?.AddContext(lastLog);
        }

        // Closes LiveCaptions when it is not needed (cloud engine), to save the power its recognition uses; and
        // for it to start again with, e.g., another default microphone: `TranslateLoop` starts it again for a
        // running class, otherwise the next class or the settings page does.
        public static void CloseLiveCaptions()
        {
#if WINDOWS
            // Not while it is being started, and it is not started while it is being closed: whoever asks for it
            // in between (`EnsureLiveCaptions`) would be handed the window that is on its way out.
            lock (launchLock)
            {
                var current = Window;
                if (current == null)
                    return;
                try
                {
                    LiveCaptionsHandler.KillLiveCaptions(current);
                }
                catch (Exception)
                {
                    // Best effort: LiveCaptions may already be closed.
                }
                // Forgotten, so that nothing works on a window that is gone. (Unless the loop that reads it
                // has done so already.)
                Interlocked.CompareExchange(ref window, null, current);
            }
#endif
        }

#if WINDOWS
        private static readonly object launchLock = new();

        // The window of LiveCaptions, which is started (out of the way) if it is not running: for a class, or
        // to show it for its first-time setup. One caller at a time: starting it closes every LiveCaptions
        // there is, the one another caller has just started included.
        public static AutomationElement EnsureLiveCaptions()
        {
            lock (launchLock)
            {
                var current = Window;
                if (current != null)
                {
                    try
                    {
                        _ = current.Current.Name;
                        return current;
                    }
                    catch (ElementNotAvailableException)
                    {
                        // The cached window is gone (closed by hand); start it again.
                    }
                }
                current = LiveCaptionsHandler.LaunchLiveCaptions();
                try
                {
                    LiveCaptionsHandler.FixLiveCaptions(current);
                    LiveCaptionsHandler.HideLiveCaptions(current);
                }
                catch (Exception)
                {
                    // Not kept half set up: it would stay on screen where Windows put it and count as running,
                    // so that nothing would start it again. The next caller does.
                    try
                    {
                        LiveCaptionsHandler.KillLiveCaptions(current);
                    }
                    catch (Exception)
                    {
                        // It is gone already.
                    }
                    throw;
                }
                Window = current;
                return current;
            }
        }
#endif

        public static void ClearContexts()
        {
            Caption?.ClearContexts();
        }

    }
}
