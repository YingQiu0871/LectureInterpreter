using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class LecturePage : Page
    {
        public const int MIN_HEIGHT = 520;
        private const string AUTO_VOICE = "自动（按朗读的语言选择）";
        private const string AUTO_OCR = "自动";
        private const string AUTO_LANGUAGE = "自动判断";
        private const string DEFAULT_OUTPUT = "Windows 默认输出设备";
        private const string MISSING_OUTPUT = "上次选的设备（现在没有连接）";

        public record ApiChoice(string Key, string Name);

        private static SettingWindow? settingWindow;

        private bool initializing = true;
        private bool fillingKeyBox = false;

        private static OpenAIConfig? ApiConfig => Summarizer.Config;

        // The config object currently bound to card ①, so event handlers can be swapped on slot change.
        private OpenAIConfig? subscribedConfig;
        private const string FOLLOW_TRANSLATION = "（跟随翻译档位）";
        private bool fillingProviderBox;
        private readonly List<System.Windows.Controls.Button> providerActions = new();

        public LecturePage()
        {
            InitializeComponent();
            DataContext = Translator.Setting.Lecture;
            ScrollHelper.UseOwnScrollViewer(this, PageScroll);

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
                Subscribe(false);
                Subscribe(true);
                BindApiCard();
                initializing = true;
                // One failing loader (e.g. an OCR language list that can't be read) must not leave the page read-only.
                foreach (Action load in new Action[]
                         {
                             LoadEngine, () => LoadApiSetting(), LoadTranslateSetting, ShowDevices, LoadOcrLanguages,
                             ShowVoices, ShowSlidesInfo, ShowLiveCaptionsState, ShowSaveFolder, LoadAppearance
                         })
                {
                    try
                    {
                        load();
                    }
                    catch (Exception)
                    {
                        // Best effort: one failed status line should not block the others.
                    }
                }
                initializing = false;
                // The lists above are the ones of the last visit; Windows is asked again without the page waiting.
                _ = LoadDevices();
                _ = LoadVoices();
            };
            Unloaded += (s, e) =>
            {
                Subscribe(false);
                if (subscribedConfig != null)
                {
                    subscribedConfig.PropertyChanged -= OnApiConfigChanged;
                    subscribedConfig = null;
                }
                initializing = true;
            };
        }

        private void Subscribe(bool on)
        {
            if (on)
            {
                Summarizer.CurrentPageChanged += OnCurrentPageChanged;
                SlideDeck.LoadingChanged += OnSlidesLoadingChanged;
                Translator.Setting.MainWindow.PropertyChanged += OnMainWindowStateChanged;
                Translator.Setting.PropertyChanged += OnSettingChanged;
            }
            else
            {
                Summarizer.CurrentPageChanged -= OnCurrentPageChanged;
                SlideDeck.LoadingChanged -= OnSlidesLoadingChanged;
                Translator.Setting.MainWindow.PropertyChanged -= OnMainWindowStateChanged;
                Translator.Setting.PropertyChanged -= OnSettingChanged;
            }
        }

        // The API settings window switched to another configuration (no property name), or added or removed
        // one: card ① is about a different configuration now. It is only shown: a new, empty one gets its
        // defaults when this page is opened the next time, not while it is being filled in over there.
        private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null or nameof(Setting.Configs)))
                return;
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded || ReferenceEquals(ApiConfig, subscribedConfig))
                    return;
                BindApiCard();
                LoadApiSetting(applyDefaults: false);
                TestApiResult.Text = string.Empty;
            });
        }

        private void LoadAppearance()
        {
            var state = Translator.Setting.MainWindow;
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                (string)item.Tag == state.Theme);
            UiScaleSlider.Value = state.UiScale;
        }

        // Ctrl+wheel on the main window changes the scale too: keep the slider in step.
        private void OnMainWindowStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "UiScale")
                return;
            Dispatcher.InvokeAsync(() =>
            {
                double scale = Translator.Setting.MainWindow.UiScale;
                if (Math.Abs(UiScaleSlider.Value - scale) < 0.001)
                    return;
                bool wasInitializing = initializing;
                initializing = true;
                UiScaleSlider.Value = scale;
                initializing = wasInitializing;
            });
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || ThemeBox.SelectedItem is not ComboBoxItem item) return;
            Translator.Setting.MainWindow.Theme = (string)item.Tag;
            MainWindow.ApplySavedTheme();
        }

        private void UiScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (initializing || App.Current.MainWindow is not MainWindow window) return;
            Translator.Setting.MainWindow.UiScale = UiScaleSlider.Value;
            window.SetUiScale(Translator.Setting.MainWindow.UiScale);
        }

        // Binds card ① to the slot used for summaries, which may differ from the translation slot.
        private void BindApiCard()
        {
            var config = ApiConfig;
            ApiCard.DataContext = config;
            if (ReferenceEquals(subscribedConfig, config))
                return;
            if (subscribedConfig != null)
                subscribedConfig.PropertyChanged -= OnApiConfigChanged;
            subscribedConfig = config;
            if (config != null)
                config.PropertyChanged += OnApiConfigChanged;
        }

        // Lists named slots so summaries can use a different model from translation.
        private void LoadSummarySlots()
        {
            var setting = Translator.Setting;
            if (setting == null)
                return;

            var names = setting.OpenAIConfigs
                .Select(slot => slot.ConfigName?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToList();

            var items = new List<string> { FOLLOW_TRANSLATION };
            items.AddRange(names);
            SummaryConfigBox.ItemsSource = items;

            string current = setting.Lecture?.SummaryConfigName?.Trim() ?? string.Empty;
            SummaryConfigBox.SelectedItem = current.Length > 0 && names.Contains(current)
                ? current
                : FOLLOW_TRANSLATION;
        }

        private void SummaryConfigBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            var setting = Translator.Setting;
            if (setting?.Lecture == null || SummaryConfigBox.SelectedItem is not string choice)
                return;

            string name = choice == FOLLOW_TRANSLATION ? string.Empty : choice;
            if (string.Equals(setting.Lecture.SummaryConfigName, name, StringComparison.Ordinal))
                return;

            setting.Lecture.SummaryConfigName = name;
            BindApiCard();
            LoadApiSetting();
            TestApiResult.Text = string.Empty;
        }

        private void LoadApiSetting(bool refreshProviders = true, bool applyDefaults = true)
        {
            var config = ApiConfig;
            if (config == null)
                return;

            int index = ApiProviders.IndexOf(config.ApiUrl);
            int savedIndex = Translator.Setting.OpenAIConfigs.ToList().IndexOf(config);
            if (refreshProviders)
                RefreshProviders(!string.IsNullOrWhiteSpace(config.ConfigName) && savedIndex >= 0
                    ? ApiProviders.All.Length + savedIndex : index);
            ModelBox.ItemsSource = ApiProviders.All[index].Models;
            // Fill in DeepSeek defaults on first use.
            if (applyDefaults && string.IsNullOrEmpty(config.ApiUrl))
                ApiProviders.Apply(ApiProviders.All[index], config);

            FillKeyBox();
            bool wasInitializing = initializing;
            initializing = true;
            try { LoadSummarySlots(); }
            finally { initializing = wasInitializing; }
        }

        private void FillKeyBox()
        {
            fillingKeyBox = true;
            ApiKeyBox.Password = ApiConfig?.ApiKey ?? string.Empty;
            fillingKeyBox = false;
            ShowApiKeyStatus();
        }

        private void OnApiConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(OpenAIConfig.ApiKey))
                return;
            Dispatcher.InvokeAsync(() =>
            {
                // Changed elsewhere (e.g. the API settings window): show it here too.
                string key = ApiConfig?.ApiKey ?? string.Empty;
                if (ApiKeyBox.Password != key && !ApiKeyBox.IsKeyboardFocusWithin)
                    FillKeyBox();
                else
                    ShowApiKeyStatus();
            });
        }

        private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || fillingProviderBox || ProviderBox.SelectedIndex < 0 || ApiConfig == null)
                return;
            if (ProviderBox.SelectedIndex >= ApiProviders.All.Length)
            {
                var slots = Translator.Setting.OpenAIConfigs.ToList();
                int slotIndex = ProviderBox.SelectedIndex - ApiProviders.All.Length;
                if (slotIndex >= slots.Count)
                    return;
                Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = slotIndex;
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
                Setting.SaveSoon();
                BindApiCard();
                LoadApiSetting(false);
                TestApiResult.Text = string.Empty;
                return;
            }
            var provider = ApiProviders.All[ProviderBox.SelectedIndex];
            if (!string.IsNullOrWhiteSpace(ApiConfig.ConfigName))
            {
                // Reuse an existing unnamed slot instead of piling up one draft per provider switch.
                var list = Translator.Setting.Configs[Summarizer.SUMMARY_API];
                int draftIndex = list.FindIndex(c => string.IsNullOrWhiteSpace(c.ConfigName));
                if (draftIndex < 0)
                {
                    list.Add(new OpenAIConfig { ApiKey = ApiConfig.ApiKey });
                    draftIndex = list.Count - 1;
                }
                Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = draftIndex;
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
                BindApiCard();
                RefreshProviders(ProviderBox.SelectedIndex);
            }
            ModelBox.ItemsSource = provider.Models;
            ApiProviders.Apply(provider, ApiConfig);
            TestApiResult.Text = string.Empty;
        }

        private void RefreshProviders(int selectedIndex)
        {
            fillingProviderBox = true;
            try
            {
                providerActions.Clear();
                while (ProviderBox.Items.Count > ApiProviders.All.Length)
                    ProviderBox.Items.RemoveAt(ApiProviders.All.Length);
                int position = 1;
                foreach (var slot in Translator.Setting.OpenAIConfigs)
                {
                    int index = position - 1;
                    string label = string.IsNullOrWhiteSpace(slot.ConfigName) ? $"配置 {position}" : slot.ConfigName.Trim();
                    position++;
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(new TextBlock { Text = label, MinWidth = 180, VerticalAlignment = VerticalAlignment.Center });
                    foreach (var (symbol, action) in new[] { ("⬆", -1), ("⬇", 1), ("删除", 0) })
                    {
                        var button = new System.Windows.Controls.Button
                        {
                            Content = symbol,
                            Tag = index,
                            Margin = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(5, 1, 5, 1),
                            Visibility = ProviderBox.IsDropDownOpen ? Visibility.Visible : Visibility.Collapsed
                        };
                        button.Click += action == 0 ? DeleteProvider_click : action < 0 ? MoveProviderUp_click : MoveProviderDown_click;
                        row.Children.Add(button);
                        providerActions.Add(button);
                    }
                    ProviderBox.Items.Add(new ComboBoxItem { Content = row });
                }
                ProviderBox.SelectedIndex = selectedIndex;
            }
            finally { fillingProviderBox = false; }
        }

        private void ProviderBox_DropDownOpened(object sender, EventArgs e)
        {
            foreach (var button in providerActions) button.Visibility = Visibility.Visible;
        }
        private void ProviderBox_DropDownClosed(object sender, EventArgs e)
        {
            foreach (var button in providerActions) button.Visibility = Visibility.Collapsed;
        }

        private void DeleteProvider_click(object sender, RoutedEventArgs e)
        {
            int index = (sender as System.Windows.Controls.Button)?.Tag is int slotIndex ? slotIndex : -1;
            var slots = Translator.Setting.Configs[Summarizer.SUMMARY_API];
            if (index < 0 || index >= slots.Count || slots.Count <= 1) return;
            string name = slots[index].ConfigName;
            string label = string.IsNullOrWhiteSpace(name) ? $"配置 {index + 1}" : name.Trim();
            if (MessageBox.Show($"删除配置“{label}”？", "删除配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            slots.RemoveAt(index);
            int current = Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API];
            Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = current == index
                ? Math.Min(index, slots.Count - 1) : current > index ? current - 1 : current;
            if (Translator.Setting.Lecture.SummaryConfigName == name)
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
            Setting.SaveSoon();
            BindApiCard();
            LoadApiSetting();
            TestApiResult.Text = string.Empty;
        }

        private void MoveProviderUp_click(object sender, RoutedEventArgs e) => MoveProvider(sender, -1);
        private void MoveProviderDown_click(object sender, RoutedEventArgs e) => MoveProvider(sender, 1);

        private void MoveProvider(object sender, int direction)
        {
            int index = (sender as System.Windows.Controls.Button)?.Tag is int slotIndex ? slotIndex : -1;
            var slots = Translator.Setting.Configs[Summarizer.SUMMARY_API];
            int target = index + direction;
            if (index < 0 || target < 0 || target >= slots.Count) return;
            int selected = ProviderBox.SelectedIndex;
            if (selected == ApiProviders.All.Length + index) selected = ApiProviders.All.Length + target;
            else if (selected == ApiProviders.All.Length + target) selected = ApiProviders.All.Length + index;
            (slots[index], slots[target]) = (slots[target], slots[index]);
            int current = Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API];
            if (current == index) Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = target;
            else if (current == target) Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = index;
            Setting.SaveSoon();
            RefreshProviders(selected);
            ProviderBox.IsDropDownOpen = true;
        }

        private void OpenAsrQuota_click(object sender, RoutedEventArgs e) =>
            ShellUtil.OpenLink("https://bailian.console.aliyun.com/cn-beijing/costing-balance/free-quota?model=asr");

        private void SaveApiConfig_click(object sender, RoutedEventArgs e)
        {
            ApiUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            var current = ApiConfig;
            if (current == null || string.IsNullOrWhiteSpace(current.ApiUrl) || string.IsNullOrWhiteSpace(current.ModelName))
            {
                TestApiResult.Text = "请先填写 API 地址和模型。";
                return;
            }
            string? name = NameConfigDialog.Ask(Window.GetWindow(this), "保存模型配置", current.ConfigName);
            if (name == null) return;
            name = name.Trim();
            string? nameError = ApiProviders.ValidateConfigName(Summarizer.SUMMARY_API, name);
            if (nameError != null)
            {
                TestApiResult.Text = nameError;
                return;
            }
            var saved = new OpenAIConfig
            {
                ConfigName = name,
                ApiUrl = current.ApiUrl,
                ApiKey = current.ApiKey,
                ModelName = current.ModelName,
                Temperature = current.Temperature,
                ApiStyle = current.ApiStyle
            };
            Translator.Setting.Configs[Summarizer.SUMMARY_API].Add(saved);
            Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = Translator.Setting.Configs[Summarizer.SUMMARY_API].Count - 1;
            Translator.Setting.Lecture.SummaryConfigName = string.Empty;
            Setting.SaveSoon();
            BindApiCard();
            RefreshProviders(ApiProviders.All.Length + Translator.Setting.OpenAIConfigs.ToList().IndexOf(saved));
            FillKeyBox();
            LoadSummarySlots();
            TestApiResult.Text = $"已保存配置：{name}";
        }

        // Taken over on every keystroke / paste, independent of the page state, so the key can't be lost
        // by switching pages. Changing the key asks for the settings to be saved by itself.
        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (fillingKeyBox || ApiConfig == null)
                return;
            string key = ApiProviders.CleanKey(ApiKeyBox.Password);
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
            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                if (TranslateApiBox.ItemsSource is IEnumerable<ApiChoice> choices)
                    TranslateApiBox.SelectedItem = choices.FirstOrDefault(c => c.Key == setting.ApiName);
                LoadTargetLanguages();
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        // Shows that a key is stored without revealing it.
        private void ShowApiKeyStatus()
        {
            string key = ApiConfig?.ApiKey ?? string.Empty;
            ApiKeyStatus.Text = key.Length == 0
                ? "还没有保存 API Key。"
                : $"✓ 已保存：{ApiProviders.MaskKey(key)}，" +
                  $"保存在 {AppPaths.SettingFile}";
        }

        private bool fillingAsrKeyBox = false;
        private bool fillingLanguageBox = false;

        private void ShowSaveFolder()
        {
            SaveFolderBox.Text = LectureDocument.SaveFolder;
        }

        private void ChooseSaveFolder_click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择转录文件的保存位置",
                InitialDirectory = Directory.Exists(LectureDocument.SaveFolder) ? LectureDocument.SaveFolder : string.Empty,
            };
            if (dialog.ShowDialog() != true)
                return;
            Translator.Setting.Lecture.SaveFolder = dialog.FolderName;
            ShowSaveFolder();
        }

        private void OpenSaveFolder_click(object sender, RoutedEventArgs e)
        {
            LectureDialogs.ShowInFolder(LectureDocument.SaveFolder);
        }

        private void ResetSaveFolder_click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.Lecture.SaveFolder = string.Empty;
            ShowSaveFolder();
        }

        private void LoadEngine()
        {
            var lecture = Translator.Setting.Lecture;
            EngineBox.SelectedIndex = (int)lecture.Engine;
            AliyunPanel.Visibility = lecture.Engine == RecognitionEngine.Aliyun ? Visibility.Visible : Visibility.Collapsed;
            var languages = LectureLanguages.All.Cast<object>().Append(AUTO_LANGUAGE).ToList();
            fillingLanguageBox = true;
            AsrLanguageBox.ItemsSource = languages;
            AsrLanguageBox.SelectedItem = LectureLanguages.Find(lecture.AsrLanguage) ?? (object)AUTO_LANGUAGE;
            fillingLanguageBox = false;
            ShowLanguageHint();
            fillingAsrKeyBox = true;
            AsrKeyBox.Password = lecture.AsrApiKey;
            fillingAsrKeyBox = false;
            ShowAsrKeyStatus();
        }

        private async void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || EngineBox.SelectedIndex < 0)
                return;
            var engine = (RecognitionEngine)EngineBox.SelectedIndex;
            Translator.Setting.Lecture.Engine = engine;
            AliyunPanel.Visibility = engine == RecognitionEngine.Aliyun ? Visibility.Visible : Visibility.Collapsed;
            ShowLanguageHint();
            if (ClassSession.IsRunning || ClassSession.IsStarting)
            {
                // The class keeps the engine it started with, and LiveCaptions stays as that engine needs it.
                SnackbarHost.Show("识别方式已更改。", "点“停止”再点“开始”后生效。", SnackbarType.Info, timeout: 3);
                return;
            }
            // LiveCaptions is only needed by the system engine; with the cloud engine it is closed to save power
            // (it is started again when a class with the system engine starts).
            if (engine == RecognitionEngine.Aliyun)
            {
                // Not on the UI thread: closing it waits for its process to end.
                await Task.Run(Translator.CloseLiveCaptions);
                ShowLiveCaptionsState();
            }
            else if (Translator.Window != null && LiveCaptionsHandler.IsHidden)
            {
                try
                {
                    // Out of the way so far (minimized); as the system engine's source it has to be on screen.
                    LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                }
                catch (Exception)
                {
                    // Best effort: the LiveCaptions window may already be gone.
                }
            }
        }

        private void AsrLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || fillingLanguageBox)
                return;
            Translator.Setting.Lecture.AsrLanguage =
                AsrLanguageBox.SelectedItem is LectureLanguage language ? language.Code : LectureLanguages.AUTO;
        }

        // With the system engine the language is chosen inside Live Captions; ours is for everything after.
        private void ShowLanguageHint()
        {
            AsrLanguageHint.Text = Translator.Setting.Lecture.Engine == RecognitionEngine.Aliyun
                ? "识别按这个语言进行（“自动判断”交给模型）；“只用课程原语言”时，小结、笔记、OCR 和播报也用它。"
                : "系统实时字幕的识别语言要在它自己的 ⚙️ 设置里选（点下面“显示系统实时辅助字幕”），并关掉它自带的翻译。" +
                  "这里选的语言用于小结、笔记、课件 OCR 和“只用课程原语言”时的播报。";
        }

        private void AsrKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (fillingAsrKeyBox)
                return;
            var lecture = Translator.Setting.Lecture;
            string key = ApiProviders.CleanKey(AsrKeyBox.Password);
            if (key.Length == 0 && lecture.AsrApiKey.Length > 0 && !AsrKeyBox.IsKeyboardFocusWithin)
            {
                LoadEngine();
                return;
            }
            if (key != lecture.AsrApiKey)
                lecture.AsrApiKey = key;
            ShowAsrKeyStatus();
        }

        private void ShowAsrKeyStatus()
        {
            string key = Translator.Setting.Lecture.AsrApiKey;
            AsrKeyStatus.Text = key.Length == 0
                ? "还没有保存阿里云 API Key。"
                : $"✓ 已保存：{ApiProviders.MaskKey(key)}";
        }

        private async void TestAsr_click(object sender, RoutedEventArgs e)
        {
            AsrEndpointBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            TestAsrButton.IsEnabled = false;
            TestAsrResult.Text = "测试中……";
            try
            {
                string? note = await CloudAsr.Test();
                TestAsrResult.Text = note == null ? "✓ 连接成功" : $"✓ 连接成功（{note}）";
            }
            catch (Exception ex)
            {
                TestAsrResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                TestAsrButton.IsEnabled = true;
            }
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

        private void TranslateApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || TranslateApiBox.SelectedItem is not ApiChoice choice)
                return;
            Translator.Setting.ApiName = choice.Key;
            initializing = true;
            try
            {
                LoadTargetLanguages();
            }
            finally
            {
                initializing = false;
            }
        }

        private void LoadTargetLanguages()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            PropertyInfo? languagesProp = null;
            // Traverse base classes to find `SupportedLanguages`
            for (var type = configType; type != null && languagesProp == null; type = type.BaseType)
                languagesProp = type.GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            languagesProp ??= typeof(TranslateAPIConfig).GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp!.GetValue(null)!;
            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.ItemsSource = supportedLanguages.Keys.ToList();
            TargetLangBox.SelectedItem = targetLang;
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!initializing && TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!initializing && !string.IsNullOrWhiteSpace(TargetLangBox.Text))
                Translator.Setting.TargetLanguage = TargetLangBox.Text.Trim();
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (settingWindow != null && settingWindow.IsLoaded)
                settingWindow.Activate();
            else
            {
                settingWindow = new SettingWindow();
                settingWindow.Closed += (sender, args) => settingWindow = null;
                settingWindow.Show();
            }
        }

        private void ShowLiveCaptionsState()
        {
            LiveCaptionsButton.Content = LiveCaptionsHandler.IsHidden || Translator.Window == null
                ? "显示系统实时辅助字幕"
                : "隐藏系统实时辅助字幕";
        }

        private async void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            // What the button says it does.
            bool show = LiveCaptionsHandler.IsHidden || Translator.Window == null;
            LiveCaptionsButton.IsEnabled = false;
            try
            {
                // Started first if it is not running: it may have been closed by hand, or not be needed so far
                // (cloud engine). Starting it can take seconds.
                var window = await Task.Run(Translator.EnsureLiveCaptions);
                if (show)
                    LiveCaptionsHandler.RestoreLiveCaptions(window);
                else
                    LiveCaptionsHandler.HideLiveCaptions(window);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 操作失败。", ex.Message, SnackbarType.Error, timeout: 3);
            }
            finally
            {
                LiveCaptionsButton.IsEnabled = true;
            }
            ShowLiveCaptionsState();
        }

        private async void ExtractGlossary_click(object sender, RoutedEventArgs e)
        {
            GlossaryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ExtractGlossaryButton.IsEnabled = false;
            GlossaryResult.Text = "正在从课件中提取术语……";
            try
            {
                var (text, added) = await Glossary.ExtractFromSlides();
                Translator.Setting.Lecture.Glossary = text;
                GlossaryBox.Text = text;
                GlossaryResult.Text = added > 0 ? $"✓ 新增 {added} 个术语，请检查一下译法" : "没有发现新的术语";
            }
            catch (Exception ex)
            {
                GlossaryResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                ExtractGlossaryButton.IsEnabled = true;
            }
        }

        private async void TestApi_click(object sender, RoutedEventArgs e)
        {
            // Commit fields that only update on lost focus.
            ApiUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

            TestApiButton.IsEnabled = false;
            TestApiResult.Text = "测试中……";
            try
            {
                TestApiResult.Text = await Summarizer.TestConnection(ApiConfig);
            }
            catch (Exception ex)
            {
                TestApiResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                TestApiButton.IsEnabled = true;
            }
        }

        // The sound devices and voices as Windows listed them last, kept for the next visit of the page (which is
        // built anew every time): asking for them takes a moment, more so with Bluetooth devices, and the page
        // should not hang for it.
        private static List<AudioDevice> microphones = new();
        private static List<AudioDevice> outputs = new();
        private static bool devicesKnown = false;
        private static List<string> installedVoices = new();
        private int deviceRound = 0;

        private async Task LoadDevices()
        {
            int round = ++deviceRound;
            List<AudioDevice> capture, render;
            try
            {
                (capture, render) = await Task.Run(() =>
                    (AudioDevices.List(DataFlow.Capture), AudioDevices.List(DataFlow.Render)));
            }
            catch (Exception)
            {
                return;
            }
            // Asked again meanwhile (the refresh button), or the page was left; or nothing is new.
            if (round != deviceRound || !IsLoaded ||
                (devicesKnown && capture.SequenceEqual(microphones) && render.SequenceEqual(outputs)))
                return;
            microphones = capture;
            outputs = render;
            devicesKnown = true;
            ShowDevices();
        }

        private void ShowDevices()
        {
            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                SourceBox.SelectedIndex = (int)Translator.Setting.Lecture.InputSource;
                MicBox.IsEnabled = Translator.Setting.Lecture.InputSource == InputSource.Microphone;
                // The very first visit: the lists stay empty for the moment it takes to read them, instead of
                // calling the chosen earphones "not connected".
                if (!devicesKnown)
                    return;

                MicBox.ItemsSource = microphones;
                MicBox.SelectedItem = microphones.FirstOrDefault(device => device.IsDefault);

                var choices = new List<object> { DEFAULT_OUTPUT };
                choices.AddRange(outputs);
                string speechDeviceId = Translator.Setting.Lecture.SpeechDeviceId;
                var chosen = outputs.FirstOrDefault(device => device.Id == speechDeviceId);
                if (chosen == null && !string.IsNullOrEmpty(speechDeviceId))
                {
                    // Earphones that are not connected right now. They stay the choice, and are shown as that.
                    chosen = new AudioDevice(speechDeviceId, MISSING_OUTPUT, false);
                    choices.Add(chosen);
                }
                SpeechDeviceBox.ItemsSource = choices;
                SpeechDeviceBox.SelectedItem = chosen ?? (object)DEFAULT_OUTPUT;
                ShowSpeechDeviceHint();
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        private void RefreshDevices_click(object sender, RoutedEventArgs e)
        {
            _ = LoadDevices();
        }

        private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || SourceBox.SelectedIndex < 0)
                return;
            var source = (InputSource)SourceBox.SelectedIndex;
            Translator.Setting.Lecture.InputSource = source;
            MicBox.IsEnabled = source == InputSource.Microphone;
            ShowSpeechDeviceHint();

            const string SET_AT_START = "✓ 已保存，点“开始”时会自动设置好系统实时辅助字幕。";
            var window = Translator.Window;
            if (window == null)
            {
                bool cloud = (ClassSession.IsRunning ? ClassSession.Engine : Translator.Setting.Lecture.Engine) ==
                             RecognitionEngine.Aliyun;
                // The cloud engine opens its source when the class starts.
                SourceStatus.Text = !cloud ? SET_AT_START
                    : ClassSession.IsRunning ? "✓ 已保存。正在上的这节课还在听原来的来源，点“停止”再点“开始”后生效。"
                    : "✓ 已保存。阿里云识别会直接使用这里选的声音来源。";
                return;
            }

            bool useMicrophone = source == InputSource.Microphone;
            SourceStatus.Text = "正在设置实时字幕……";
            SourceBox.IsEnabled = false;
            bool ok;
            try
            {
                ok = await Task.Run(() => LiveCaptionsMicrophone.Set(window, useMicrophone));
            }
            catch (Exception)
            {
                ok = false;
            }
            finally
            {
                SourceBox.IsEnabled = true;
            }

            if (ok)
            {
                SourceStatus.Text = useMicrophone
                    ? "✓ 已打开实时字幕的\"包含麦克风音频\"。"
                    : "✓ 已关闭实时字幕的\"包含麦克风音频\"，只听电脑播放的声音。";
            }
            else
            {
                SourceStatus.Text = useMicrophone
                    ? "没能自动设置。已打开实时字幕的设置菜单，请在\"首选项\"里勾选\"包含麦克风音频\"，然后点\"隐藏\"。"
                    : "没能自动设置。已打开实时字幕的设置菜单，请在\"首选项\"里取消\"包含麦克风音频\"，然后点\"隐藏\"。";
                try
                {
                    await Task.Run(() => LiveCaptionsMicrophone.ShowSettings(window));
                }
                catch (Exception)
                {
                    // LiveCaptions is not running any more (closed by hand): the next class sets it up.
                    SourceStatus.Text = SET_AT_START;
                }
            }
        }

        private async void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || MicBox.SelectedItem is not AudioDevice device || device.IsDefault)
                return;
            try
            {
                AudioDevices.SetDefault(device.Id);
                // LiveCaptions takes the new default microphone when it starts: a running class starts it again
                // at once, otherwise the next class does.
                await Task.Run(Translator.CloseLiveCaptions);
                SourceStatus.Text = $"✓ 已改用：{device.Name}";
            }
            catch (Exception ex)
            {
                SourceStatus.Text = $"✗ 切换麦克风失败：{ex.Message}";
                // Back to the microphone that still is the default.
                ShowDevices();
            }
            ShowLiveCaptionsState();
            await LoadDevices();
        }

        private void SpeechDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Translator.Setting.Lecture.SpeechDeviceId =
                SpeechDeviceBox.SelectedItem is AudioDevice device ? device.Id : string.Empty;
            ShowSpeechDeviceHint();
        }

        private void ShowSpeechDeviceHint()
        {
            if (!devicesKnown)
                return;
            string speechDeviceId = Translator.Setting.Lecture.SpeechDeviceId;
            var device = outputs.FirstOrDefault(output => output.Id == speechDeviceId);
            if (!string.IsNullOrEmpty(speechDeviceId) && device == null)
            {
                SpeechDeviceHint.Text = "这个设备现在没有连接，播报不会出声；连上以后会从它播出。";
                return;
            }
            SpeechDeviceHint.Text = device == null || device.IsDefault
                ? "播报和电脑声音走同一个设备，实时字幕会听到播报，所以播报时会暂停接收字幕。" +
                  "线下课建议在这里直接选蓝牙耳机，并把 Windows 默认输出设为电脑扬声器。"
                : "播报只从这个设备播出，不会被实时字幕听到。";
        }

        private void LoadOcrLanguages()
        {
            var languages = new List<object> { AUTO_OCR };
            var available = SlideOcr.AvailableLanguages();
            languages.AddRange(available);
            OcrLanguageBox.ItemsSource = languages;
            string current = Translator.Setting.Lecture.OcrLanguage;
            OcrLanguageBox.SelectedItem = available.FirstOrDefault(l => l.Tag == current) ?? (object)AUTO_OCR;
        }

        private void OcrLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Translator.Setting.Lecture.OcrLanguage =
                OcrLanguageBox.SelectedItem is SlideOcr.OcrLanguage language ? language.Tag : string.Empty;
        }

        private void OnCurrentPageChanged(int? page)
        {
            Dispatcher.InvokeAsync(ShowSlidesInfo);
        }

        private void OnSlidesLoadingChanged()
        {
            Dispatcher.InvokeAsync(ShowSlidesInfo);
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
                ClearSlidesButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (!SlideDeck.IsLoaded)
            {
                SlidesInfo.Text = "未上传课件：按话题变化分节。";
                ClearSlidesButton.Visibility = Visibility.Collapsed;
                return;
            }
            string page = Summarizer.CurrentPage == null ? "识别中" : $"第 {Summarizer.CurrentPage} 页";
            string ocr = SlideDeck.OcrPageCount > 0 ? $"（其中 {SlideDeck.OcrPageCount} 页用 OCR 识别）" : string.Empty;
            SlidesInfo.Text = $"{SlideDeck.FileName}\n共 {SlideDeck.Pages.Count} 页{ocr}，当前：{page}";
            ClearSlidesButton.Visibility = Visibility.Visible;
        }

        private async void LoadSlides_click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = SlideDeck.FileFilter, RestoreDirectory = true };
            if (dialog.ShowDialog() != true)
                return;

            SlidesInfo.Text = "读取中……";
            LoadSlidesButton.IsEnabled = false;
            readingSlides = true;
            try
            {
                await SlideDeck.Load(dialog.FileName, new Progress<string>(text => SlidesInfo.Text = text));
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

        private void ClearSlides_click(object sender, RoutedEventArgs e)
        {
            SlideDeck.Clear();
            ShowSlidesInfo();
        }

        private async Task LoadVoices()
        {
            List<string> voices;
            try
            {
                voices = await Task.Run(Speaker.GetVoices);
            }
            catch (Exception)
            {
                return;
            }
            if (!IsLoaded || voices.SequenceEqual(installedVoices))
                return;
            installedVoices = voices;
            ShowVoices();
        }

        private void ShowVoices()
        {
            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                var voices = new List<string> { AUTO_VOICE };
                voices.AddRange(installedVoices);
                string current = Translator.Setting.Lecture.VoiceName;
                // A chosen voice stays in the list while the voices are still being read, or when it was removed
                // from Windows (the speaker then picks one by language).
                if (!string.IsNullOrEmpty(current) && !voices.Contains(current))
                    voices.Add(current);
                VoiceBox.ItemsSource = voices;
                VoiceBox.SelectedItem = string.IsNullOrEmpty(current) ? AUTO_VOICE : current;
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || VoiceBox.SelectedItem is not string voice)
                return;
            Translator.Setting.Lecture.VoiceName = voice == AUTO_VOICE ? string.Empty : voice;
        }

        private void TestSpeech_click(object sender, RoutedEventArgs e)
        {
            Speaker.Speak("这是一段试听。老师每讲完一节，你会在耳机里听到这一节的小结。");
        }

        private void SoundSettings_click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShellUtil.Open("ms-settings:sound");
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 无法打开声音设置。", ex.Message, SnackbarType.Error, timeout: 2);
            }
        }

        private void ResetPrompt_click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.Lecture.SummaryPrompt = LectureState.DEFAULT_SUMMARY_PROMPT;
        }
    }
}
