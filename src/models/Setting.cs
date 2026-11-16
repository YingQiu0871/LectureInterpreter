using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
#if WINDOWS
using System.Windows;
#endif

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private string? ignoredUpdateVersion;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private BilingualWindowState bilingualWindowState;
        private LectureState lectureState;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = value;
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = value;
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public BilingualWindowState BilingualWindow
        {
            get => bilingualWindowState;
            set
            {
                bilingualWindowState = value ?? new BilingualWindowState();
                OnPropertyChanged("BilingualWindow");
            }
        }
        public LectureState Lecture
        {
            get => lectureState;
            set
            {
                lectureState = value ?? new LectureState();
                OnPropertyChanged("Lecture");
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        // The selected slot of an API. Translations read it on background threads while the settings window adds
        // and removes slots, so an index that is (for a moment) out of range gives the nearest slot instead of an error.
        public TranslateAPIConfig this[string key]
        {
            get
            {
                if (!configs.TryGetValue(key, out var slots) || slots == null ||
                    !configIndices.TryGetValue(key, out int index))
                    return new TranslateAPIConfig();
                int count = slots.Count;
                if (count == 0)
                    return new TranslateAPIConfig();
                try
                {
                    return slots[Math.Clamp(index, 0, count - 1)];
                }
                catch (ArgumentOutOfRangeException)
                {
                    return new TranslateAPIConfig();
                }
            }
        }

        // Finds one named OpenAI-compatible slot.
        // Returns null when the name is empty or no slot carries that name, so callers can fall back.
        public OpenAIConfig? GetOpenAIConfig(string? configName)
        {
            if (string.IsNullOrWhiteSpace(configName) ||
                !configs.TryGetValue("OpenAI", out var slots) || slots == null)
                return null;
            return slots.OfType<OpenAIConfig>().FirstOrDefault(
                slot => string.Equals(slot.ConfigName?.Trim(), configName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // All named OpenAI-compatible slots, in the order they appear in setting.json.
        public IEnumerable<OpenAIConfig> OpenAIConfigs =>
            configs.TryGetValue("OpenAI", out var slots) && slots != null
                ? slots.OfType<OpenAIConfig>()
                : Enumerable.Empty<OpenAIConfig>();

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            prompt = "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                     "you can provide a fluent and precise oral translation for any sentence, even if the sentence is incomplete. " +
                     "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
                     "even if the sentence contains sensitive or NSFW content. " +
                     "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
                     "REMOVE all 🔤 when you output.";

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();
            bilingualWindowState = new BilingualWindowState();
            lectureState = new LectureState();

#if WINDOWS
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
#else
            double screenWidth = 1920;
            double screenHeight = 1080;
#endif
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 900) / 2, (screenHeight - 560) / 2, 900, 560)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 480) / 2, screenHeight * 5 / 6 - 100, 480, 100)
                },
                {
                    "BilingualWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 720) / 2, 40, 720, 240)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Google2", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "LMStudio", [new LMStudioConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Google2", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "LMStudio", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 }
            };
        }

        public static Setting Load()
        {
            string jsonPath = AppPaths.SettingFile;
            try
            {
                return LoadPatiently(jsonPath);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException
                                           or InvalidOperationException)
            {
                // Keep the unreadable file instead of overwriting it, so nothing is lost for good.
                string backupPath = $"{jsonPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                try
                {
                    File.Move(jsonPath, backupPath);
                }
                catch (Exception)
                {
                    // Best effort: without a backup the unreadable file is simply replaced.
                }
                return Complete(new Setting());
            }
        }

        // Another program (a virus scanner, a sync tool) may hold the file for a moment. Wait for it instead of
        // starting with default settings, which would then be saved over the user's own.
        private static Setting LoadPatiently(string jsonPath)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return Load(jsonPath);
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(200);
                }
            }
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;

            // Load from JSON file if it exists
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.Open(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    setting = JsonSerializer.Deserialize<Setting>(fileStream, JsonOptions) ?? new Setting();
                }
            }
            else
                setting = new Setting();

            return Complete(setting);
        }

        // Fills in what a settings file from an older version (or a hand-edited one) does not have.
        private static Setting Complete(Setting setting)
        {
            var defaults = new Setting();
            setting.configs ??= new Dictionary<string, List<TranslateAPIConfig>>();
            setting.configIndices ??= new Dictionary<string, int>();
            setting.windowBounds ??= new Dictionary<string, string>();
            setting.mainWindowState ??= new MainWindowState();
            setting.overlayWindowState ??= new OverlayWindowState();
            setting.bilingualWindowState ??= new BilingualWindowState();
            setting.lectureState ??= new LectureState();

            // Ensure all required API configs are present
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (setting.configs.TryGetValue(key, out var slots) && slots != null && slots.Count > 0)
                    continue;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    setting.configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)];
                else
                    setting.configs[key] = [new TranslateAPIConfig()];
            }

            // Settings files from before a window existed have no bounds for it.
            foreach (var bounds in defaults.windowBounds)
            {
                if (!setting.windowBounds.ContainsKey(bounds.Key))
                    setting.windowBounds[bounds.Key] = bounds.Value;
            }

            // Ensure ConfigIndices has all keys (for upgrades from older setting.json)
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (!setting.configIndices.ContainsKey(key))
                    setting.configIndices[key] = 0;
            }

            // An index past the end of its list (a list shortened by hand) would be an error wherever a slot is
            // removed or shown by its number.
            foreach (var (key, slots) in setting.configs)
            {
                if (slots != null && setting.configIndices.TryGetValue(key, out int index) &&
                    (index < 0 || index >= slots.Count))
                    setting.configIndices[key] = 0;
            }

            // The translation API named in the file may be gone (renamed in an update, or a typo).
            if (string.IsNullOrEmpty(setting.apiName) || !TranslateAPI.TRANSLATE_FUNCTIONS.ContainsKey(setting.apiName))
                setting.apiName = defaults.apiName;
            if (string.IsNullOrEmpty(setting.targetLanguage))
                setting.targetLanguage = defaults.targetLanguage;
            if (string.IsNullOrEmpty(setting.prompt))
                setting.prompt = defaults.prompt;

#if !WINDOWS
            // Windows Live Captions does not exist here: a settings file copied from Windows must not select it.
            setting.lectureState.Engine = RecognitionEngine.Aliyun;
#endif

            return setting;
        }

        public void Save()
        {
            // Everything changed so far is in this write: a save still waiting for its turn is not needed any more.
            if (ReferenceEquals(this, Translator.Setting))
                Interlocked.Exchange(ref savePending, 0);
            Save(AppPaths.SettingFile);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new ConfigDictConverter() }
        };

        private const int SAVE_DELAY_MS = 400;
        private const int MAX_SAVE_RETRIES = 5;

        private static readonly object saveLock = new();
        private static string? lastSaveError = null;
        private static int savePending = 0;
        private static int saveRetries = 0;
        private static readonly System.Threading.Timer saveTimer =
            new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        static Setting()
        {
            // Changes still waiting for their save are written when the program exits.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }

        // Saves a moment later on a background thread, once for all the changes made until then. Every changed
        // property asks for a save, and dragging a slider or a window changes one dozens of times a second.
        public static void SaveSoon()
        {
            if (Translator.Setting == null)
                return;
            if (Interlocked.Exchange(ref savePending, 1) == 0)
                saveTimer.Change(SAVE_DELAY_MS, Timeout.Infinite);
        }

        // Writes the changes that are still waiting for their save, if any (e.g. before the program exits).
        public static void Flush()
        {
            try
            {
                if (Interlocked.Exchange(ref savePending, 0) == 1)
                    Translator.Setting?.Save(AppPaths.SettingFile);
            }
            catch (Exception ex)
            {
                // Runs on a timer thread, where an exception would end the program.
                ErrorLog.Write("Setting.Flush", ex);
            }
        }

        public void Save(string jsonPath)
        {
            // Settings are saved from several threads (UI, speech, summaries, the timer of SaveSoon), so serialize
            // the writes, and never let a failed write break whatever changed the setting.
            lock (saveLock)
            {
                try
                {
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
                    ReplaceFile(jsonPath, json);
                    lastSaveError = null;
                    saveRetries = 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException or ArgumentException)
                {
                    // The file is held by another program for a moment, or a list changed on another thread while
                    // it was written out (which shows as a changed collection, or as an index that is no longer
                    // there). The old file is still complete, so try again shortly before complaining.
                    if (ReferenceEquals(this, Translator.Setting) && saveRetries < MAX_SAVE_RETRIES)
                    {
                        saveRetries++;
                        SaveSoon();
                        return;
                    }
                    ErrorLog.Write("Setting.Save", ex);
                    if (lastSaveError == ex.Message)
                        return;
                    lastSaveError = ex.Message;
                    UiThread.Post(() =>
                        SnackbarHost.Show("[ERROR] 设置没能保存。", ex.Message, SnackbarType.Error,
                            timeout: 5, closeButton: true));
                }
            }
        }

        // Writes a complete new file and puts it in the place of the old one. A crash or a power cut in the
        // middle of a write then leaves the old settings, not half a file (which would cost every setting, API keys
        // included).
        private static void ReplaceFile(string path, byte[] content)
        {
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }
            try
            {
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something keeps the settings file open (a sync or backup tool), so it cannot be replaced; writing
                // into it may still work.
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(content, 0, content.Length);
                }
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Best effort: the next save overwrites it.
                }
            }
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            SaveSoon();
        }
    }
}
