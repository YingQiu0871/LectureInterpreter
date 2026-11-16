using System.Diagnostics;
using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using ComboBox = System.Windows.Controls.ComboBox;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace LiveCaptionsTranslator
{
    // Dialogs for naming a recording and choosing its course; the save flow itself is in LectureSaving.
    public static class LectureDialogs
    {
        // One dialog at a time. They share one place in the main window, and a second one would take it from the
        // first, whose answer would then never come: whatever waits for it (saving a class, for one) would never
        // finish. The second one waits its turn instead.
        private static readonly SemaphoreSlim oneAtATime = new(1, 1);

        private static ContentPresenter? Host => (Application.Current?.MainWindow as MainWindow)?.DialogHostContainer;

        // Shows `content` with a title and up to three buttons; null when there is no main window to show it in.
        private static async Task<ContentDialogResult?> Show(string title, object content, string primaryText,
            string closeText, ContentDialogButton defaultButton, string? secondaryText = null)
        {
            if (Host == null)
                return null;
            await oneAtATime.WaitAsync();
            try
            {
                // Looked up again: the wait can be long, and the window may be gone by now.
                var host = Host;
                if (host == null)
                    return null;
                var dialog = new ContentDialog
                {
                    Title = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Regular },
                    Content = content,
                    PrimaryButtonText = primaryText,
                    SecondaryButtonText = secondaryText ?? string.Empty,
                    CloseButtonText = closeText,
                    DefaultButton = defaultButton,
                    DialogHost = host,
                    Padding = new Thickness(8, 4, 8, 8),
                };
                host.Visibility = Visibility.Visible;
                try
                {
                    return await dialog.ShowAsync();
                }
                finally
                {
                    host.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                oneAtATime.Release();
            }
        }

        // Asks for the course and the name. Returns null if cancelled. With `discardText` there is a third button
        // whose answer has Discard set.
        public static async Task<NameAnswer?> AskName(string title, string course, string name,
            string primaryText, string closeText, string? discardText = null)
        {
            if (Host == null)
                return null;

            List<string> courses;
            try
            {
                courses = (await LectureStore.LoadCourses()).Select(c => c.Name).ToList();
            }
            catch (Exception)
            {
                courses = new List<string>();
            }

            var courseBox = new ComboBox
            {
                IsEditable = true,
                ItemsSource = courses,
                Text = course,
                MinWidth = 320,
            };
            var nameBox = new TextBox { Text = name, MinWidth = 320 };
            nameBox.Loaded += (s, e) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
            var panel = new StackPanel { MinWidth = 340 };
            panel.Children.Add(new TextBlock { Text = "本节课名称（也用作历史里的标题和文件名）", Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "放进哪个课程文件夹（可直接输入新课程）", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(courseBox);

            var result = await Show(title, panel, primaryText, closeText, ContentDialogButton.Primary, discardText);
            if (result == ContentDialogResult.Secondary)
                return new NameAnswer(course, name, true);
            if (result != ContentDialogResult.Primary)
                return null;

            string newName = nameBox.Text.Trim();
            return new NameAnswer(LectureStore.CleanCourseName(courseBox.Text), newName.Length == 0 ? name : newName);
        }

        // The default button is "取消": what is confirmed here usually cannot be undone.
        public static async Task<bool> Confirm(string title, string message, string primary)
        {
            var result = await Show(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                primary, "取消", ContentDialogButton.Close);
            return result == ContentDialogResult.Primary;
        }

        // Asks for one line of text. Returns it trimmed, or null if cancelled.
        public static async Task<string?> AskText(string title, string label, string text)
        {
            var box = new TextBox { Text = text, MinWidth = 320 };
            box.Loaded += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(box);
            var result = await Show(title, panel, "确定", "取消", ContentDialogButton.Primary);
            return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
        }

        public static void ShowInFolder(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                {
                    string folder = Directory.Exists(path) ? path : LectureDocument.SaveFolder;
                    Directory.CreateDirectory(folder);
                    Process.Start("explorer.exe", $"\"{folder}\"");
                }
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 打不开文件夹。", ex.Message, SnackbarType.Error, timeout: 2);
            }
        }
    }
}
