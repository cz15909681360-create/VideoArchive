using System.Windows;
namespace VideoArchive;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try { System.IO.Directory.CreateDirectory(VideoArchive.Core.JsonStore.DataDirectory); System.IO.File.AppendAllText(System.IO.Path.Combine(VideoArchive.Core.JsonStore.DataDirectory, "errors.log"), $"{DateTime.Now:O}\n{args.Exception}\n"); } catch { }
            args.Handled = true;
            if (e.Args.Contains("--capture")) Shutdown(1);
            else MessageBox.Show(args.Exception.Message, "VideoArchive", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}
