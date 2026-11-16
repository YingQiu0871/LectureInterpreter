using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    // `LectureInterpreter.exe --uicheck <folder>`: opens every page once, with a few sample records, and writes
    // into <folder> where everything on it stands (layout.txt) and a picture of each page (*.png). CI runs it on
    // Windows, where nobody looks at the pages: a page that cannot be opened makes it end with 1 (a resource name
    // that does not exist, for one, is only noticed when its page is loaded), and the pictures show what a change
    // did to the look.
    //
    // It works in a data folder of its own (<folder>/data, new every time) with the cloud engine chosen, so it
    // never touches the user's settings and history, and leaves Live Captions and a copy of the program that is
    // running alone. What the program logs while it is checked (error.log) is found there.
    public static class UiCheck
    {
        public static bool Active { get; private set; }

        private const string OPTION = "--uicheck";
        private const string COURSE = "机器学习导论";
        private const string LECTURE = "第 3 讲 梯度下降";
        private const int MAX_ITEMS = 3;
        private const int MAX_TEXT = 18;
        private const double PICTURE_SCALE = 1.5;
        // A window that waits for an answer nobody gives must not keep CI waiting.
        private static readonly TimeSpan TIME_LIMIT = TimeSpan.FromMinutes(3);

        private static string folder = string.Empty;
        private static readonly List<string> errors = new();
        private static readonly StringBuilder layout = new();
        private static System.Threading.Timer? timeLimit;

        // To be called before anything reads the settings. True when the check was asked for.
        public static bool Prepare(string[] args)
        {
            int at = Array.IndexOf(args, OPTION);
            if (at < 0)
                return false;
            Active = true;
            folder = System.IO.Path.GetFullPath(at + 1 < args.Length ? args[at + 1] : "uicheck");
            Directory.CreateDirectory(folder);

            string data = System.IO.Path.Combine(folder, "data");
            Directory.CreateDirectory(data);
            foreach (string old in new[] { "translation_history.db-wal", "translation_history.db-shm", "error.log" })
                File.Delete(System.IO.Path.Combine(data, old));
            // Both files are there before the data folder is looked for, which would otherwise copy the ones of
            // an old install next to the program. The settings: the cloud engine (Live Captions is not started),
            // no check for updates, light, files saved inside the data folder, and not the first use (the
            // welcome window is opened below).
            File.WriteAllText(System.IO.Path.Combine(data, "setting.json"),
                "{\"Lecture\":{\"Engine\":1,\"AutoCheckUpdate\":false,\"SaveFolder\":" +
                System.Text.Json.JsonSerializer.Serialize(System.IO.Path.Combine(data, "saved")) +
                "},\"MainWindow\":{\"Theme\":\"Light\",\"Topmost\":false}}");
            File.WriteAllBytes(System.IO.Path.Combine(data, "translation_history.db"), Array.Empty<byte>());
            Environment.SetEnvironmentVariable("LCT_DATA_DIR", data);

            timeLimit = new System.Threading.Timer(_ =>
            {
                Fail($"not finished after {TIME_LIMIT.TotalMinutes:0} minutes");
                WriteReport();
                Environment.Exit(2);
            }, null, TIME_LIMIT, Timeout.InfiniteTimeSpan);
            // An error that ends the program: the report says so, with what was measured until then.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Fail("the program ended: " + e.ExceptionObject);
                WriteReport();
            };
            return true;
        }

        private static void Fail(string error)
        {
            lock (errors)
            {
                errors.Add(error);
            }
        }

        private static string[] Errors()
        {
            lock (errors)
            {
                return errors.ToArray();
            }
        }

        // To be called before the program's own handler of UI errors is set: that one lets the program go on, and
        // the error would not be counted here.
        public static void Attach(Application app)
        {
            app.DispatcherUnhandledException += (s, e) => Fail(e.Exception.ToString());
            // When there is nothing left to do the main window is up and has shown its first page.
            app.Startup += (s, e) => app.Dispatcher.InvokeAsync(Run, DispatcherPriority.ApplicationIdle);
        }

        private static async void Run()
        {
            var window = Application.Current.MainWindow as MainWindow;
            try
            {
                if (window == null)
                    throw new InvalidOperationException("no main window");
                window.Width = 900;
                window.Height = 560;
                await Empty(window);
                var lines = await Seed();

                await Pages(window, lines, "", describe: true);
                await Windows(window);

                // The smallest the window can be: only what no longer fits is listed.
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                await Pages(window, lines, "-small", describe: false);

                window.Width = 900;
                window.Height = 560;
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                await Pages(window, lines, "-dark", describe: false, listClipped: false);
            }
            catch (Exception ex)
            {
                Fail(ex.ToString());
            }
            WriteReport();
            Application.Current.Shutdown(Errors().Length == 0 ? 0 : 1);
        }

        private static void WriteReport()
        {
            try
            {
                var report = new StringBuilder();
                lock (layout)
                {
                    report.Append(layout);
                }
                string[] found = Errors();
                report.AppendLine($"== result: {found.Length} error(s)");
                foreach (string error in found)
                    report.AppendLine(error);
                File.WriteAllText(System.IO.Path.Combine(folder, "layout.txt"), report.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The exit code still says how it went.
            }
        }

        // ---- Sample records ----

        private static readonly (string Source, string Translation)[] Sentences =
        {
            ("Today we look at how a model actually learns from data.", "今天我们来看模型究竟是怎样从数据中学习的。"),
            ("We need a loss function that tells us how wrong the predictions are.", "我们需要一个损失函数（loss function），告诉我们预测错了多少。"),
            ("Gradient descent moves the parameters a small step against the gradient.", "梯度下降（gradient descent）让参数逆着梯度方向走一小步。"),
            ("The size of that step is called the learning rate.", "这一步的大小叫做学习率（learning rate）。"),
            ("If the learning rate is too large, the loss can jump around or even diverge.", "学习率太大时，损失会来回震荡，甚至发散。"),
            ("If it is too small, training takes forever.", "太小的话，训练会慢得没有尽头。"),
            ("In practice we use mini-batches instead of the whole data set.", "实际中我们用小批量（mini-batch），而不是整个数据集。"),
            ("That is stochastic gradient descent, and it is what almost everybody uses.", "这就是随机梯度下降，几乎所有人都在用它。"),
            ("Next week we will see how momentum helps.", "下周我们来看动量（momentum）有什么帮助。"),
        };

        private static async Task<List<HistoryLine>> Seed()
        {
            SectionLogger.EnsureTables();
            foreach (string name in new[] { "分子生物学", "宏观经济学", "德语 B1" })
                await LectureStore.AddCourse(name);

            var start = DateTime.Now.AddMinutes(-20);
            long before = await SectionLogger.GetMaxHistoryId();
            long earlier = await LectureStore.StartLecture(COURSE, "第 2 讲 线性回归", start.AddDays(-7), before);
            await LectureStore.FinishLecture(earlier, start.AddDays(-7).AddMinutes(45), before);

            long lecture = await LectureStore.StartLecture(COURSE, LECTURE, start, before);
            foreach (var (source, translation) in Sentences)
                await SQLiteHistoryLogger.LogTranslation(source, translation, "zh-CN", "UiCheck");
            var lines = await SectionLogger.LoadHistoryRange(before);
            if (lines.Count != Sentences.Length)
                throw new InvalidOperationException($"{lines.Count} of {Sentences.Length} sample sentences were stored");
            await SectionLogger.AddSection(start, start.AddMinutes(6), lines[0].Id, lines[3].Id,
                "梯度下降的基本思路\n- 损失函数（loss function）衡量预测与真实值的差距。\n" +
                "- 参数沿梯度的反方向每次走一小步。\n- 步长由学习率（learning rate）决定。", 3);
            await SectionLogger.AddSection(start.AddMinutes(6), start.AddMinutes(13), lines[4].Id, lines[7].Id,
                "学习率与小批量\n- 学习率太大，损失会震荡甚至发散；太小则训练很慢。\n" +
                "- 实际使用小批量（mini-batch）的随机梯度下降。", null);
            await LectureStore.FinishLecture(lecture, DateTime.Now, lines[^1].Id);
            Translator.Setting.Lecture.CurrentCourse = COURSE;
            return lines;
        }

        // ---- The pages, one after the other ----

        // Before there is anything to show: what the empty pages say. (The program starts on the caption page;
        // it is opened again at the end, so that the sample records are read when the pages are gone through.)
        private static async Task Empty(MainWindow window)
        {
            await Settle(600);
            if (All<CaptionPage>(window).FirstOrDefault() is CaptionPage captions)
            {
                Describe("0-captions-empty", captions, window, onlyClipped: false);
                Picture("0-captions-empty", window);
            }
            Describe("0-timeline-empty", await Open<TimelinePage>(window), window, onlyClipped: false);
            Picture("0-timeline-empty", window);
            Describe("0-history-empty", await Open<HistoryPage>(window), window, onlyClipped: false);
            Picture("0-history-empty", window);
        }

        private static async Task Pages(MainWindow window, List<HistoryLine> lines, string suffix, bool describe,
            bool listClipped = true)
        {
            void Note(string name, FrameworkElement root)
            {
                if (describe)
                    Describe(name + suffix, root, window, onlyClipped: false);
                else if (listClipped)
                    Describe(name + suffix, root, window, onlyClipped: true);
                Picture(name + suffix, window);
            }

            // Captions: the sentences of a class, the sentence being spoken, the sections.
            var captions = await Open<CaptionPage>(window);
            // (A page without sentences would pass as well: when the list is not found the check says so.)
            var sentences = typeof(CaptionPage).GetField("transcript", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(captions) as TranscriptView
                ?? throw new InvalidOperationException("the list of sentences of the caption page was not found");
            sentences.Apply(lines);
            if (Translator.Caption != null)
            {
                Translator.Caption.DisplayOriginalCaption = "Momentum keeps a running average of the past gradients.";
                Translator.Caption.DisplayTranslatedCaption = "动量会保留过去梯度的滑动平均。";
            }
            await Settle();
            // (The first time the whole window is listed, with its title bar and its navigation.)
            Note("1-captions", describe ? window : captions);
            if (Named<ToggleButton>(captions, "BigModeToggle") is ToggleButton big)
            {
                big.IsChecked = true;
                await Settle();
                Note("1-captions-large", captions);
                big.IsChecked = false;
                await Settle();
            }

            // Settings: everything that can be opened is open.
            var settings = await Open<LecturePage>(window, 900);
            foreach (var expander in All<Expander>(settings))
                expander.IsExpanded = true;
            await Settle(600);
            Note("2-settings", settings);
            if (Named<ScrollViewer>(settings, "PageScroll")?.Content is FrameworkElement everything)
                Picture("2-settings-whole" + suffix, everything);

            var timeline = await Open<TimelinePage>(window);
            Note("3-timeline", timeline);

            // History: the courses, the classes of one, one class, and the list of all sentences.
            var history = await Open<HistoryPage>(window);
            Note("4-history-courses", history);
            Press(All<Border>(history).FirstOrDefault(card => card.DataContext is CourseEntry { Name: COURSE }));
            await Settle(600);
            Note("4-history-classes", history);
            Press(All<Border>(history).FirstOrDefault(card => card.DataContext is LectureRecord { Name: LECTURE }));
            await Settle(600);
            Note("4-history-class", history);
            if (Named<ScrollViewer>(history, "DetailScroll")?.Content is FrameworkElement transcript)
                Picture("4-history-class-whole" + suffix, transcript);
            Click(Named<ButtonBase>(history, "CrumbHome"));
            await Settle(600);
            Click(Named<ButtonBase>(history, "AllSentences"));
            await Settle(600);
            Note("4-history-all", history);

            var about = await Open<InfoPage>(window);
            Note("5-about", about);
        }

        // The windows next to the main one.
        private static async Task Windows(MainWindow window)
        {
            var welcome = new WelcomeWindow { Owner = window };
            welcome.Show();
            await Settle(600);
            Describe("6-welcome", welcome, welcome, onlyClipped: false);
            Picture("6-welcome", welcome);
            welcome.Close();

            var apis = new SettingWindow { Owner = window };
            apis.Show();
            await Settle(900);
            Describe("7-apis", apis, apis, onlyClipped: true);
            Picture("7-apis", apis);
            apis.Close();
            await Settle();
        }

        private static async Task<T> Open<T>(MainWindow window, int wait = 600) where T : Page
        {
            window.RootNavigation.Navigate(typeof(T));
            // The page is there after its transition; what it reads from the database a moment later.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Settle(attempt == 0 ? wait : 100);
                if (All<T>(window).FirstOrDefault() is T page && page.IsLoaded)
                    return page;
            }
            throw new InvalidOperationException($"{typeof(T).Name} did not open");
        }

        private static async Task Settle(int milliseconds = 300)
        {
            await Task.Delay(milliseconds);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found)
                    yield return found;
                foreach (var deeper in All<T>(child))
                    yield return deeper;
            }
        }

        private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
            All<T>(root).FirstOrDefault(element => element.Name == name);

        private static void Click(ButtonBase? button)
        {
            if (button == null)
                throw new InvalidOperationException("a button of the check is not on its page");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        private static void Press(UIElement? card)
        {
            if (card == null)
                throw new InvalidOperationException("a sample record of the check is not on its page");
            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            });
        }

        // ---- Where everything stands ----

        // One line for everything that can be seen: what it is, its name, where it stands in `origin` (left, top,
        // width x height), how it looks, what it says. "!clip": it is larger than its place and cut off there.
        private static void Describe(string name, FrameworkElement root, Visual origin, bool onlyClipped)
        {
            lock (layout)
            {
                layout.AppendLine($"== {name} {Number(root.ActualWidth)}x{Number(root.ActualHeight)}" +
                                  (onlyClipped ? " (only what is cut off)" : string.Empty));
                Walk(root, origin, 0, onlyClipped);
            }
        }

        private static void Walk(DependencyObject node, Visual origin, int depth, bool onlyClipped)
        {
            if (node is FrameworkElement element)
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                    return;
                bool leaf = IsLeaf(element);
                string? line = Line(element, origin, leaf);
                if (line != null && (!onlyClipped || line.EndsWith("!clip", StringComparison.Ordinal)))
                {
                    layout.Append(' ', depth).AppendLine(line);
                    depth++;
                }
                if (leaf)
                    return;
            }

            // Of a list its first rows: the others look the same.
            bool rows = node is Panel { IsItemsHost: true };
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (rows && i >= MAX_ITEMS)
                {
                    if (!onlyClipped)
                        layout.Append(' ', depth).AppendLine($"(+{count - i} more)");
                    break;
                }
                Walk(VisualTreeHelper.GetChild(node, i), origin, depth, onlyClipped);
            }
        }

        // What is listed as one thing, with the words inside it.
        private static bool IsLeaf(FrameworkElement element) =>
            element is TextBlock or ButtonBase or ComboBox or TextBoxBase or PasswordBox or Slider or ScrollBar
                or DataGrid or Separator or GridSplitter or Image or Shape or Wpf.Ui.Controls.IconElement
                or Wpf.Ui.Controls.AutoSuggestBox;

        private static string? Line(FrameworkElement element, Visual origin, bool leaf)
        {
            // A name given on a page (not the names of the parts of a control).
            bool named = !string.IsNullOrEmpty(element.Name) &&
                         (element.TemplatedParent == null || element.TemplatedParent is ContentPresenter);
            var parts = new List<string>();
            switch (element)
            {
                case TextBlock text:
                    string said = TextOf(text);
                    if (said.Length == 0 && !named)
                        return null;
                    parts.Add($"s{Number(text.FontSize)}{(text.FontWeight.ToOpenTypeWeight() >= 600 ? "b" : string.Empty)}");
                    parts.Add(Color(text.Foreground));
                    parts.Add(Quote(said));
                    break;
                case Border border:
                    bool filled = IsSeen(border.Background);
                    bool outlined = IsSeen(border.BorderBrush) && border.BorderThickness != default;
                    if (!filled && !outlined && !named)
                        return null;
                    if (filled)
                        parts.Add("bg" + Color(border.Background));
                    if (outlined)
                        parts.Add("bd" + Color(border.BorderBrush));
                    if (border.CornerRadius.TopLeft > 0)
                        parts.Add("r" + Number(border.CornerRadius.TopLeft));
                    break;
                case Shape shape:
                    parts.Add("bg" + Color(shape.Fill));
                    break;
                default:
                    if (!leaf && !named)
                        return null;
                    if (element is ToggleButton toggle)
                        parts.Add(toggle.IsChecked == true ? "on" : "off");
                    if (element is Slider slider)
                        parts.Add(Number(slider.Value));
                    if (leaf)
                    {
                        string words = string.Join(" ", All<TextBlock>(element).Where(inner => inner.IsVisible)
                            .Select(TextOf).Where(inner => inner.Length > 0));
                        if (words.Length == 0 && element is TextBox box)
                            words = box.Text;
                        if (words.Length > 0)
                            parts.Add(Quote(words));
                    }
                    break;
            }

            Rect bounds;
            try
            {
                bounds = element.TransformToAncestor(origin).TransformBounds(new Rect(element.RenderSize));
            }
            catch (InvalidOperationException)
            {
                // Not inside `origin` (the content of a popup).
                return null;
            }
            if (element.Opacity < 1)
                parts.Add("o" + Number(element.Opacity));
            if (LayoutInformation.GetLayoutClip(element) != null)
                parts.Add("!clip");
            return $"{element.GetType().Name}{(named ? "#" + element.Name : string.Empty)} " +
                   $"{Number(bounds.X)},{Number(bounds.Y)} {Number(bounds.Width)}x{Number(bounds.Height)} " +
                   string.Join(" ", parts.Where(part => part.Length > 0));
        }

        private static string TextOf(TextBlock text)
        {
            string said = text.Inlines.Count > 1 ? new TextRange(text.ContentStart, text.ContentEnd).Text : text.Text;
            return string.Join(" ", said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Quote(string text) =>
            "\"" + (text.Length > MAX_TEXT ? text[..MAX_TEXT] + "…" : text) + "\"";

        private static string Number(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

        private static bool IsSeen(Brush? brush) =>
            brush != null && brush.Opacity > 0 && (brush is not SolidColorBrush solid || solid.Color.A > 0);

        private static string Color(Brush? brush)
        {
            if (brush is not SolidColorBrush solid)
                return brush == null ? string.Empty : "#gradient";
            int alpha = (int)Math.Round(solid.Color.A * solid.Opacity);
            return $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" + (alpha < 255 ? $"/{alpha:X2}" : string.Empty);
        }

        // ---- Pictures ----

        // Draws `element` as it is on screen (or all of it, for the content of a list that scrolls) on the
        // background of the window, which a picture of the window itself does not have.
        private static void Picture(string name, FrameworkElement element)
        {
            try
            {
                var size = new Size(element.ActualWidth, element.ActualHeight);
                if (size.Width < 1 || size.Height < 1)
                    return;
                var background = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(background, null, new Rect(size));
                    context.DrawRectangle(new VisualBrush(element)
                    {
                        Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(size),
                    }, null, new Rect(size));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * PICTURE_SCALE),
                    (int)Math.Ceiling(size.Height * PICTURE_SCALE), 96 * PICTURE_SCALE, 96 * PICTURE_SCALE,
                    PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(System.IO.Path.Combine(folder, name + ".png"));
                encoder.Save(file);
            }
            catch (Exception ex)
            {
                Fail($"picture {name}: {ex}");
            }
        }
    }
}
