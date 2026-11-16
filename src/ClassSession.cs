using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    // Start / stop of a class: captions are only read and translated while a session runs.
    public static class ClassSession
    {
        public const string IDLE_HINT = "点击右上角的“开始”按钮开始上课。";
        private const int NO_CAPTION_WARNING_SECONDS = 15;
        // How long the last sentences of a class may take to be translated and logged (a request times out
        // after 8 to 15 seconds).
        private static readonly TimeSpan LAST_SENTENCES_TIMEOUT = TimeSpan.FromSeconds(20);

        private static int generation = 0;
        private static RecognitionEngine? startedEngine;
        // The last start came to nothing, and the page says why.
        private static bool startFailed = false;
        // Held where a class begins to start, and where another thread puts something on the page that depends on
        // whether a class runs (the idle hint, what the recognizer has to say): so that neither lands on a class
        // that has begun, or ended, in the meantime.
        private static readonly object stateLock = new();

        public static bool IsRunning { get; private set; } = false;
        public static bool IsStarting { get; private set; } = false;
        public static DateTime LastCaptionTime { get; set; } = DateTime.MinValue;
        // History rows after this id belong to the current class; -1 before the first start.
        public static long FirstHistoryId { get; private set; } = -1;
        // The recording in the history that the running class is saved as; -1 if none.
        public static long CurrentLectureId { get; private set; } = -1;
        // The engine of the running (or last) class. Choosing another one in the settings takes effect with the
        // next class: whatever reads the captions goes by this, not by the setting.
        public static RecognitionEngine Engine => startedEngine ?? Translator.Setting.Lecture.Engine;

        public static event Action? StateChanged;

        public static async Task Start()
        {
            lock (stateLock)
            {
                if (IsRunning || IsStarting)
                    return;
                IsStarting = true;
                startFailed = false;
            }
            StateChanged?.Invoke();
            var engine = Translator.Setting.Lecture.Engine;
            bool cloud = engine == RecognitionEngine.Aliyun;
            // The recording made for this class, once there is one: `CurrentLectureId` is still that of the class
            // before until then.
            long startedLecture = -1;
            try
            {
                // The last sentence of the class before would stay on the page and hide the hints below. Not on
                // the UI thread: the reset waits for the display loop, and whatever listens to the caption runs
                // inside that loop's lock. A listener that needs the UI thread would then never return.
                await Task.Run(Translator.ResetDisplay);
                if (cloud && string.IsNullOrWhiteSpace(Translator.Setting.Lecture.AsrApiKey))
                {
                    SetHint("识别方式选了阿里云，但还没有填阿里云 API Key：请到“设置”页的“⓪ 输入源”里填写。");
                    startFailed = true;
                    return;
                }
                SetHint(cloud ? "正在连接阿里云语音识别……" : "正在启动实时辅助字幕……");
#if WINDOWS
                string? problem = cloud ? null : await Task.Run(Prepare);
#else
                string? problem = null;
#endif
                try
                {
                    // Sentences of a class stopped a moment ago are logged first, so they stay in that class.
                    await Translator.WaitUntilIdle(LAST_SENTENCES_TIMEOUT);
                    FirstHistoryId = await SectionLogger.GetMaxHistoryId();
                }
                catch (Exception)
                {
                    FirstHistoryId = 0;
                }
                // Once more: those last sentences were shown, and kept as context for the next translations,
                // while they came in.
                await Task.Run(Translator.ResetDisplay);

                startedEngine = engine;
                LastCaptionTime = DateTime.MinValue;
                Translator.ResetSentences();
                Refiner.Reset(FirstHistoryId);
                Summarizer.StartAt(FirstHistoryId);
                if (cloud)
                    // Not needed, and its own recognition would only cost power. (Closing it waits for its
                    // process to end, and opening the sound device below takes a moment: not on the UI thread.)
                    await Task.Run(Translator.CloseLiveCaptions);
                await StartLecture();
                startedLecture = CurrentLectureId;
                IsRunning = true;
                if (cloud)
                {
                    bool useMicrophone = Translator.Setting.Lecture.InputSource == InputSource.Microphone;
                    await Task.Run(() => CloudAsr.Start(useMicrophone));
                }
                SetHint(problem ?? "已开始，正在听……说话或播放课程声音后，原文会出现在这里。");
                _ = WatchForSilence(++generation, problem);
            }
            catch (Exception ex)
            {
                // Nothing may be left half running: the button would say "停止" for a class that never started.
                IsRunning = false;
                generation++;
                ErrorLog.Write("ClassSession.Start", ex);
                try
                {
                    CloudAsr.Stop();
                    ClearSourceWarning();
                    // Only the recording made for this start. Before there is one, the id is that of the class
                    // before, whose last sentences may still be on their way: it is not for this start to judge
                    // that one empty.
                    if (startedLecture >= 0)
                    {
                        CurrentLectureId = -1;
                        await LectureStore.DeleteIfEmpty(startedLecture);
                    }
                }
                catch (Exception cleanup)
                {
                    ErrorLog.Write("ClassSession.Start", cleanup);
                }
                startFailed = true;
                SetHint(cloud
                    ? $"启动失败：{ex.Message}"
                    : $"启动失败：{ex.Message}\n请到“设置”页点“显示系统实时辅助字幕”，确认系统的实时辅助字幕能正常打开；" +
                      "也可以把“识别方式”改成阿里云，不依赖系统的实时辅助字幕。");
            }
            finally
            {
                IsStarting = false;
                StateChanged?.Invoke();
            }
        }

        // Every class gets its own entry in the history, in the course chosen last time.
        private static async Task StartLecture()
        {
            var now = DateTime.Now;
            string course = LectureStore.CleanCourseName(Translator.Setting.Lecture.CurrentCourse);
            try
            {
                CurrentLectureId = await LectureStore.StartLecture(course, $"{now:yyyy-MM-dd HH:mm} 课堂", now, FirstHistoryId);
            }
            catch (Exception)
            {
                CurrentLectureId = -1;
            }
        }

        // After `Stop`: waits for the last sentences to be translated, closes the recording and summarizes its
        // last section. Returns the recording, or null if there is none.
        public static async Task<LectureRecord?> FinishLecture(long lectureId)
        {
            if (lectureId < 0)
                return null;
            // Translations still on their way are logged a moment after stopping: they belong to this class.
            await Translator.WaitUntilIdle(LAST_SENTENCES_TIMEOUT);
            long last = await SectionLogger.GetMaxHistoryId();
            // A new class was started meanwhile: its sentences are not part of this one.
            if (IsRunning && CurrentLectureId != lectureId)
                last = Math.Min(last, FirstHistoryId);
            await LectureStore.FinishLecture(lectureId, DateTime.Now, last);
            try
            {
                await Summarizer.FinishLecture(last);
            }
            catch (Exception)
            {
                // The transcript is saved anyway; only the summary of the last section is missing.
            }
            return await LectureStore.GetLecture(lectureId);
        }

        public static void Stop()
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            generation++;
            try
            {
                Translator.FinishCurrentSentence(Engine);
            }
            catch (Exception ex)
            {
                // The class stops in any case.
                ErrorLog.Write("ClassSession.Stop", ex);
            }
            CloudAsr.Stop();
            ClearSourceWarning();
            Speaker.StopAll();
            SetHint("已停止。点击右上角的“开始”继续。");
            StateChanged?.Invoke();
        }

#if WINDOWS
        // Makes sure LiveCaptions runs and listens to the chosen input. Returns a warning to show, if any.
        private static string? Prepare()
        {
            var window = Translator.EnsureLiveCaptions();

            bool useMicrophone = Translator.Setting.Lecture.InputSource == InputSource.Microphone;
            SetHint(useMicrophone ? "正在打开实时字幕的“包含麦克风音频”……" : "正在关闭实时字幕的“包含麦克风音频”……");
            bool microphoneSet;
            try
            {
                microphoneSet = LiveCaptionsMicrophone.Set(window, useMicrophone);
            }
            catch (Exception)
            {
                microphoneSet = false;
            }

            if (!LiveCaptionsHandler.HasCaptionsTextBlock(window))
            {
                return "找不到实时辅助字幕的文字区域，可能是系统字幕还没完成首次设置。" +
                       "请到“设置”页点“显示系统实时辅助字幕”，按系统提示下载语音识别文件后再点“开始”。";
            }

            if (!microphoneSet && useMicrophone)
            {
                LiveCaptionsMicrophone.ShowSettings(window);
                return "已开始，但没能自动打开麦克风。已弹出实时辅助字幕的设置菜单：" +
                       "请在“首选项”里勾选“包含麦克风音频”，再点本工具“设置”页的“隐藏系统实时辅助字幕”。";
            }
            return null;
        }
#endif

        private static async Task WatchForSilence(int session, string? problem)
        {
            await Task.Delay(TimeSpan.FromSeconds(NO_CAPTION_WARNING_SECONDS));
            if (session != generation || !IsRunning || LastCaptionTime != DateTime.MinValue)
                return;

            bool useMicrophone = Translator.Setting.Lecture.InputSource == InputSource.Microphone;
            if (Engine == RecognitionEngine.Aliyun)
            {
                // The recognizer has said what is wrong itself (no connection, the quota is used up): it is not
                // the sound, which the advice below is about.
                if (!string.IsNullOrEmpty(Translator.Caption?.SourceWarning))
                {
                    SetHint($"已经 {NO_CAPTION_WARNING_SECONDS} 秒没有识别到文字，原因见上方的提示。");
                    return;
                }
                SetHint($"已经 {NO_CAPTION_WARNING_SECONDS} 秒没有识别到文字。" + (useMicrophone
#if WINDOWS
                    ? "现在听的是 Windows 默认麦克风，请确认它没有静音，并在“设置 ⓪”里选对了麦克风。"
                    : "现在听的是电脑正在播放的声音（Windows 默认输出设备），请确认课程声音正在播放、没有静音。"));
#else
                    ? "现在听的是系统默认麦克风，请确认它没有静音，并在系统设置里允许本程序使用麦克风。"
                    : "现在听的是电脑正在播放的声音，请确认课程声音正在播放、没有静音。"));
#endif
                return;
            }
            string advice = useMicrophone
                ? "现在听的是麦克风。请确认实时辅助字幕“首选项”里已勾选“包含麦克风音频”，" +
                  "并在“设置 ⓪”里选对了麦克风（不要选蓝牙耳机的麦克风）。"
                : "现在只听电脑播放的声音，线下上课请在“设置 ⓪”里改成“麦克风”。";
            SetHint($"已经 {NO_CAPTION_WARNING_SECONDS} 秒没有识别到文字。{advice}" +
                    "\n也可以到“设置”页点“显示系统实时辅助字幕”，看系统的实时辅助字幕窗口里有没有出字。" +
                    (problem == null ? string.Empty : $"\n{problem}"));
        }

        static ClassSession()
        {
            // Problems of the cloud recognizer (the connection, the audio capture) are shown above the current
            // sentence, and taken away when it says they are over.
            string shownStatus = string.Empty;
            CloudAsr.StatusChanged += status =>
            {
                // Under the lock, so that what is said while the class stops is not shown after it has.
                lock (stateLock)
                {
                    if (Translator.Caption == null || !IsRunning)
                        return;
                    if (!string.IsNullOrEmpty(status))
                        Translator.Caption.SourceWarning = status;
                    else if (Translator.Caption.SourceWarning == shownStatus)
                        // Only what the recognizer said itself: another warning may be shown by now.
                        Translator.Caption.SourceWarning = string.Empty;
                    shownStatus = status;
                }
            };

            // The recording of the class that just ended was deleted (thrown away after "停止", or in the
            // history): its sentences leave the caption page and the floating windows as well. Not when the next
            // class is on screen already.
            LectureStore.LectureDeleted += id =>
            {
                if (id != CurrentLectureId || IsRunning || IsStarting)
                    return;
                CurrentLectureId = -1;
                // Not on this thread, which may be the UI thread (see `Start`).
                Task.Run(() =>
                {
                    if (IsRunning || IsStarting)
                        return;
                    Translator.ResetDisplay();
                    lock (stateLock)
                    {
                        // A class begun just now has its own hints.
                        if (IsRunning || IsStarting)
                            return;
                        // So has a start that failed after that class: the page was emptied for it, and says
                        // what went wrong.
                        if (!startFailed)
                            SetHint(IDLE_HINT);
                    }
                    StateChanged?.Invoke();
                });
            };
        }

        private static void SetHint(string text)
        {
            if (Translator.Caption != null)
                Translator.Caption.StatusHint = text;
        }

        // With the class, what was wrong with its recognition is over: "正在重连……" would stay for ever, since
        // nothing is listening any more that could say it has worked.
        private static void ClearSourceWarning()
        {
            lock (stateLock)
            {
                if (Translator.Caption != null)
                    Translator.Caption.SourceWarning = string.Empty;
            }
        }
    }
}
