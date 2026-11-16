using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppHost.Start();
                desktop.MainWindow = new MainWindow();
                // The main window is the program. Whatever else is still open when it closes (the API settings)
                // goes with it, instead of keeping a program without its main window running.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.Exit += (s, e) => AppHost.Shutdown();
                // An error in one handler should not end a class that is being recorded: the program goes on.
                // Errors before the main window is up, or one after another, are left to end it.
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    if (ErrorLog.SurviveUiError(e.Exception) && desktop.MainWindow?.IsVisible == true)
                        e.Handled = true;
                    else
                        models.Setting.Flush();
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
