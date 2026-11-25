using System.Windows;
namespace VideoArchive;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var captureArgument = Array.IndexOf(e.Args, "--capture");
        if (captureArgument >= 0 && captureArgument + 1 < e.Args.Length && Environment.GetEnvironmentVariable("VIDEOARCHIVE_TEST_DATA") != null)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var elapsed = DateTime.Now - process.StartTime;
            System.IO.File.WriteAllText(e.Args[captureArgument + 1] + ".startup-ms.txt", elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try { System.IO.Directory.CreateDirectory(VideoArchive.Core.JsonStore.DataDirectory); System.IO.File.AppendAllText(System.IO.Path.Combine(VideoArchive.Core.JsonStore.DataDirectory, "errors.log"), $"{DateTime.Now:O}\n{args.Exception}\n"); } catch { }
            args.Handled = true;
            if (e.Args.Contains("--capture")) Shutdown(1);
            else MessageBox.Show(args.Exception.Message, "VideoArchive", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}
