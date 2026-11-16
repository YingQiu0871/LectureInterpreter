using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : UserControl
    {
        public const int MIN_HEIGHT = 420;
        private const double COURSE_CARD_MIN_WIDTH = 220;

        private enum View { Courses, Lectures, Detail, All }

        private View view = View.Courses;
        private CourseEntry? currentCourse = null;
        private LectureRecord? currentLecture = null;

        private int currentPage = 1;
        private int searchPage = 1;
        private int maxPage = 1;
        private int maxRowPerPage = 30;

        private string SearchText { get; set; } = string.Empty;

        public HistoryPage()
        {
            InitializeComponent();
            HistoryMaxRow.SelectionChanged += MaxRow_SelectionChanged;
            // As many columns of courses as fit with at least COURSE_CARD_MIN_WIDTH each; they share the width.
            CourseScroll.SizeChanged += (s, e) =>
                CourseList.Columns = Math.Clamp((int)(e.NewSize.Width / (COURSE_CARD_MIN_WIDTH + 12)), 1, 6);
            HistorySearchBox.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter)
                    await SubmitSearch();
            };
            HistorySearchBox.TextChanged += async (s, e) =>
            {
                // Clearing the box clears the search.
                if (string.IsNullOrEmpty(HistorySearchBox.Text) && !string.IsNullOrEmpty(SearchText))
                    await SubmitSearch();
            };
        }

        protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Shell.Main?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
            Translator.TranslationLogged -= OnTranslationLogged;
            Translator.TranslationLogged += OnTranslationLogged;
            await ShowCourses();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            HistoryList.ItemsSource = null;
            Translator.TranslationLogged -= OnTranslationLogged;
        }

        // A new sentence shows up at the top of the first page. Other pages are left alone: loading them again
        // would move the list under the reader's eyes.
        private void OnTranslationLogged()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (view == View.All && currentPage == 1)
                    await LoadHistory();
            });
        }

        // ---- Navigation: courses > recordings of a course > one recording ----

        private void SetView(View newView)
        {
            view = newView;
            CourseScroll.IsVisible = view == View.Courses;
            LectureScroll.IsVisible = view == View.Lectures;
            DetailScroll.IsVisible = view == View.Detail;
            AllView.IsVisible = view == View.All;

            // What an empty level says stands in the middle of the page, not inside the list: it is taken away
            // with the level, and put up again by whoever has loaded the next one.
            CourseEmptyHint.IsVisible = false;
            LectureEmptyHint.IsVisible = false;
            DetailEmptyHint.IsVisible = false;

            CrumbSep1.IsVisible = view != View.Courses;
            CrumbCourse.IsVisible = CrumbSep1.IsVisible;
            CrumbCourse.Content = view == View.All ? "全部句子" : currentCourse?.Name ?? string.Empty;
            CrumbSep2.IsVisible = view == View.Detail;
            CrumbLecture.IsVisible = CrumbSep2.IsVisible;
            CrumbLecture.Text = currentLecture?.Name ?? string.Empty;
            // The last name of the path is where the reader is.
            CrumbHome.Classes.Set("current", view == View.Courses);
            CrumbCourse.Classes.Set("current", view is View.Lectures or View.All);

            NewCourse.IsVisible = view == View.Courses;
            AllSentences.IsVisible = NewCourse.IsVisible;
            RenameLecture.IsVisible = view == View.Detail;
            OpenFile.IsVisible = RenameLecture.IsVisible;
            NotesDraft.IsVisible = RenameLecture.IsVisible;
            Export.IsVisible = view == View.All;
            Delete.IsVisible = Export.IsVisible;
        }

        private async Task ShowCourses()
        {
            currentCourse = null;
            currentLecture = null;
            SetView(View.Courses);
            try
            {
                var courses = await LectureStore.LoadCourses();
                CourseList.Children.Clear();
                foreach (var course in courses)
                    CourseList.Children.Add(BuildCourseCard(course));
                CourseEmptyHint.IsVisible = view == View.Courses && courses.Count == 0;
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取课程失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        private async Task ShowLectures(CourseEntry course)
        {
            currentCourse = course;
            currentLecture = null;
            SetView(View.Lectures);
            try
            {
                var lectures = await LectureStore.LoadLectures(course.Id);
                LectureList.Children.Clear();
                foreach (var lecture in lectures)
                    LectureList.Children.Add(BuildLectureCard(lecture));
                LectureEmptyHint.IsVisible = view == View.Lectures && ReferenceEquals(currentCourse, course)
                    && lectures.Count == 0;
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取录音失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        private async Task ShowDetail(LectureRecord lecture)
        {
            currentLecture = lecture;
            SetView(View.Detail);
            DetailList.ItemsSource = null;
            DetailInfo.Text = $"{lecture.CourseName} · {lecture.Info}";
            try
            {
                var sections = await LectureDocument.BuildForDisplay(lecture);
                // Another class or page was opened while this one was read.
                if (view != View.Detail || !ReferenceEquals(currentLecture, lecture))
                    return;
                DetailList.ItemsSource = sections;
                DetailEmptyHint.IsVisible = sections.Count == 0;
                DetailScroll.ScrollToHome();
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取本节课失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        // ---- Cards of the two folder levels (built here because they carry a context menu) ----

        // Drawn, not characters: not every Linux font has the glyphs.
        private const string FOLDER_ICON =
            "M2,6 a2,2 0 0 1 2,-2 h5 l2,2.5 h9 a2,2 0 0 1 2,2 v9.5 a2,2 0 0 1 -2,2 h-16 a2,2 0 0 1 -2,-2 z";
        private const string DOCUMENT_ICON =
            "M6,2 h8 l5,5 v13 a2,2 0 0 1 -2,2 h-11 a2,2 0 0 1 -2,-2 v-16 a2,2 0 0 1 2,-2 z M14,2 v5 h5 M8,12 h8 M8,16 h8";

        // A card of one of the two folder levels: a small picture, a name and one line about it. A long name or
        // line is cut off with "…" inside the card, whatever its width.
        private Border BuildCard(string icon, string iconBrush, string? iconFill, string title, string info, string? tooltip)
        {
            var picture = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(icon),
                Width = 24,
                Height = 24,
                StrokeThickness = 1.5,
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The colours belong to the light or the dark theme: they are looked up for the theme in use, and follow it.
            picture.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetResourceObservable(iconBrush));
            if (iconFill != null)
                picture.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, this.GetResourceObservable(iconFill));

            var name = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (tooltip != null)
                ToolTip.SetTip(name, tooltip);
            var text = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    name,
                    new TextBlock
                    {
                        Text = info,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontSize = 12,
                        Classes = { "secondary" },
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            };
            Grid.SetColumn(text, 1);
            return new Border
            {
                Classes = { "card", "link" },
                Padding = new Thickness(14, 12),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Children = { picture, text },
                },
            };
        }

        private Border BuildCourseCard(CourseEntry course)
        {
            var card = BuildCard(FOLDER_ICON, "BrandBrush", "BrandSoftBrush", course.Name, course.Info, course.Name);
            card.Margin = new Thickness(0, 0, 12, 12);
            AddCardActions(card, () => ShowLectures(course),
                "重命名", () => RenameCourse(course), "删除（仅空文件夹）", () => DeleteCourse(course));
            return card;
        }

        private Border BuildLectureCard(LectureRecord lecture)
        {
            var card = BuildCard(DOCUMENT_ICON, "TextSecondaryBrush", null, lecture.Name, lecture.Info, lecture.Name);
            card.Margin = new Thickness(0, 0, 0, 8);
            AddCardActions(card, () => ShowDetail(lecture),
                "重命名 / 移到其他课程", async () => await RenameLectureAndRefresh(lecture),
                "删除这节课", () => DeleteLecture(lecture));
            return card;
        }

        // A left click opens the card; the context menu has a rename and a delete entry.
        private static void AddCardActions(Border card, Func<Task> open, string renameHeader, Action rename,
                                           string deleteHeader, Action delete)
        {
            card.PointerReleased += async (s, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                    await open();
            };
            var renameItem = new MenuItem { Header = renameHeader };
            renameItem.Click += (s, e) => rename();
            var deleteItem = new MenuItem { Header = deleteHeader };
            deleteItem.Click += (s, e) => delete();
            card.ContextMenu = new ContextMenu { Items = { renameItem, deleteItem } };
        }

        private async void CrumbHome_Click(object? sender, RoutedEventArgs e) => await ShowCourses();

        private async void CrumbCourse_Click(object? sender, RoutedEventArgs e)
        {
            if (view == View.Detail && currentCourse != null)
                await ShowLectures(currentCourse);
        }

        private async void AllSentences_Click(object? sender, RoutedEventArgs e)
        {
            SetView(View.All);
            await LoadHistory();
        }

        private async void NewCourse_Click(object? sender, RoutedEventArgs e)
        {
            string? name = await LectureDialogs.AskText("新建课程", "课程名称", string.Empty);
            if (string.IsNullOrWhiteSpace(name))
                return;
            try
            {
                await LectureStore.AddCourse(name);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 新建课程失败。", ex.Message, SnackbarType.Error, timeout: 2);
            }
            await ShowCourses();
        }

        private async void RenameCourse(CourseEntry course)
        {
            string? name = await LectureDialogs.AskText("重命名课程", "课程名称", course.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == course.Name)
                return;
            try
            {
                await LectureStore.RenameCourse(course.Id, name);
            }
            catch (Exception)
            {
                SnackbarHost.Show("已经有同名的课程了。", "", SnackbarType.Warning, timeout: 2);
                // The course keeps its name, and so does the course new classes go to.
                return;
            }
            if (Translator.Setting.Lecture.CurrentCourse == course.Name)
                Translator.Setting.Lecture.CurrentCourse = name.Trim();
            await ShowCourses();
        }

        private async void DeleteCourse(CourseEntry course)
        {
            try
            {
                if (!await LectureStore.DeleteCourse(course.Id))
                    SnackbarHost.Show("文件夹里还有录音。", "请先进入文件夹删除或移走里面的课。", SnackbarType.Warning, timeout: 2);
                else if (Translator.Setting.Lecture.CurrentCourse == course.Name)
                    Translator.Setting.Lecture.CurrentCourse = string.Empty;
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 删除失败。", ex.Message, SnackbarType.Error, timeout: 2);
            }
            await ShowCourses();
        }

        private async Task RenameLectureAndRefresh(LectureRecord lecture)
        {
            var answer = await LectureDialogs.AskName("重命名", lecture.CourseName, lecture.Name, "确定", "取消");
            if (answer is not { } chosen)
                return;
            try
            {
                await LectureStore.RenameLecture(lecture.Id, chosen.Name, chosen.Course);
                var renamed = await LectureStore.GetLecture(lecture.Id);
                if (renamed != null && !string.IsNullOrEmpty(renamed.FilePath))
                    await LectureDocument.Save(renamed);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 重命名失败。", ex.Message, SnackbarType.Error, timeout: 2);
            }

            try
            {
                // The page follows the lecture to the course it is in now (the one it was in, if that failed).
                var updated = await LectureStore.GetLecture(lecture.Id);
                if (updated != null)
                {
                    var courses = await LectureStore.LoadCourses();
                    currentCourse = courses.FirstOrDefault(c => c.Id == updated.CourseId) ?? currentCourse;
                }
                if (view == View.Detail)
                {
                    if (updated != null)
                    {
                        updated.SentenceCount = lecture.SentenceCount;
                        await ShowDetail(updated);
                    }
                }
                else if (currentCourse != null)
                    await ShowLectures(currentCourse);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取录音失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        private async void RenameLecture_Click(object? sender, RoutedEventArgs e)
        {
            if (currentLecture != null)
                await RenameLectureAndRefresh(currentLecture);
        }

        private async void DeleteLecture(LectureRecord lecture)
        {
            if (ClassSession.IsRunning && ClassSession.CurrentLectureId == lecture.Id)
            {
                SnackbarHost.Show("这节课正在录制。", "请先点“停止”。", SnackbarType.Warning, timeout: 2);
                return;
            }
            if (!await LectureDialogs.Confirm($"要删除“{lecture.Name}”吗？",
                    "这节课的原文、译文和小节总结都会删除，已保存的文件不受影响。此操作无法撤销！", "删除"))
                return;
            try
            {
                await LectureStore.DeleteLecture(lecture);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 删除失败。", ex.Message, SnackbarType.Error, timeout: 2);
            }
            if (currentCourse != null)
                await ShowLectures(currentCourse);
        }

        private async void OpenFile_Click(object? sender, RoutedEventArgs e)
        {
            if (currentLecture == null)
                return;
            try
            {
                var lecture = await LectureStore.GetLecture(currentLecture.Id) ?? currentLecture;
                string path = await LectureDocument.Save(lecture);
                currentLecture.FilePath = path;
                LectureDialogs.ShowInFolder(path);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 保存文件失败。", ex.Message, SnackbarType.Error, timeout: 3, closeButton: true);
            }
        }

        private async void NotesDraft_Click(object? sender, RoutedEventArgs e)
        {
            if (currentLecture == null)
                return;
            NotesDraft.IsEnabled = false;
            try
            {
                var lecture = await LectureStore.GetLecture(currentLecture.Id) ?? currentLecture;
                // The draft goes next to the transcript file, so make sure that exists.
                if (string.IsNullOrEmpty(lecture.FilePath) || !File.Exists(lecture.FilePath))
                    await LectureDocument.Save(lecture);
                string? path = await LectureSaving.WriteNotesDraft(lecture);
                if (path != null)
                    LectureDialogs.ShowInFolder(path);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 笔记初稿生成失败。", ex.Message, SnackbarType.Error, timeout: 3, closeButton: true);
            }
            finally
            {
                NotesDraft.IsEnabled = true;
            }
        }

        private async void PageDown_Click(object? sender, RoutedEventArgs e)
        {
            if (currentPage - 1 >= 1)
                currentPage--;
            await LoadHistory();
        }

        private async void PageUp_Click(object? sender, RoutedEventArgs e)
        {
            if (currentPage < maxPage)
                currentPage++;
            await LoadHistory();
        }

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            // The class being recorded lives in these tables: its later sentences would belong to no class.
            if (ClassSession.IsRunning)
            {
                SnackbarHost.Show("正在录制时不能全部删除。", "请先点“停止”。", SnackbarType.Warning, timeout: 2);
                return;
            }
            if (!await LectureDialogs.Confirm("要删除全部历史记录吗？",
                    "所有课程里的录音、时间线上的小节总结都会一起删除（课程文件夹和已保存的文件保留），此操作无法撤销！", "删除"))
                return;
            try
            {
                currentPage = 1;
                await SQLiteHistoryLogger.ClearHistory();
                // Sections point at history ids, which restart after clearing.
                await SectionLogger.ClearSections();
                await LectureStore.ClearLectures();
                Summarizer.ResetCursor();
                Translator.ClearContexts();
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 删除失败。", ex.Message, SnackbarType.Error, timeout: 3, closeButton: true);
            }
            await LoadHistory();
        }

        private async void MaxRow_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (HistoryMaxRow.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int rows))
                return;
            maxRowPerPage = rows;
            // Shown only on the "全部句子" view; the others have nothing to load again.
            if (view == View.All)
                await LoadHistory();
        }

        private async void Refresh_Click(object? sender, RoutedEventArgs e)
        {
            switch (view)
            {
                case View.Courses:
                    await ShowCourses();
                    break;
                case View.Lectures when currentCourse != null:
                    await ShowLectures(currentCourse);
                    break;
                case View.Detail when currentLecture != null:
                    await ShowDetail(currentLecture);
                    break;
                default:
                    await LoadHistory();
                    break;
            }
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await Shell.PickSavePath("导出全部句子", $"exported_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv", "csv",
                new FilePickerFileType("CSV 表格（*.csv）") { Patterns = new[] { "*.csv" } });
            if (path == null)
                return;
            try
            {
                await SQLiteHistoryLogger.ExportToCSV(path);
                SnackbarHost.Show("保存成功。", $"文件已保存到：{path}", SnackbarType.Success);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("保存失败。", $"文件保存失败：{ex.Message}", SnackbarType.Error);
            }
        }

        private async Task SubmitSearch()
        {
            string searchText = HistorySearchBox.Text ?? "";

            // Clear search by emptying the box
            if (string.IsNullOrEmpty(searchText))
            {
                SearchText = string.Empty;
                currentPage = searchPage;
            }
            else // Submit search
            {
                if (string.IsNullOrEmpty(SearchText))
                    searchPage = currentPage;
                SearchText = searchText;
                currentPage = 1;
            }
            await LoadHistory();
        }

        private async Task LoadHistory()
        {
            try
            {
                var (history, pages) = await SQLiteHistoryLogger.LoadHistoryAsync(currentPage, maxRowPerPage, SearchText);
                maxPage = Math.Max(pages, 1);
                // The page shown no longer exists (rows were deleted, or there are more rows on a page now).
                if (currentPage > maxPage)
                {
                    currentPage = maxPage;
                    (history, _) = await SQLiteHistoryLogger.LoadHistoryAsync(currentPage, maxRowPerPage, SearchText);
                }
                HistoryList.ItemsSource = history;
                PageNumber.Text = $"{currentPage}/{maxPage}";
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取历史记录失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }
    }
}
