using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoArchive;
public partial class MainWindow : Window
{
    readonly MainViewModel viewModel = new();
    System.Diagnostics.TextWriterTraceListener? bindingListener;
    bool showingProgress, showingReview, reviewOpen;
    int reviewRequests;
    public MainWindow()
    {
        InitializeComponent(); DataContext = viewModel;
        viewModel.ReviewRequested += ShowReview;
        if (Environment.GetCommandLineArgs().Contains("--capture")) { Opacity = 0; ShowInTaskbar = false; ShowActivated = false; }
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.JobRunning))
            { if (viewModel.JobRunning && !showingProgress) ProgressCard.BringIntoView(); showingProgress = viewModel.JobRunning; }
            if (args.PropertyName == nameof(MainViewModel.HasReviewItems))
            {
                if (viewModel.HasReviewItems && !showingReview && viewModel.TaskPage) Dispatcher.BeginInvoke(() => ScanGrid.BringIntoView());
                showingReview = viewModel.HasReviewItems;
            }
        };
    }
    void ShowReview()
    {
        reviewRequests++;
        if (reviewOpen || Environment.GetCommandLineArgs().Contains("--capture") || !viewModel.CanMakeChoices) return;
        reviewOpen = true;
        try { new ReviewWindow(viewModel) { Owner = this }.ShowDialog(); }
        finally { reviewOpen = false; }
    }
    async void Window_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= Window_ContentRendered;
        var initialization = viewModel.InitializeAsync();
        if (viewModel.DetectingHardware && !viewModel.ChooseSourceCommand.CanExecute(null)) throw new InvalidOperationException("UI verification: hardware detection blocks source selection");
        if (!Environment.GetCommandLineArgs().Contains("--capture"))
            await Dispatcher.InvokeAsync(() => viewModel.ChooseSourceCommand.Execute(null), System.Windows.Threading.DispatcherPriority.Background);
        await initialization;
        // Used by the verification script: render the actual native WPF surface to an artifact.
        var arguments = Environment.GetCommandLineArgs(); var preview = Array.IndexOf(arguments, "--capture");
        if (preview >= 0 && preview + 1 < arguments.Length)
        {
            string? Argument(string name) { var index = Array.IndexOf(arguments, name); return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null; }
            bindingListener = new System.Diagnostics.TextWriterTraceListener(arguments[preview + 1] + ".bindings.txt");
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
            await viewModel.PrepareCaptureAsync(Argument("--sample-source"), Argument("--sample-output"), Argument("--page"), arguments.Contains("--advanced"));
            if (Argument("--wallpaper") is { } wallpaper) viewModel.SetWallpaper(wallpaper);
            if (arguments.Contains("--verify-archive")) await viewModel.VerifyArchiveAsync();
            if (arguments.Contains("--review-demo")) viewModel.PrepareReviewDemo();
            File.WriteAllText(arguments[preview + 1] + ".requests.txt", $"Automatic decision requests: {reviewRequests}\nPending files: {viewModel.PendingReviewCount}\nRun enabled: {viewModel.RunCommand.CanExecute(null)}");
            if (arguments.Contains("--verify-ui"))
            {
                await viewModel.VerifyUiAsync();
                File.WriteAllText(arguments[preview + 1] + ".checks.txt", "Folder selection during detection; cached startup; decision-dialog select-all, individual/mixed selection, colored actions, pending run block and choice persistence; wallpaper import, intensity, local copy after original removal, restore defaults and persistence; sibling output, automatic profile, all-page layout verified.\n" + viewModel.CapabilityCacheNote + $"\nAutomatic decision requests: {reviewRequests}");
            }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (arguments.Contains("--review-demo")) ScanGrid.BringIntoView();
            UpdateLayout(); var surface = (FrameworkElement)Content;
            var target = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
            using (var file = File.Create(arguments[preview + 1])) png.Save(file);
            if (Argument("--capture-review") is { } reviewPath)
            {
                var dialog = new ReviewWindow(viewModel) { Owner = this, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                dialog.Show(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
                if (arguments.Contains("--verify-ui"))
                {
                    await dialog.VerifyBindingsAsync();
                    viewModel.PrepareReviewDemo();
                    dialog.Model.Rows[0].ReviewSelected = true; dialog.Model.CopySelectedCommand.Execute(null);
                    dialog.Model.SelectPendingCommand.Execute(null);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
                    File.AppendAllText(arguments[preview + 1] + ".checks.txt", "\nNative dialog checkboxes, mixed state, both action bindings and completion button verified.");
                }
                var dialogSurface = (FrameworkElement)dialog.Content;
                var dialogImage = new RenderTargetBitmap((int)dialogSurface.ActualWidth, (int)dialogSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32); dialogImage.Render(dialogSurface);
                var dialogPng = new PngBitmapEncoder(); dialogPng.Frames.Add(BitmapFrame.Create(dialogImage));
                using (var file = File.Create(reviewPath)) dialogPng.Save(file);
                dialog.Close();
            }
            bindingListener.Flush(); Close();
        }
    }
    void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        bool valid = FolderDrop.TryRead(e.Data, out _, out var error);
        bool accepted = FolderDrop.SetEffect(e, valid && viewModel.FolderSelectionEnabled) != DragDropEffects.None;
        string hint = accepted ? "松开鼠标，选择该文件夹并开始扫描"
            : !viewModel.FolderSelectionEnabled ? "当前任务运行中，结束后可以拖入文件夹。"
            : valid ? "无法接收此次拖放。" : error;
        FolderDropHint.Text = hint;
        FolderDropBanner.Background = (Brush)new BrushConverter().ConvertFromString(accepted ? "#D8EDFA" : "#FFF1E2")!;
        if (!viewModel.TaskPage) GlobalStatus.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, hint);
    }
    void Window_DragLeave(object sender, DragEventArgs e)
    {
        FolderDropHint.Text = "从资源管理器拖入一个文件夹，松开后开始扫描";
        FolderDropBanner.Background = (Brush)new BrushConverter().ConvertFromString("#EDF5FD")!;
        GlobalStatus.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
    }
    async void Window_Drop(object sender, DragEventArgs e)
    {
        Window_DragLeave(sender, e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        bool valid = FolderDrop.TryRead(e.Data, out var folder, out var error);
        if (FolderDrop.SetEffect(e, valid && viewModel.FolderSelectionEnabled) == DragDropEffects.None)
        {
            viewModel.Status = !viewModel.FolderSelectionEnabled ? "当前任务运行中，结束后可以更换源文件夹。"
                : valid ? "无法接收此次拖放。" : error;
            return;
        }
        await viewModel.SelectSourceAndScanAsync(folder);
    }
    void Window_Closing(object? sender, CancelEventArgs e) => viewModel.RequestClose(e);
    void ShowFailures_Click(object sender, RoutedEventArgs e) => viewModel.Filter = "失败";
}
