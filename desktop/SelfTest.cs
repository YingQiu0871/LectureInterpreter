using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LiveCaptionsTranslator
{
    // `LectureInterpreter --selftest`: exercises the core without any window and exits with 0 if it all works.
    // It runs in a temporary data folder, so it never touches the user's settings and history.
    public static class SelfTest
    {
        public static int Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "lct-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("LCT_DATA_DIR", directory);
            int failures = 0;

            void Check(string name, Func<string> test)
            {
                try
                {
                    Console.WriteLine($"[ OK ] {name}: {test()}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"Platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                              $"({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
            Console.WriteLine($"Data folder: {AppPaths.DataDirectory}");

            Check("provider error text", () =>
            {
                string e = TextUtil.ProviderError("{\"error\":{\"message\":\"Insufficient Balance\"}}",
                    "https://api.deepseek.com/v1/chat/completions");
                if (e != "：Insufficient Balance（服务器：api.deepseek.com）")
                    throw new Exception("detail: " + e);
                if (TextUtil.ProviderError("<html>x</html>", "not a url") != string.Empty)
                    throw new Exception("html or bad url not skipped");
                if (!TextUtil.PaymentRequiredMessage("openrouter.ai", "{\"error\":{\"message\":\"Insufficient credits\"}}").Contains("openrouter.ai 要求付费（HTTP 402），通常是这个服务商的账户余额不足（原文：Insufficient credits）"))
                    throw new Exception("402 message lacks host");
                if (TextUtil.ErrorDetail("{\"code\":\"Arrearage\",\"message\":\"Access denied.\"}") != "Access denied.")
                    throw new Exception("top-level message not read");
                return e;
            });

            Check("settings", () =>
            {
                var setting = Translator.Setting ?? throw new InvalidOperationException("no settings");
                setting.Save();
                if (!File.Exists(AppPaths.SettingFile))
                    throw new FileNotFoundException("setting.json was not written");
                var reloaded = Setting.Load();
                if (reloaded.Lecture.Engine != RecognitionEngine.Aliyun)
                    throw new InvalidOperationException("the cloud engine is not forced");
                return $"engine {reloaded.Lecture.Engine}, target {reloaded.TargetLanguage}";
            });

            Check("sqlite (courses and lectures)", () =>
            {
                SectionLogger.EnsureTables();
                long courseId = LectureStore.AddCourse("自测课程").GetAwaiter().GetResult();
                long lectureId = LectureStore.StartLecture("自测课程", "自测", DateTime.Now, 0).GetAwaiter().GetResult();
                LectureStore.FinishLecture(lectureId, DateTime.Now, 0).GetAwaiter().GetResult();
                var lecture = LectureStore.GetLecture(lectureId).GetAwaiter().GetResult()
                              ?? throw new InvalidOperationException("lecture not found");
                var courses = LectureStore.LoadCourses().GetAwaiter().GetResult();
                return $"course {courseId}, lecture {lecture.Id} \"{lecture.Name}\", {courses.Count} course(s)";
            });

            Check("history", () =>
            {
                SQLiteHistoryLogger.LogTranslation("hello", "你好", "zh-CN", "SelfTest").GetAwaiter().GetResult();
                long last = SectionLogger.GetMaxHistoryId().GetAwaiter().GetResult();
                if (last < 1)
                    throw new InvalidOperationException("no history row");
                SQLiteHistoryLogger.LogTranslation("second", "第二", "zh-CN", "SelfTest").GetAwaiter().GetResult();
                var tail = SectionLogger.LoadLastHistory(0, 1).GetAwaiter().GetResult();
                if (tail.Count != 1 || tail[0].SourceText != "second")
                    throw new InvalidOperationException("last history row not found");
                return $"last history id {last}";
            });

            Check("settings file", () =>
            {
                // A file from another version, or edited by hand: an API that does not exist, a slot number past
                // the end of its list.
                string path = Path.Combine(directory, "odd-setting.json");
                new Setting().Save(path);
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                json["ApiName"] = "NoSuchApi";
                json["ConfigIndices"]!["OpenAI"] = 7;
                json["Configs"]!["OpenAI"]![0]!["ModelName"] = "m1";
                File.WriteAllText(path, json.ToJsonString());

                var odd = Setting.Load(path);
                if (odd.ApiName != new Setting().ApiName)
                    throw new InvalidOperationException($"the unknown API \"{odd.ApiName}\" was kept");
                if (odd.ConfigIndices["OpenAI"] != 0 || odd["OpenAI"] is not OpenAIConfig { ModelName: "m1" })
                    throw new InvalidOperationException("a slot number past the end was kept");
                // The settings window removes slots while translations read them: no error in between.
                odd.ConfigIndices["OpenAI"] = 5;
                if (odd["OpenAI"] is not OpenAIConfig { ModelName: "m1" } || odd["NoSuchApi"] == null)
                    throw new InvalidOperationException("reading a slot by a wrong number failed");
                odd.ConfigIndices["OpenAI"] = 0;
                odd.Save(path);
                if (File.Exists(path + ".tmp") || Setting.Load(path)["OpenAI"] is not OpenAIConfig { ModelName: "m1" })
                    throw new InvalidOperationException("the file was not written back whole");
                return "unknown API and slot number corrected";
            });

            Check("stored translations", () =>
            {
                // What the history holds as a translation, and what of it is one.
                var cases = new (string Stored, string Translation)[]
                {
                    ("你好", "你好"),
                    (" 你好 ", "你好"),
                    ("[ 123 ms] 你好", "你好"),
                    ("[ 123 ms] 见 [1] 和 [2]", "见 [1] 和 [2]"),
                    ("N/A", ""),
                    ("[ERROR] 翻译失败：超时", ""),
                    ("[ 123 ms] [ERROR] 翻译失败：超时", ""),
                    ("[WARNING] Unsupported API", ""),
                    ("", ""),
                };
                foreach (var (stored, translation) in cases)
                {
                    string cleaned = LectureDocument.CleanTranslation(stored);
                    if (cleaned != translation)
                        throw new InvalidOperationException($"\"{stored}\" gives \"{cleaned}\"");
                }
                var parts = RegexPatterns.NoticePrefixAndTranslation().Match("[ 12 ms] 第一行 [注]\n第二行");
                if (parts.Groups[1].Value != "[ 12 ms] " || parts.Groups[2].Value != "第一行 [注]\n第二行")
                    throw new InvalidOperationException("the notice in front is not told from the translation");
                if (RegexPatterns.ModelThinking().Replace("<think>想\n一下</think>\n译文", "") != "译文" ||
                    RegexPatterns.ModelThinking().Replace("<think>没想完", "") != "")
                    throw new InvalidOperationException("a model's thinking is left in its answer");

                var time = new DateTime(2026, 1, 10, 9, 0, 0);
                var lines = Enumerable.Range(1, 5)
                    .Select(i => new HistoryLine { Id = i, Time = time, SourceText = $"s{i}", TranslatedText = $"t{i}" })
                    .ToList();
                var paragraphs = new List<ParagraphEntry>
                {
                    new() { Time = time, FirstHistoryId = 2, LastHistoryId = 4, Source = "s2 s3 s4", Translation = "段落" },
                };
                var merged = LectureDocument.MergeParagraphs(lines, paragraphs, fadeUnrefined: true);
                if (merged.Count != 3 || merged[1].Id != 2 || merged[1].TranslatedText != "段落" ||
                    merged[1].Opacity != 1.0 || merged[2].Id != 5 || merged[2].Opacity >= 1.0)
                    throw new InvalidOperationException("a paragraph does not replace its sentences");
                return $"{cases.Length} cases";
            });

            Check("caption", () =>
            {
                var caption = Translator.Caption ?? throw new InvalidOperationException("no caption");
                caption.Reset();
                TranslationHistoryEntry Entry(int i, string translation) => new()
                {
                    Timestamp = string.Empty,
                    TimestampFull = string.Empty,
                    SourceText = $"Sentence {i}.",
                    TranslatedText = translation,
                    TargetLanguage = "zh-CN",
                    ApiUsed = "SelfTest",
                };
                for (int i = 1; i <= Caption.MAX_CONTEXTS + 2; i++)
                    caption.AddContext(Entry(i, i == 11 ? "[ERROR] 翻译失败：超时" : $"[ 9 ms] 第{i}句。"));
                if (caption.ContextCount != Caption.MAX_CONTEXTS)
                    throw new InvalidOperationException($"{caption.ContextCount} contexts are kept");
                // The newest sentences, oldest first, without the failed one.
                string sources = string.Join("|", caption.GetPreviousContexts(3).Select(entry => entry.SourceText));
                if (sources != "Sentence 10.|Sentence 12.")
                    throw new InvalidOperationException("contexts: " + sources);
                if (caption.GetPreviousText(3, TextType.Translation) != "第10句。第12句。" ||
                    caption.GetPreviousText(2, TextType.Caption) != "Sentence 12. ")
                    throw new InvalidOperationException("previous text: \"" +
                        caption.GetPreviousText(3, TextType.Translation) + "\", \"" +
                        caption.GetPreviousText(2, TextType.Caption) + "\"");

                // The texts on screen are assigned many times a second: only a new text is announced.
                int announced = 0;
                void Count(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => announced++;
                caption.PropertyChanged += Count;
                try
                {
                    caption.DisplayOriginalCaption = "same";
                    caption.DisplayOriginalCaption = "same";
                    caption.OverlayCurrentTranslation = "同一句";
                    caption.OverlayCurrentTranslation = "同一句";
                }
                finally
                {
                    caption.PropertyChanged -= Count;
                }
                if (announced != 2)
                    throw new InvalidOperationException($"{announced} announcements for 2 new texts");
                caption.Reset();
                if (caption.ContextCount != 0 || caption.DisplayOriginalCaption != string.Empty)
                    throw new InvalidOperationException("a new class does not start empty");
                return "contexts oldest first, unchanged texts not announced";
            });

            Check("transcript list", () =>
            {
                var view = new TranscriptView();
                int updates = 0, changes = 0;
                view.Updated += () => updates++;
                view.Lines.CollectionChanged += (s, e) => changes++;
                var time = new DateTime(2026, 1, 10, 9, 0, 0);
                HistoryLine Line(long id, string text, double opacity = 0.6) =>
                    new() { Id = id, Time = time, SourceText = text, TranslatedText = text, Opacity = opacity };

                var first = new List<HistoryLine> { Line(1, "a", 1.0), Line(2, "b"), Line(3, "c") };
                if (!view.Apply(first) || view.Lines.Count != 3 || updates != 1)
                    throw new InvalidOperationException("the first sentences are not listed");
                // Nothing new: the page is left alone.
                changes = 0;
                if (view.Apply(new List<HistoryLine> { Line(1, "a", 1.0), Line(2, "b"), Line(3, "c") }) ||
                    changes != 0 || updates != 1)
                    throw new InvalidOperationException("an unchanged list was touched");
                // One more sentence is one more row.
                view.Apply(new List<HistoryLine> { Line(1, "a", 1.0), Line(2, "b"), Line(3, "c"), Line(4, "d") });
                if (changes != 1 || view.Lines.Count != 4 || !ReferenceEquals(view.Lines[0], first[0]))
                    throw new InvalidOperationException($"one new sentence made {changes} changes");
                // Sentences 2 and 3 became a paragraph.
                view.Apply(new List<HistoryLine> { Line(1, "a", 1.0), Line(2, "b c", 1.0), Line(4, "d") });
                if (view.Lines.Count != 3 || view.Lines[1].SourceText != "b c" || view.Lines[2].Id != 4 ||
                    !ReferenceEquals(view.Lines[0], first[0]))
                    throw new InvalidOperationException("a paragraph did not replace its sentences");
                // Another class, then none.
                changes = 0;
                view.Apply(new List<HistoryLine> { Line(9, "x") });
                if (view.Lines.Count != 1 || view.Lines[0].Id != 9 || changes != 2)
                    throw new InvalidOperationException($"another class made {changes} changes");
                view.Apply(new List<HistoryLine>());
                if (view.Lines.Count != 0)
                    throw new InvalidOperationException("the list is not emptied");
                return $"{updates} updates";
            });

            Check("lectures without sentences", () =>
            {
                T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
                // A class that ended before anything was said (the program was closed), then one with sentences.
                long before = Wait(SectionLogger.GetMaxHistoryId());
                long emptyId = Wait(LectureStore.StartLecture("自测课程", "没说话", DateTime.Now, before));
                long nextId = Wait(LectureStore.StartLecture("自测课程", "下一节", DateTime.Now, before));
                SQLiteHistoryLogger.LogTranslation("third", "第三", "zh-CN", "SelfTest").GetAwaiter().GetResult();
                SQLiteHistoryLogger.LogTranslation("fourth", "第四", "zh-CN", "SelfTest").GetAwaiter().GetResult();
                long last = Wait(SectionLogger.GetMaxHistoryId());
                LectureStore.FinishLecture(nextId, DateTime.Now, last).GetAwaiter().GetResult();
                var empty = Wait(LectureStore.GetLecture(emptyId)) ?? throw new InvalidOperationException("lecture not found");
                var next = Wait(LectureStore.GetLecture(nextId)) ?? throw new InvalidOperationException("lecture not found");
                if (Wait(LectureStore.EffectiveLastHistoryId(empty)) != before || Wait(LectureStore.HasSentences(empty)) ||
                    !Wait(LectureStore.HasSentences(next)))
                    throw new InvalidOperationException("the empty lecture reaches into the next one");

                long deleted = -1;
                void OnDeleted(long id) => deleted = id;
                LectureStore.LectureDeleted += OnDeleted;
                try
                {
                    if (!Wait(LectureStore.DeleteIfEmpty(emptyId)) || deleted != emptyId)
                        throw new InvalidOperationException("the empty lecture was not deleted");
                    if (Wait(LectureStore.DeleteIfEmpty(nextId)))
                        throw new InvalidOperationException("a lecture with sentences was deleted");
                }
                finally
                {
                    LectureStore.LectureDeleted -= OnDeleted;
                }
                if (Wait(SectionLogger.LoadHistoryRange(before, last)).Count != 2)
                    throw new InvalidOperationException("deleting the empty lecture took the next one's sentences");
                return $"lecture {emptyId} deleted, lecture {nextId} kept";
            });

            Check("lecture file", () =>
            {
                T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
                var setting = Translator.Setting ?? throw new InvalidOperationException("no settings");
                setting.Lecture.SaveFolder = Path.Combine(directory, "save");
                long before = Wait(SectionLogger.GetMaxHistoryId());
                long id = Wait(LectureStore.StartLecture("自测课程", "文件", DateTime.Now, before));
                foreach (var (source, translation) in new[]
                         {
                             ("It works.", "[ 9 ms] 可以用。"), ("It failed.", "[ERROR] 翻译失败：超时"), ("Paused.", "N/A"),
                         })
                    SQLiteHistoryLogger.LogTranslation(source, translation, "zh-CN", "SelfTest").GetAwaiter().GetResult();
                LectureStore.FinishLecture(id, DateTime.Now, Wait(SectionLogger.GetMaxHistoryId())).GetAwaiter().GetResult();
                var lecture = Wait(LectureStore.GetLecture(id)) ?? throw new InvalidOperationException("lecture not found");

                string path = Wait(LectureDocument.Save(lecture));
                string text = File.ReadAllText(path);
                if (!text.Contains("> 可以用。") || !text.Contains("It failed.") || !text.Contains("Paused.") ||
                    text.Contains("翻译失败") || text.Contains("N/A") || text.Contains("ms]"))
                    throw new InvalidOperationException("the file holds something that is not a translation");

                // Renamed and moved to another course: the file under the old name goes, and the notes come along.
                string draft = LectureNotes.DraftPath(lecture);
                File.WriteAllText(draft, "notes");
                LectureStore.RenameLecture(id, "改了名", "另一门课").GetAwaiter().GetResult();
                lecture = Wait(LectureStore.GetLecture(id)) ?? throw new InvalidOperationException("lecture not found");
                string renamed = Wait(LectureDocument.Save(lecture));
                if (File.Exists(path) || !File.Exists(renamed) || File.Exists(draft) ||
                    !File.Exists(LectureNotes.DraftPath(lecture)) ||
                    Path.GetFileName(Path.GetDirectoryName(renamed)) != "另一门课")
                    throw new InvalidOperationException("the file or its notes did not follow the new name");
                return Path.GetFileName(renamed);
            });

            Check("pdf slides (PdfPig)", () =>
            {
                string path = Path.Combine(directory, "slides.pdf");
                var builder = new PdfDocumentBuilder();
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                for (int i = 1; i <= 2; i++)
                {
                    var page = builder.AddPage(595, 842);
                    page.AddText($"Slide {i}: dynamic programming splits a problem into overlapping subproblems " +
                                 "and stores their answers so that each is computed only once.",
                        12, new UglyToad.PdfPig.Core.PdfPoint(40, 780), font);
                }
                File.WriteAllBytes(path, builder.Build());
                // While it is read, a second file is asked for (from another page, say): that one is refused.
                Task? second = null;
                int changes = 0;
                void Count() => Interlocked.Increment(ref changes);
                SlideDeck.LoadingChanged += Count;
                try
                {
                    SlideDeck.Load(path, new InlineProgress(text => second ??= SlideDeck.Load(path))).GetAwaiter().GetResult();
                }
                finally
                {
                    SlideDeck.LoadingChanged -= Count;
                }
                if (!SlideDeck.IsLoaded)
                    throw new InvalidOperationException("no page read");
                if (second?.Exception?.InnerException is not InvalidOperationException || SlideDeck.IsLoading || changes != 2)
                    throw new InvalidOperationException("a second file was read on top of the first");
                return $"{SlideDeck.Pages.Count} page(s), first: \"{SlideDeck.Pages[0].Text[..Math.Min(40, SlideDeck.Pages[0].Text.Length)]}\"";
            });

            Check("asr quota fallback", () =>
            {
                if (!CloudAsr.IsQuotaError("AllocationQuota.FreeTierOnly") || !CloudAsr.IsQuotaError("Arrearage") ||
                    CloudAsr.IsQuotaError("InvalidApiKey") || CloudAsr.IsQuotaError("InvalidParameter") ||
                    CloudAsr.IsQuotaError("") || CloudAsr.IsQuotaError(null))
                    throw new InvalidOperationException("quota code classification is wrong");
                string order = string.Join(",", CloudAsr.CandidateModels("paraformer-realtime-v2", "en"));
                if (order != "paraformer-realtime-v2,fun-asr-realtime,fun-asr-realtime-2026-02-28")
                    throw new InvalidOperationException("order: " + order);
                var fr = CloudAsr.CandidateModels("paraformer-realtime-v2", "fr");
                if (fr.Contains("fun-asr-realtime-2026-02-28") || fr.Contains("paraformer-realtime-v1") ||
                    !fr.Contains("fun-asr-realtime"))
                    throw new InvalidOperationException("candidates for French: " + string.Join(",", fr));
                var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "paraformer-realtime-v2" };
                if (CloudAsr.NextModel("paraformer-realtime-v2", "fr", gone) != "fun-asr-realtime")
                    throw new InvalidOperationException("next model after v2 is wrong");
                gone.Add("fun-asr-realtime");
                if (CloudAsr.NextModel("paraformer-realtime-v2", "fr", gone) != null ||
                    CloudAsr.NextModel("paraformer-realtime-v2", "zh", gone) != "fun-asr-realtime-2026-02-28")
                    throw new InvalidOperationException("end of the list is wrong");
                if (CloudAsr.CandidateModels("my-model", "zh")[0] != "my-model")
                    throw new InvalidOperationException("configured model not first");
                // Spanish: Paraformer cannot, only the Fun-ASR alias can; a model we do not know is trusted.
                var es = CloudAsr.CandidateModels("paraformer-realtime-v2", "es");
                if (es.Count != 1 || es[0] != "fun-asr-realtime" || CloudAsr.CandidateModels("my-model", "es")[0] != "my-model")
                    throw new InvalidOperationException("candidates for Spanish: " + string.Join(",", es));
                if (CloudAsr.CandidateModels("paraformer-realtime-v2", "yue").Count != 2 ||
                    CloudAsr.CandidateModels("paraformer-realtime-v2", "").Count != 3 ||
                    CloudAsr.CandidateModels("paraformer-realtime-v2", "de-DE")[0] != "paraformer-realtime-v2")
                    throw new InvalidOperationException("language filtering is wrong");
                if (LectureLanguages.Find("de-DE")?.Tesseract != "deu" || LectureLanguages.Find("zh-Hant-TW")?.Code != "zh" ||
                    LectureLanguages.Find("") != null || LectureLanguages.EnglishName("xx") != "the original language of the lecture")
                    throw new InvalidOperationException("language table lookup is wrong");
                string message = CloudAsr.QuotaMessage("AllocationQuota.FreeTierOnly",
                    new[] { "paraformer-realtime-v2", "fun-asr-realtime" });
                if (!message.Contains("paraformer-realtime-v2、fun-asr-realtime，") ||
                    CloudAsr.QuotaMessage("Arrearage").Contains("用完即停"))
                    throw new InvalidOperationException("quota message: " + message);
                if (!TextUtil.IsArrears("{\"code\":\"Arrearage\"}") || TextUtil.IsArrears("{\"code\":\"x\"}"))
                    throw new InvalidOperationException("arrearage body classification is wrong");
                return order;
            });

            Check("update check (tags)", () =>
            {
                if (UpdateChecker.ParseTag("v1.7-lecture.12") != 12 || UpdateChecker.ParseTag("v1.7-lecture.x") != null ||
                    UpdateChecker.ParseTag("v1.7.3") != null || UpdateChecker.ParseTag("v2.0.180") != 180 ||
                    UpdateChecker.ParseTag(UpdateChecker.TagOf(7)) != 7 || UpdateChecker.ParseTag(null) != null)
                    throw new InvalidOperationException("release tag parsing is wrong");
                if (!UpdateChecker.IsNewer(12, 13) || UpdateChecker.IsNewer(13, 13) || UpdateChecker.IsNewer(14, 13) ||
                    UpdateChecker.IsNewer(null, 13))
                    throw new InvalidOperationException("build comparison is wrong");
                var releases = System.Text.Json.JsonDocument.Parse(
                    "[{\"tag_name\":\"v1.7-lecture.20\",\"draft\":true}," +
                    "{\"tag_name\":\"v1.7-lecture.19\",\"prerelease\":true}," +
                    "{\"tag_name\":\"nightly\"}," +
                    "{\"tag_name\":\"v1.7-lecture.9\",\"html_url\":\"https://example.com/9\"}," +
                    "{\"tag_name\":\"v1.7-lecture.12\",\"html_url\":\"https://example.com/12\"}," +
                    "{\"tag_name\":\"v1.7-lecture.10\"}]").RootElement;
                var picked = UpdateChecker.PickLatest(releases, 10);
                if (picked == null || picked.LatestBuild != 12 || !picked.IsNewer || picked.Url != "https://example.com/12" ||
                    UpdateChecker.PickLatest(System.Text.Json.JsonDocument.Parse("[]").RootElement, 10) != null)
                    throw new InvalidOperationException("choosing the highest release is wrong");
                var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
                if (UpdateChecker.IsCheckDue(now.AddHours(-1), now) || !UpdateChecker.IsCheckDue(now.AddHours(-25), now) ||
                    !UpdateChecker.IsCheckDue(now.AddHours(1), now))
                    throw new InvalidOperationException("check interval is wrong");
                if (UpdateChecker.ShouldAnnounce(12, now.AddDays(-1), 12, now) || !UpdateChecker.ShouldAnnounce(11, now, 12, now) ||
                    !UpdateChecker.ShouldAnnounce(12, now.AddDays(-4), 12, now))
                    throw new InvalidOperationException("announcement rule is wrong");
                return $"current build {(UpdateChecker.CurrentBuild?.ToString() ?? "none (development build)")}";
            });

            Check("markdown folder", () => LectureDocument.DefaultSaveFolder);

            foreach (string tool in new[] { "parec", "pactl", "say", "spd-say", "espeak-ng", "pdftoppm", "tesseract" })
                Console.WriteLine($"[INFO] {tool}: {(Exec.Find(tool) ?? "not found")}");
            Console.WriteLine($"[INFO] OCR languages: {string.Join(", ", SlideOcr.AvailableLanguages())}");

            try
            {
                // Settings changed above are still waiting for their save: not into a folder that is gone.
                Setting.Flush();
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // Removing the temporary folder is best effort.
            }
            Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures})");
            return failures == 0 ? 0 : 1;
        }

        // Reports at once, on the thread that reports (`Progress<T>` would post it for later).
        private sealed class InlineProgress(Action<string> report) : IProgress<string>
        {
            public void Report(string value) => report(value);
        }
    }
}
