using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using MenuItem = System.Windows.Controls.MenuItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : Page
    {
        public const int MIN_HEIGHT = 420;

        private enum View { Courses, Lectures, Detail, All }

        private View view = View.Courses;
        private CourseEntry? currentCourse = null;
        private LectureRecord? currentLecture = null;
        private readonly HashSet<long> selectedCourses = new();
        private readonly HashSet<long> selectedLectures = new();

        private int currentPage = 1;
        private int searchPage = 1;
        private int maxPage = 1;
        private int maxRowPerPage = 30;

        public string SearchText { get; set; } = string.Empty;

        public HistoryPage()
        {
            InitializeComponent();

            ScrollHelper.UseOwnScrollViewer(this, CourseScroll, LectureScroll, DetailScroll);
            Loaded += async (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
                // Taken off first: `Loaded` can come twice in a row.
                Translator.TranslationLogged -= OnTranslationLogged;
                Translator.TranslationLogged += OnTranslationLogged;
                await ShowCourses();
            };
            Unloaded += (s, e) =>
            {
                HistoryDataGrid.ItemsSource = null;
                Translator.TranslationLogged -= OnTranslationLogged;
            };

            HistoryMaxRow.SelectionChanged += maxRow_SelectionChanged;
        }

        // A new sentence shows up at the top of the first page. Other pages, and a page with rows selected, are
        // left alone: loading them again would drop the selection and move the list under the reader's eyes.
        private void OnTranslationLogged()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (view == View.All && currentPage == 1 && HistoryDataGrid.SelectedItems.Count == 0)
                    await LoadHistory();
            });
        }

        // ---- Navigation: courses > recordings of a course > one recording ----

        private void SetView(View newView)
        {
            view = newView;
            CourseScroll.Visibility = view == View.Courses ? Visibility.Visible : Visibility.Collapsed;
            LectureScroll.Visibility = view == View.Lectures ? Visibility.Visible : Visibility.Collapsed;
            DetailScroll.Visibility = view == View.Detail ? Visibility.Visible : Visibility.Collapsed;
            AllView.Visibility = view == View.All ? Visibility.Visible : Visibility.Collapsed;

            CrumbSep1.Visibility = view == View.Courses ? Visibility.Collapsed : Visibility.Visible;
            CrumbCourse.Visibility = CrumbSep1.Visibility;
            CrumbCourse.Content = view == View.All ? "全部句子" : currentCourse?.Name ?? string.Empty;
            CrumbSep2.Visibility = view == View.Detail ? Visibility.Visible : Visibility.Collapsed;
            CrumbLecture.Visibility = CrumbSep2.Visibility;
            CrumbLecture.Text = currentLecture?.Name ?? string.Empty;

            NewCourse.Visibility = view == View.Courses ? Visibility.Visible : Visibility.Collapsed;
            AllSentences.Visibility = NewCourse.Visibility;
            RenameLecture.Visibility = view == View.Detail ? Visibility.Visible : Visibility.Collapsed;
            OpenFile.Visibility = RenameLecture.Visibility;
            NotesDraft.Visibility = RenameLecture.Visibility;
            Export.Visibility = view == View.All ? Visibility.Visible : Visibility.Collapsed;
            Delete.Visibility = Export.Visibility;
            DeleteSelected.Visibility = view == View.Detail ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task ShowCourses()
        {
            selectedCourses.Clear();
            currentCourse = null;
            currentLecture = null;
            SetView(View.Courses);
            try
            {
                var courses = await LectureStore.LoadCourses();
                CourseList.ItemsSource = courses;
                CourseEmptyHint.Visibility = courses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取课程失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        private async Task ShowLectures(CourseEntry course)
        {
            selectedLectures.Clear();
            currentCourse = course;
            currentLecture = null;
            SetView(View.Lectures);
            try
            {
                var lectures = await LectureStore.LoadLectures(course.Id);
                LectureList.ItemsSource = lectures;
                LectureEmptyHint.Visibility = lectures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                DetailEmptyHint.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                DetailScroll.ScrollToTop();
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取本节课失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }

        private async void CrumbHome_click(object sender, RoutedEventArgs e) => await ShowCourses();

        private async void CrumbCourse_click(object sender, RoutedEventArgs e)
        {
            if (view == View.Detail && currentCourse != null)
                await ShowLectures(currentCourse);
        }

        private async void Course_click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CourseEntry course)
                await ShowLectures(course);
        }

        private async void Lecture_click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LectureRecord lecture)
                await ShowDetail(lecture);
        }

        private async void AllSentences_click(object sender, RoutedEventArgs e)
        {
            SetView(View.All);
            await LoadHistory();
        }

        private static T? MenuTarget<T>(object sender) where T : class =>
            (sender as MenuItem)?.DataContext as T;

        private void CourseChecked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CourseEntry course) selectedCourses.Add(course.Id);
        }
        private void CourseUnchecked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CourseEntry course) selectedCourses.Remove(course.Id);
        }
        private void LectureChecked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LectureRecord lecture) selectedLectures.Add(lecture.Id);
        }
        private void LectureUnchecked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LectureRecord lecture) selectedLectures.Remove(lecture.Id);
        }

        private async void DeleteSelected_click(object sender, RoutedEventArgs e)
        {
            if (ClassSession.IsRunning)
            {
                SnackbarHost.Show("正在录制时不能批量删除。", "请先点“停止”。", SnackbarType.Warning, timeout: 2);
                return;
            }
            try
            {
                if (view == View.All)
                {
                    var ids = HistoryDataGrid.SelectedItems.Cast<TranslationHistoryEntry>().Select(row => row.Id).ToArray();
                    if (ids.Length == 0 || !await LectureDialogs.Confirm($"删除选中的 {ids.Length} 条句子？",
                            "只删除所选历史句子，不删除整节课或已保存文件。", "删除")) return;
                    await SQLiteHistoryLogger.DeleteRows(ids);
                    await LoadHistory();
                    return;
                }

                var lectures = new List<LectureRecord>();
                var courses = new List<CourseEntry>();
                if (view == View.Courses)
                {
                    courses = (CourseList.ItemsSource as IEnumerable<CourseEntry>)?.Where(c => selectedCourses.Contains(c.Id)).ToList() ?? new();
                    foreach (var course in courses)
                        lectures.AddRange(await LectureStore.LoadLectures(course.Id));
                }
                else if (view == View.Lectures)
                    lectures = (LectureList.ItemsSource as IEnumerable<LectureRecord>)?.Where(l => selectedLectures.Contains(l.Id)).ToList() ?? new();

                if (lectures.Count == 0 && courses.Count == 0) return;
                if (!await LectureDialogs.Confirm($"删除选中的 {courses.Count} 门课程、{lectures.Count} 节课？",
                        "原文、译文、小节总结及对应的已保存转录和笔记文件将删除。此操作无法撤销。", "删除")) return;

                try
                {
                    int filesLeft = 0;
                    foreach (var lecture in lectures)
                    {
                        await LectureStore.DeleteLecture(lecture);
                        filesLeft += DeleteSavedFiles(lecture);
                    }
                    foreach (var course in courses)
                    {
                        await LectureStore.DeleteCourse(course.Id);
                        if (Translator.Setting.Lecture.CurrentCourse == course.Name)
                            Translator.Setting.Lecture.CurrentCourse = string.Empty;
                    }
                    if (filesLeft > 0)
                        SnackbarHost.Show("部分文件仍保留。", $"{filesLeft} 个文件不在当前保存目录或无法删除，请手动检查。", SnackbarType.Warning, timeout: 5);
                }
                finally
                {
                    // Also after a failure half way: the list shows what is still there.
                    if (view == View.Courses) await ShowCourses();
                    else if (view == View.Lectures && currentCourse != null) await ShowLectures(currentCourse);
                }
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("批量删除失败。", ex.Message, SnackbarType.Error, timeout: 5, closeButton: true);
            }
        }

        // Removes the saved transcript of a deleted lecture and its notes draft. Only files inside the save folder
        // are touched. Returns how many files were left where they are.
        private static int DeleteSavedFiles(LectureRecord lecture)
        {
            if (string.IsNullOrWhiteSpace(lecture.FilePath))
                return 0;
            int left = 0;
            try
            {
                string root = Path.GetFullPath(LectureDocument.SaveFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(lecture.FilePath);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return 1;
                foreach (string file in new[] { path, LectureNotes.DraftPath(lecture) })
                {
                    try
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        left++;
                    }
                }
            }
            catch (Exception)
            {
                // A recorded path that cannot even be read as one: whatever is there stays.
                left++;
            }
            return left;
        }

        private async void NewCourse_click(object sender, RoutedEventArgs e)
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

        private async void RenameCourse_click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget<CourseEntry>(sender) is not CourseEntry course)
                return;
            string? name = await LectureDialogs.AskText("重命名课程", "课程名称", course.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == course.Name)
                return;
            try
            {
                await LectureStore.RenameCourse(course.Id, name);
            }
            catch (Exception)
            {
                // The course keeps its name, and so does the course new classes go to.
                SnackbarHost.Show("已经有同名的课程了。", "", SnackbarType.Warning, timeout: 2);
                return;
            }
            if (Translator.Setting.Lecture.CurrentCourse == course.Name)
                Translator.Setting.Lecture.CurrentCourse = name.Trim();
            await ShowCourses();
        }

        private async void DeleteCourse_click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget<CourseEntry>(sender) is not CourseEntry course)
                return;
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

        private async void RenameLecture_click(object sender, RoutedEventArgs e)
        {
            if (currentLecture != null)
                await RenameLectureAndRefresh(currentLecture);
        }

        private async void RenameLectureMenu_click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget<LectureRecord>(sender) is LectureRecord lecture)
                await RenameLectureAndRefresh(lecture);
        }

        private async void DeleteLecture_click(object sender, RoutedEventArgs e)
        {
            if (MenuTarget<LectureRecord>(sender) is not LectureRecord lecture)
                return;
            if (ClassSession.IsRunning && ClassSession.CurrentLectureId == lecture.Id)
            {
                SnackbarHost.Show("这节课正在录制。", "请先点“停止”。", SnackbarType.Warning, timeout: 2);
                return;
            }
            if (!await LectureDialogs.Confirm($"要删除“{lecture.Name}”吗？", "这节课的原文、译文和小节总结都会删除，已保存的文件不受影响。此操作无法撤销！", "删除"))
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

        private async void OpenFile_click(object sender, RoutedEventArgs e)
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

        private async void NotesDraft_click(object sender, RoutedEventArgs e)
        {
            if (currentLecture == null)
                return;
            NotesDraft.IsEnabled = false;
            try
            {
                var lecture = await LectureStore.GetLecture(currentLecture.Id) ?? currentLecture;
                // The draft goes next to the transcript file, so make sure that exists.
                if (string.IsNullOrEmpty(lecture.FilePath) || !System.IO.File.Exists(lecture.FilePath))
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

        private async void PageDown_click(object sender, RoutedEventArgs e)
        {
            if (currentPage - 1 >= 1)
                currentPage--;
            await LoadHistory();
        }

        private async void PageUp_click(object sender, RoutedEventArgs e)
        {
            if (currentPage < maxPage)
                currentPage++;
            await LoadHistory();
        }

        private async void Delete_click(object sender, RoutedEventArgs e)
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

        private async void maxRow_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || (e.AddedItems[0] as ComboBoxItem)?.Tag is not string tag ||
                !int.TryParse(tag, out int rows))
                return;
            maxRowPerPage = rows;
            await LoadHistory();
        }

        private async void Refresh_click(object sender, RoutedEventArgs e)
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

        private async void Export_click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = $"exported_{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                RestoreDirectory = true,
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await SQLiteHistoryLogger.ExportToCSV(saveFileDialog.FileName);
                    SnackbarHost.Show("保存成功。", $"文件已保存到：{saveFileDialog.FileName}", SnackbarType.Success);
                }
                catch (Exception ex)
                {
                    SnackbarHost.Show("保存失败。", $"文件保存失败：{ex.Message}", SnackbarType.Error);
                }
            }
        }

        private async void HistorySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string searchText = (sender as AutoSuggestBox)?.Text ?? "";

            // Clear search by Ctrl+A and Delete and Enter
            if (string.IsNullOrEmpty(searchText))
            {
                SearchText = string.Empty;
                currentPage = searchPage;
            }
            else // Submit search
            {
                if (string.IsNullOrEmpty(SearchText))
                {
                    searchPage = currentPage;
                }
                SearchText = (sender as AutoSuggestBox)?.Text;
                currentPage = 1;
            }
            await LoadHistory();
        }

        private async void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Press X to clear search box
            if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            {
                if (!string.IsNullOrEmpty(SearchText))
                {
                    SearchText = string.Empty;
                    currentPage = searchPage;
                    await LoadHistory();
                }
            }
        }

        public async Task LoadHistory()
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
                HistoryDataGrid.ItemsSource = history;
                PageNumber.Text = $"{currentPage}/{maxPage}";
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取历史记录失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
            }
        }
    }
}
