using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class LecturePage : UserControl
    {
        public const int MIN_HEIGHT = 520;
        private const string AUTO_VOICE = "自动（按朗读的语言选择）";
        private const string AUTO_OCR = "自动";
        private const string DEFAULT_OUTPUT = "系统默认输出设备";
        private const string MISSING_OUTPUT = "上次选的设备（现在没有连接）";

        public record Choice(string Tag, string Name)
        {
            public override string ToString() => Name;
        }

        public record ApiChoice(string Key, string Name)
        {
            public override string ToString() => Name;
        }

        private static readonly Choice[] ASR_LANGUAGES = LectureLanguages.All
            .Select(l => new Choice(l.Code, l.Name))
            .Append(new Choice(LectureLanguages.AUTO, "自动判断"))
            .ToArray();

        // What the external tools (tesseract, spd-say or say, pactl) answered last: shown at once when the page
        // opens and asked again in the background. Null until the first answer.
        private static List<SlideOcr.OcrLanguage>? ocrLanguages;
        private static List<string>? voices;
        private static List<AudioDevice>? microphones;
        private static List<AudioDevice>? outputs;
        // The default output device, read when the devices are listed (asking costs a call to `pactl`).
        private static string? renderDefaultId;

        private bool initializing = true;
        private bool attached;
        private readonly List<TextField> textFields = new();
        private readonly List<Action> sliderShown = new();
        // Set while the devices are being listed: a second refresh would only repeat the work.
        private bool loadingDevices;
        // Whether this page shows the lists above. They are shared: a page that was left before its answer came
        // may have stored the very lists this one is still waiting to show.
        private bool devicesShown, voicesShown, ocrLanguagesShown;
        // The configuration card ① shows and listens to. Another one takes its place when the API settings
        // window switches configurations.
        private OpenAIConfig? subscribedConfig;

        private readonly TextField asrModelField;
        private readonly TextField asrEndpointField;
        private readonly TextField apiUrlField;
        private readonly TextField modelField;
        private readonly TextField subjectField;
        private readonly TextField glossaryField;
        private readonly TextField summaryPromptField;
        private readonly TextField targetLangField;

        private static OpenAIConfig? ApiConfig => Summarizer.Config;
        private static LectureState Lecture => Translator.Setting.Lecture;

        // A text setting that is written when its box loses the focus (and when the page is left), not on every
        // key. It remembers what it put in the box, so that only what was typed since is written: a box still
        // showing an older value, because the setting was changed elsewhere (the API settings window, the
        // recognition model switched for the language) or because its loader failed, never puts that value back.
        private sealed class TextField
        {
            private readonly Func<string> read;
            private readonly Action<string> write;
            private readonly Func<string> get;
            private readonly Action<string> set;
            private string shown = string.Empty;

            public TextField(Func<string> read, Action<string> write, Func<string> get, Action<string> set)
            {
                this.read = read;
                this.write = write;
                this.get = get;
                this.set = set;
            }

            // Shows the setting, in place of anything typed but not written yet.
            public void Show()
            {
                write(get());
                shown = read();
            }

            // The setting was changed elsewhere: it is shown, unless something is being typed here.
            public void Follow()
            {
                if (read() == shown)
                    Show();
            }

            public void Commit()
            {
                string text = read();
                if (text == shown)
                    return;
                shown = text;
                if (text != get())
                    set(text);
            }
        }

        public LecturePage()
        {
            InitializeComponent();
            DataContext = Lecture;
            ContextAwareSwitch.DataContext = Translator.Setting;
            ContextAwareSwitch.IsChecked = Translator.Setting.ContextAware;
            ContextAwareSwitch.IsCheckedChanged += (s, e) => Translator.Setting.ContextAware = ContextAwareSwitch.IsChecked == true;
            LatencySwitch.IsChecked = Translator.Setting.MainWindow.LatencyShow;
            LatencySwitch.IsCheckedChanged += (s, e) =>
                Translator.Setting.MainWindow.LatencyShow = LatencySwitch.IsChecked == true;

            if (OperatingSystem.IsLinux())
            {
                ScaleCard.IsVisible = true;
                UiScaleBox.ItemsSource = LinuxScale.Choices.Select(c => new Choice(c, LinuxScale.Label(c))).ToList();
                UiScaleBox.SelectedItem = ((IEnumerable<Choice>)UiScaleBox.ItemsSource).FirstOrDefault(c => c.Tag == DesktopSettings.UiScale)
                    ?? ((IEnumerable<Choice>)UiScaleBox.ItemsSource).First();
                UiScaleBox.SelectionChanged += (s, e) =>
                {
                    if (UiScaleBox.SelectedItem is Choice choice)
                        DesktopSettings.UiScale = choice.Tag;
                };
            }

            AsrKeyBox.TextChanged += AsrKeyBox_TextChanged;
            ApiKeyBox.TextChanged += ApiKeyBox_TextChanged;
            AsrLanguageBox.ItemsSource = ASR_LANGUAGES;
            AsrLanguageBox.SelectionChanged += (s, e) =>
            {
                if (!initializing && AsrLanguageBox.SelectedItem is Choice choice)
                    Lecture.AsrLanguage = choice.Tag;
            };
            AsrModelBox.ItemsSource = new[] { "paraformer-realtime-v2", "fun-asr-realtime" };
            asrModelField = CommitOnLeave(AsrModelBox, () => Lecture.AsrModel, value => Lecture.AsrModel = value);
            asrEndpointField = CommitOnLeave(AsrEndpointBox, () => Lecture.AsrEndpoint,
                value => Lecture.AsrEndpoint = value);
            apiUrlField = CommitOnLeave(ApiUrlBox, () => ApiConfig?.ApiUrl ?? string.Empty, value =>
            {
                if (ApiConfig != null)
                    ApiConfig.ApiUrl = value;
            });
            modelField = CommitOnLeave(ModelBox, () => ApiConfig?.ModelName ?? string.Empty, value =>
            {
                if (ApiConfig != null)
                    ApiConfig.ModelName = value;
            });
            subjectField = CommitOnLeave(SubjectBox, () => Lecture.Subject, value => Lecture.Subject = value);
            glossaryField = CommitOnLeave(GlossaryBox, () => Lecture.Glossary, value => Lecture.Glossary = value);
            summaryPromptField = CommitOnLeave(SummaryPromptBox, () => Lecture.SummaryPrompt,
                value => Lecture.SummaryPrompt = value);
            targetLangField = CommitOnLeave(TargetLangBox, () => Translator.Setting.TargetLanguage, value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                    Translator.Setting.TargetLanguage = value.Trim();
            });

            SegmentModeBox.SelectionChanged += (s, e) =>
            {
                if (!initializing && SegmentModeBox.SelectedIndex >= 0)
                    Lecture.SegmentModeIndex = SegmentModeBox.SelectedIndex;
            };
            SpeakModeBox.SelectionChanged += (s, e) =>
            {
                if (!initializing && SpeakModeBox.SelectedIndex >= 0)
                    Lecture.SpeakModeIndex = SpeakModeBox.SelectedIndex;
            };
            // The name of a slider stands on the left of its row (in the page), its value on the right.
            BindSlider(MaxSectionSlider, () => Lecture.MaxSectionMinutes, v => Lecture.MaxSectionMinutes = v,
                MaxSectionLabel, v => $"{v} 分钟");
            BindSlider(IntervalSlider, () => Lecture.SummaryIntervalMinutes, v => Lecture.SummaryIntervalMinutes = v,
                IntervalLabel, v => $"每 {v} 分钟一节");
            BindSlider(RateSlider, () => Lecture.SpeechRate, v => Lecture.SpeechRate = v, RateLabel, v => $"{v}");
            BindSlider(VolumeSlider, () => Lecture.SpeechVolume, v => Lecture.SpeechVolume = v, VolumeLabel,
                v => $"{v}");
            BindSlider(SyncSlider, () => Translator.Setting.MaxSyncInterval, v => Translator.Setting.MaxSyncInterval = v,
                SyncLabel, v => $"字幕每变化 {v} 次翻译一次");
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Shell.Main?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
            attached = true;
            Subscribe(false);
            Subscribe(true);
            BindApiConfig();
            Load();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            attached = false;
            Subscribe(false);
            if (subscribedConfig != null)
            {
                subscribedConfig.PropertyChanged -= OnApiConfigChanged;
                subscribedConfig = null;
            }
            // Text still being edited is kept.
            foreach (var field in textFields)
                field.Commit();
            initializing = true;
        }

        private void Subscribe(bool on)
        {
            if (on)
            {
                Summarizer.CurrentPageChanged += OnCurrentPageChanged;
                SlideDeck.LoadingChanged += OnSlidesLoadingChanged;
                Lecture.PropertyChanged += OnLectureChanged;
                Translator.Setting.PropertyChanged += OnSettingChanged;
            }
            else
            {
                Summarizer.CurrentPageChanged -= OnCurrentPageChanged;
                SlideDeck.LoadingChanged -= OnSlidesLoadingChanged;
                Lecture.PropertyChanged -= OnLectureChanged;
                Translator.Setting.PropertyChanged -= OnSettingChanged;
            }
        }

        // Listens to the configuration card ① is about.
        private void BindApiConfig()
        {
            var config = ApiConfig;
            if (ReferenceEquals(subscribedConfig, config))
                return;
            if (subscribedConfig != null)
                subscribedConfig.PropertyChanged -= OnApiConfigChanged;
            subscribedConfig = config;
            if (config != null)
                config.PropertyChanged += OnApiConfigChanged;
        }

        private void Load()
        {
            initializing = true;
            // One failing loader (e.g. an audio device that can't be read) must not leave the page read-only.
            foreach (Action load in new Action[]
                     {
                         LoadEngine, LoadSettings, LoadApiSetting, LoadTranslateSetting, LoadSource,
                         ShowOcrLanguages, ShowVoices, ShowDevices, ShowSlidesInfo, ShowSaveFolder
                     })
            {
                try
                {
                    load();
                }
                catch (Exception)
                {
                    // Reported nowhere: the other loaders still run and the control keeps its default.
                }
            }
            // Edits made from here on are saved: the loaders below fill their own controls under a short guard.
            initializing = false;
            // The lists shown so far are the ones of the last visit. The tools behind them (tesseract, spd-say
            // or say, pactl) are asked again without the page waiting for them.
            Refresh(LoadOcrLanguages);
            Refresh(LoadVoices);
            Refresh(LoadDevices);
        }

        // A list that cannot be read leaves its control as it is.
        private static async void Refresh(Func<Task> load)
        {
            try
            {
                await load();
            }
            catch (Exception)
            {
                // Nothing new to show.
            }
        }

        // Fills controls from the settings without the change handlers saving them back. Runs on the UI thread
        // without awaiting, so no user edit can come in between.
        private void Fill(Action fill)
        {
            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                fill();
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        private TextField CommitOnLeave(EditableCombo box, Func<string> get, Action<string> set)
        {
            var field = new TextField(() => box.Text, text => box.Text = text, get, set);
            box.LostFocus += (s, e) => field.Commit();
            box.ItemPicked += item => field.Commit();
            textFields.Add(field);
            return field;
        }

        private TextField CommitOnLeave(TextBox box, Func<string> get, Action<string> set)
        {
            var field = new TextField(() => box.Text ?? string.Empty, text => box.Text = text, get, set);
            box.LostFocus += (s, e) => field.Commit();
            textFields.Add(field);
            return field;
        }

        private void BindSlider(Slider slider, Func<int> get, Action<int> set, TextBlock label, Func<int, string> text)
        {
            slider.PropertyChanged += (s, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || initializing)
                    return;
                int value = (int)Math.Round(slider.Value);
                if (value != get())
                    set(value);
                label.Text = text(get());
            };
            sliderShown.Add(() =>
            {
                slider.Value = get();
                label.Text = text(get());
            });
        }

        private void LoadSettings()
        {
            SegmentModeBox.SelectedIndex = Lecture.SegmentModeIndex;
            SpeakModeBox.SelectedIndex = Lecture.SpeakModeIndex;
            ContextAwareSwitch.IsChecked = Translator.Setting.ContextAware;
            LatencySwitch.IsChecked = Translator.Setting.MainWindow.LatencyShow;
            foreach (var show in sliderShown)
                show();
            asrModelField.Show();
            asrEndpointField.Show();
            subjectField.Show();
            glossaryField.Show();
            summaryPromptField.Show();
        }

        private void LoadApiSetting()
        {
            var config = ApiConfig;
            if (config == null)
                return;
            // Fill in DeepSeek defaults on first use.
            if (string.IsNullOrEmpty(config.ApiUrl))
                ApiProviders.Apply(ApiProviders.All[ApiProviders.IndexOf(config.ApiUrl)], config);
            ShowApiSetting();
        }

        private void ShowApiSetting()
        {
            var config = ApiConfig;
            if (config == null)
                return;
            int index = ApiProviders.IndexOf(config.ApiUrl);
            ProviderBox.SelectedIndex = index;
            ModelBox.ItemsSource = ApiProviders.All[index].Models;
            ShowApiFields();
            FillKeyBox();
        }

        private void ShowApiFields()
        {
            apiUrlField.Show();
            modelField.Show();
        }

        // The box tells of its new text a moment later (`TextChanged` is posted): by then the text is the saved
        // key, which is how the handler knows there is nothing to take over.
        private void FillKeyBox()
        {
            ApiKeyBox.Text = ApiConfig?.ApiKey ?? string.Empty;
            ShowApiKeyStatus();
        }

        // Changed by the code and not on this page: the recognition model (free quota used up), the glossary
        // (terms extracted from the slides arrive after the page was left and opened again).
        private void OnLectureChanged(object? sender, PropertyChangedEventArgs e)
        {
            var field = e.PropertyName switch
            {
                nameof(LectureState.AsrModel) => asrModelField,
                nameof(LectureState.Glossary) => glossaryField,
                _ => null,
            };
            if (field != null)
                Dispatcher.UIThread.Post(() =>
                {
                    if (attached)
                        field.Follow();
                });
        }

        // Changed elsewhere (e.g. the API settings window): show it here too.
        private void OnApiConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(OpenAIConfig.ApiKey):
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!attached)
                            return;
                        string key = ApiConfig?.ApiKey ?? string.Empty;
                        if (ApiKeyBox.Text != key && !ApiKeyBox.IsKeyboardFocusWithin)
                            FillKeyBox();
                        else
                            ShowApiKeyStatus();
                    });
                    break;
                case nameof(OpenAIConfig.ApiUrl):
                case nameof(OpenAIConfig.ModelName):
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!attached || ApiConfig == null)
                            return;
                        apiUrlField.Follow();
                        modelField.Follow();
                        // The provider is told from the address. Without one it stays as picked ("其他" empties
                        // the address for a new one to be typed).
                        int index = ApiProviders.IndexOf(ApiConfig.ApiUrl);
                        if (ApiConfig.ApiUrl.Length > 0 && ProviderBox.SelectedIndex != index)
                            Fill(() =>
                            {
                                ProviderBox.SelectedIndex = index;
                                ModelBox.ItemsSource = ApiProviders.All[index].Models;
                            });
                    });
                    break;
            }
        }

        // The API settings window switched to another configuration (no property name), or added or removed
        // one: card ① is about a different configuration now. It is only shown: a new, empty one gets its
        // defaults when this page is opened the next time, not while it is being filled in over there.
        private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null or nameof(Setting.Configs)))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!attached || ReferenceEquals(ApiConfig, subscribedConfig))
                    return;
                BindApiConfig();
                Fill(ShowApiSetting);
                TestApiResult.Text = string.Empty;
            });
        }

        private void ProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing || ProviderBox.SelectedIndex < 0 || ApiConfig == null)
                return;
            var provider = ApiProviders.All[ProviderBox.SelectedIndex];
            ModelBox.ItemsSource = provider.Models;
            ApiProviders.Apply(provider, ApiConfig);
            Fill(ShowApiFields);
            TestApiResult.Text = string.Empty;
        }

        // Taken over on every keystroke / paste, independent of the page state, so the key can't be lost
        // by switching pages. Changing the key asks for the settings to be saved by itself.
        private void ApiKeyBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ApiConfig == null)
                return;
            string key = ApiProviders.CleanKey(ApiKeyBox.Text ?? string.Empty);
            // Only the user clearing the box may erase a saved key, never a control reloading itself.
            if (key.Length == 0 && ApiConfig.ApiKey.Length > 0 && !ApiKeyBox.IsKeyboardFocusWithin)
            {
                FillKeyBox();
                return;
            }
            if (key != ApiConfig.ApiKey)
            {
                bool firstKey = ApiConfig.ApiKey.Length == 0 && key.Length > 0;
                ApiConfig.ApiKey = key;
                if (firstKey)
                    SwitchTranslateApiToSummary();
            }
            ShowApiKeyStatus();
        }

        // Paragraph refinement and the glossary in translation only work when the translation API is the
        // one configured in ①. Fresh installs start on Google so translation works without a key, so the
        // first saved key moves a Google default over; a deliberately chosen API is left alone.
        private void SwitchTranslateApiToSummary()
        {
            var setting = Translator.Setting;
            if (setting == null || (setting.ApiName != "Google" && setting.ApiName != "Google2"))
                return;
            setting.ApiName = Summarizer.SUMMARY_API;
            Fill(() =>
            {
                if (TranslateApiBox.ItemsSource is IEnumerable<ApiChoice> choices)
                    TranslateApiBox.SelectedItem = choices.FirstOrDefault(c => c.Key == setting.ApiName);
                LoadTargetLanguages();
            });
        }

        // Shows that a key is stored without revealing it.
        private void ShowApiKeyStatus()
        {
            string key = ApiConfig?.ApiKey ?? string.Empty;
            ApiKeyStatus.Text = key.Length == 0
                ? "还没有保存 API Key。"
                : $"已保存：{ApiProviders.MaskKey(key)}，" +
                  $"保存在 {AppPaths.SettingFile}";
        }

        private void ShowSaveFolder()
        {
            SaveFolderBox.Text = LectureDocument.SaveFolder;
        }

        private async void ChooseSaveFolder_Click(object? sender, RoutedEventArgs e)
        {
            string? folder = await Shell.PickFolder("选择转录文件的保存位置", LectureDocument.SaveFolder);
            if (folder == null)
                return;
            Lecture.SaveFolder = folder;
            ShowSaveFolder();
        }

        private void OpenSaveFolder_Click(object? sender, RoutedEventArgs e)
        {
            LectureDialogs.ShowInFolder(LectureDocument.SaveFolder);
        }

        private void ResetSaveFolder_Click(object? sender, RoutedEventArgs e)
        {
            Lecture.SaveFolder = string.Empty;
            ShowSaveFolder();
        }

        // Live Captions does not exist here, so the cloud recognizer is the only engine.
        private void LoadEngine()
        {
            AsrLanguageBox.SelectedItem = ASR_LANGUAGES.FirstOrDefault(c => c.Tag == LectureLanguages.Code(Lecture.AsrLanguage));
            AsrKeyBox.Text = Lecture.AsrApiKey;
            ShowAsrKeyStatus();
        }

        private void AsrKeyBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            string key = ApiProviders.CleanKey(AsrKeyBox.Text ?? string.Empty);
            if (key.Length == 0 && Lecture.AsrApiKey.Length > 0 && !AsrKeyBox.IsKeyboardFocusWithin)
            {
                LoadEngine();
                return;
            }
            if (key != Lecture.AsrApiKey)
                Lecture.AsrApiKey = key;
            ShowAsrKeyStatus();
        }

        private void ShowAsrKeyStatus()
        {
            string key = Lecture.AsrApiKey;
            AsrKeyStatus.Text = key.Length == 0
                ? "还没有保存阿里云 API Key。"
                : $"已保存：{ApiProviders.MaskKey(key)}";
        }

        private async void TestAsr_Click(object? sender, RoutedEventArgs e)
        {
            await RunBusy(TestAsrButton, TestAsrResult, "测试中……", async () =>
            {
                string? note = await CloudAsr.Test();
                return note == null ? "连接成功" : $"连接成功（{note}）";
            });
        }

        private void LoadTranslateSetting()
        {
            var keys = Translator.Setting.Configs.Keys.ToList();
            var choices = keys
                .OrderBy(key => key == Summarizer.SUMMARY_API ? 0 : ApiProviders.ApiNames.ContainsKey(key) ? 1 : 2)
                .Select(key => new ApiChoice(key, ApiProviders.ApiNames.TryGetValue(key, out var name) ? name : key))
                .ToList();
            TranslateApiBox.ItemsSource = choices;
            TranslateApiBox.SelectedItem = choices.FirstOrDefault(c => c.Key == Translator.Setting.ApiName);
            LoadTargetLanguages();
        }

        private void TranslateApiBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing || TranslateApiBox.SelectedItem is not ApiChoice choice)
                return;
            Translator.Setting.ApiName = choice.Key;
            Fill(LoadTargetLanguages);
        }

        private void LoadTargetLanguages()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            PropertyInfo? languagesProp = null;
            for (var type = configType; type != null && languagesProp == null; type = type.BaseType)
                languagesProp = type.GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            languagesProp ??= typeof(TranslateAPIConfig).GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp!.GetValue(null)!;
            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;
            TargetLangBox.ItemsSource = supportedLanguages.Keys.ToList();
            targetLangField.Show();
        }

        private void ApiSettingButton_Click(object? sender, RoutedEventArgs e)
        {
            ApiSettingWindow.ShowSingle();
        }

        private async void ExtractGlossary_Click(object? sender, RoutedEventArgs e)
        {
            glossaryField.Commit();
            await RunBusy(ExtractGlossaryButton, GlossaryResult, "正在从课件中提取术语……", async () =>
            {
                var (text, added) = await Glossary.ExtractFromSlides();
                Lecture.Glossary = text;
                glossaryField.Show();
                return added > 0 ? $"新增 {added} 个术语，请检查一下译法" : "没有发现新的术语";
            });
        }

        private async void TestApi_Click(object? sender, RoutedEventArgs e)
        {
            // Commit fields that only update on lost focus.
            foreach (var field in textFields)
                field.Commit();

            await RunBusy(TestApiButton, TestApiResult, "测试中……", async () =>
            {
                return await Summarizer.TestConnection();
            });
        }

        // Disables `button` while `work` runs, showing `busyText` and then the text it returns (or the error).
        private static async Task RunBusy(Button button, TextBlock result, string busyText, Func<Task<string>> work)
        {
            button.IsEnabled = false;
            result.Text = busyText;
            try
            {
                result.Text = await work();
            }
            catch (Exception ex)
            {
                result.Text = $"× {ex.Message}";
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        // The part of the sound settings that does not wait for the device lists.
        private void LoadSource()
        {
            SourceBox.SelectedIndex = (int)Lecture.InputSource;
            bool mac = OperatingSystem.IsMacOS();
            // macOS has no list of microphones: the system default one is used.
            MicPanel.IsVisible = !mac;
            MicHint.Text = mac
                ? "用的是系统默认麦克风，可在系统设置的“声音”里更换。第一次使用时 macOS 会请求麦克风权限。"
                : "这里选的麦克风只供本程序录音，不会更改系统的默认录音设备。不要选蓝牙耳机自带的麦克风，否则耳机会切换到通话模式，音质变差。";
            MicBox.IsEnabled = Lecture.InputSource == InputSource.Microphone;

            // On Linux the speaker always plays on the system default output, so no picker.
            bool pickable = !OperatingSystem.IsLinux();
            SpeechDeviceLabel.IsVisible = pickable;
            SpeechDeviceBox.IsVisible = pickable;
        }

        private void ShowDevices()
        {
            ShowSpeechDeviceHint();
            if (microphones == null || outputs == null)
                return;
            devicesShown = true;

            // A chosen microphone that is not plugged in shows as the default one, which is what a class then uses.
            MicBox.ItemsSource = microphones;
            string chosen = DesktopSettings.MicrophoneId;
            MicBox.SelectedItem = microphones.FirstOrDefault(device => device.Id == chosen && chosen.Length > 0)
                                  ?? microphones.FirstOrDefault(device => device.IsDefault);

            if (OperatingSystem.IsLinux())
                return;
            var items = new List<object> { DEFAULT_OUTPUT };
            items.AddRange(outputs);
            string speechDeviceId = Lecture.SpeechDeviceId;
            object? selected = outputs.FirstOrDefault(device => device.Id == speechDeviceId);
            if (selected == null && speechDeviceId.Length > 0)
            {
                // Shown as what it is, not as the default device: speech only ever goes to the chosen device.
                selected = new AudioDevice(speechDeviceId, MISSING_OUTPUT, false);
                items.Add(selected);
            }
            SpeechDeviceBox.ItemsSource = items;
            SpeechDeviceBox.SelectedItem = selected ?? DEFAULT_OUTPUT;
        }

        private async Task LoadDevices()
        {
            if (loadingDevices)
                return;
            loadingDevices = true;
            RefreshDevicesButton.IsEnabled = false;
            try
            {
                var capture = await Task.Run(() => AudioDevices.ListAsync(DataFlow.Capture));
                var render = await Task.Run(() => AudioDevices.ListAsync(DataFlow.Render));
                string? renderDefault = await Task.Run(() => AudioDevices.DefaultIdAsync(DataFlow.Render));
                // Unchanged lists are left alone: filling them again would close a list that is open.
                if (devicesShown && microphones != null && outputs != null && microphones.SequenceEqual(capture) &&
                    outputs.SequenceEqual(render) && renderDefaultId == renderDefault)
                    return;
                microphones = capture;
                outputs = render;
                renderDefaultId = renderDefault;
                Fill(ShowDevices);
            }
            finally
            {
                loadingDevices = false;
                RefreshDevicesButton.IsEnabled = true;
            }
        }

        private async void RefreshDevices_Click(object? sender, RoutedEventArgs e)
        {
            await LoadDevices();
        }

        private void SourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing || SourceBox.SelectedIndex < 0)
                return;
            var source = (InputSource)SourceBox.SelectedIndex;
            Lecture.InputSource = source;
            MicBox.IsEnabled = source == InputSource.Microphone;
            // The source is taken when a class starts.
            SourceStatus.Text = ClassSession.IsRunning
                ? "已保存。正在上的这节课还在听原来的来源，点“停止”再点“开始”后生效。"
                : "已保存。阿里云识别会直接使用这里选的声音来源。";
        }

        private void MicBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing || MicBox.SelectedItem is not AudioDevice device)
                return;
            // The default device is followed, whichever it is.
            string source = device.IsDefault ? string.Empty : device.Id;
            DesktopSettings.MicrophoneId = source;
            CloudAsr.MicrophoneSource = source.Length == 0 ? null : source;
            SourceStatus.Text = ClassSession.IsRunning
                ? $"已改用：{device.Name}。正在上的这节课点“停止”再点“开始”后生效。"
                : $"已改用：{device.Name}。";
        }

        private void SpeechDeviceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Lecture.SpeechDeviceId = SpeechDeviceBox.SelectedItem is AudioDevice device ? device.Id : string.Empty;
            ShowSpeechDeviceHint();
        }

        private void ShowSpeechDeviceHint()
        {
            if (OperatingSystem.IsLinux())
            {
                SpeechDeviceHint.Text = "Linux 上播报总是从系统默认输出设备播出，播报时会暂停识别。";
                return;
            }
            string speechDeviceId = Lecture.SpeechDeviceId;
            if (speechDeviceId.Length > 0 && outputs != null && outputs.All(device => device.Id != speechDeviceId))
            {
                SpeechDeviceHint.Text = "这个设备现在没有连接，播报不会出声；连上以后会从它播出。";
                return;
            }
            bool onDefault = string.IsNullOrEmpty(speechDeviceId) ||
                             speechDeviceId == renderDefaultId;
            SpeechDeviceHint.Text = onDefault
                ? "播报和电脑声音走同一个设备，识别会听到播报，所以播报时会暂停接收字幕。" +
                  "线下课建议在这里直接选蓝牙耳机，并把系统默认输出设为电脑扬声器。"
                : "播报只从这个设备播出，不会被识别听到。";
        }

        private void ShowOcrLanguages()
        {
            OcrHint.Text = "上传后按老师讲到的页码分节，小结也会参考这一页的文字纠正术语。没有文字的页面（扫描件、图片）会自动用 Tesseract OCR 识别；" +
                           "OCR 语言来自已安装的 Tesseract 语言包。也支持 .pptx。" +
                           (SlideOcr.IsAvailable ? string.Empty : "\n还没有找到 OCR 工具：请安装 poppler-utils（pdftoppm）和 tesseract。");
            if (ocrLanguages == null)
                return;
            ocrLanguagesShown = true;
            var languages = new List<object> { AUTO_OCR };
            languages.AddRange(ocrLanguages);
            OcrLanguageBox.ItemsSource = languages;
            string current = Lecture.OcrLanguage;
            OcrLanguageBox.SelectedItem = ocrLanguages.FirstOrDefault(l => l.Tag == current) ?? (object)AUTO_OCR;
        }

        private async Task LoadOcrLanguages()
        {
            var available = await Task.Run(SlideOcr.AvailableLanguages);
            if (ocrLanguagesShown && ocrLanguages != null && ocrLanguages.SequenceEqual(available))
                return;
            ocrLanguages = available;
            Fill(ShowOcrLanguages);
        }

        private void OcrLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Lecture.OcrLanguage =
                OcrLanguageBox.SelectedItem is SlideOcr.OcrLanguage language ? language.Tag : string.Empty;
        }

        private void OnCurrentPageChanged(int? page)
        {
            Dispatcher.UIThread.Post(ShowSlidesInfo);
        }

        private void OnSlidesLoadingChanged()
        {
            Dispatcher.UIThread.Post(ShowSlidesInfo);
        }

        // Whether this page is reading a file, and shows how far that is.
        private bool readingSlides = false;

        private void ShowSlidesInfo()
        {
            // A file is being read, maybe since before this page was opened, or from the timeline page: not a
            // second one on top of it.
            LoadSlidesButton.IsEnabled = !SlideDeck.IsLoading;
            if (SlideDeck.IsLoading)
            {
                if (!readingSlides)
                    SlidesInfo.Text = "读取中……";
                // Nor is the file before it taken away meanwhile: the one being read takes its place.
                ClearSlidesButton.IsVisible = false;
                return;
            }
            if (!SlideDeck.IsLoaded)
            {
                SlidesInfo.Text = "未上传课件：按话题变化分节。";
                ClearSlidesButton.IsVisible = false;
                return;
            }
            string page = Summarizer.CurrentPage == null ? "识别中" : $"第 {Summarizer.CurrentPage} 页";
            string ocr = SlideDeck.OcrPageCount > 0 ? $"（其中 {SlideDeck.OcrPageCount} 页用 OCR 识别）" : string.Empty;
            SlidesInfo.Text = $"{SlideDeck.FileName}\n共 {SlideDeck.Pages.Count} 页{ocr}，当前：{page}";
            ClearSlidesButton.IsVisible = true;
        }

        private async void LoadSlides_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await Shell.PickFile("选择课件", Shell.SlideFiles);
            if (path == null)
                return;

            SlidesInfo.Text = "读取中……";
            LoadSlidesButton.IsEnabled = false;
            readingSlides = true;
            try
            {
                await SlideDeck.Load(path, new Progress<string>(text => SlidesInfo.Text = text));
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 课件读取失败。", ex.Message, SnackbarType.Error,
                    timeout: 3, closeButton: true);
            }
            finally
            {
                readingSlides = false;
            }
            ShowSlidesInfo();
        }

        private void ClearSlides_Click(object? sender, RoutedEventArgs e)
        {
            SlideDeck.Clear();
            ShowSlidesInfo();
        }

        private void ShowVoices()
        {
            if (voices == null)
                return;
            voicesShown = true;
            var items = new List<string> { AUTO_VOICE };
            items.AddRange(voices);
            // The chosen voice stays in the list even if it is not installed right now: it is the one in use.
            string current = Lecture.VoiceName;
            if (current.Length > 0 && !items.Contains(current))
                items.Add(current);
            VoiceBox.ItemsSource = items;
            VoiceBox.SelectedItem = current.Length > 0 ? current : AUTO_VOICE;
        }

        private async Task LoadVoices()
        {
            var installed = await Task.Run(Speaker.GetVoices);
            if (voicesShown && voices != null && voices.SequenceEqual(installed))
                return;
            voices = installed;
            Fill(ShowVoices);
        }

        private void VoiceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (initializing || VoiceBox.SelectedItem is not string voice)
                return;
            Lecture.VoiceName = voice == AUTO_VOICE ? string.Empty : voice;
        }

        private void TestSpeech_Click(object? sender, RoutedEventArgs e)
        {
            Speaker.Speak("这是一段试听。老师每讲完一节，你会在耳机里听到这一节的小结。");
        }

        private void SoundSettings_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!Shell.OpenSoundSettings())
                    SnackbarHost.Show("没有找到系统的声音设置。", "请在系统设置的“声音”里选择输出设备。", SnackbarType.Warning, timeout: 3);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 无法打开声音设置。", ex.Message, SnackbarType.Error, timeout: 2);
            }
        }

        private void ResetPrompt_Click(object? sender, RoutedEventArgs e)
        {
            Lecture.SummaryPrompt = LectureState.DEFAULT_SUMMARY_PROMPT;
            summaryPromptField.Show();
        }
    }
}
