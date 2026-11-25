using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoArchive;

internal enum PickerPurpose { Source, Output, Wallpaper, Engine, Csv, Json }

public partial class PathPickerWindow : Window
{
    const string Computer = "此电脑";
    const string DropInstruction = "从资源管理器拖入一个文件夹，松开后开始扫描";
    readonly ExplorerResultsHost browser;
    readonly PickerPurpose purpose;
    readonly string? initialPath;
    readonly List<string?> back = [];
    CancellationTokenSource? navigation;
    string? currentPath, selectedPath;
    bool loaded, loading, closed;
    string? ReportExtension => purpose == PickerPurpose.Csv ? ".csv" : purpose == PickerPurpose.Json ? ".json" : null;
    bool FolderMode => purpose is PickerPurpose.Source or PickerPurpose.Output;
    bool SaveMode => ReportExtension != null;

    public string? SelectedResult { get; private set; }

    internal PathPickerWindow(PickerPurpose purpose, string title, string? initialPath, string? suggestedName = null)
    {
        this.purpose = purpose;
        this.initialPath = initialPath;
        InitializeComponent();
        Title = Heading.Text = title;
        Confirm.Content = FolderMode ? "使用此文件夹" : SaveMode ? "保存报告" : "选择此文件";
        ContentsHint.Text = purpose switch
        {
            PickerPurpose.Wallpaper => "图片 · PNG / JPG / BMP / GIF",
            PickerPurpose.Engine => "可执行文件 · EXE",
            PickerPurpose.Csv => "CSV 报告 · .csv",
            PickerPurpose.Json => "JSON 报告 · .json",
            _ => "双击子文件夹可进入"
        };
        if (purpose == PickerPurpose.Source) SourceDropZone.Visibility = Visibility.Visible;
        if (purpose == PickerPurpose.Output || SaveMode) NewFolder.Visibility = Visibility.Visible;
        if (SaveMode)
        {
            SaveNameRow.Visibility = Visibility.Visible;
            SaveName.Text = suggestedName + ReportExtension;
        }
        browser = new();
        BrowserFrame.Child = browser;
        browser.ItemActivated += ActivateItem;
        browser.SelectionChanged += ReadSelection;
        browser.CancelRequested += Close;
        Loaded += OpenInitialFolder;
        Closing += (_, _) => browser.ReleaseBrowser();
        Closed += (_, _) =>
        {
            closed = true;
            navigation?.Cancel();
            browser.Dispose();
        };
    }

    async void OpenInitialFolder(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (closed) return;
        browser.InitializeBrowser();
        string? initial = await Task.Run(() =>
        {
            if (Directory.Exists(initialPath)) return initialPath;
            return File.Exists(initialPath) ? Path.GetDirectoryName(initialPath) : null;
        });
        if (!closed && navigation == null && currentPath == null) await NavigateAsync(initial, remember: false);
    }

    async Task NavigateAsync(string? target, bool remember = true)
    {
        if (!browser.Ready)
        {
            Feedback.Text = browser.InitializationError ?? "正在打开 Windows 缩略图浏览区…";
            return;
        }
        navigation?.Cancel();
        using var request = new CancellationTokenSource();
        navigation = request;
        var token = request.Token;
        loading = true;
        Feedback.Text = "正在读取此位置…";
        UpdateButtons();
        try
        {
            target = target == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            var entries = await Task.Run(() => ReadEntries(target, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            browser.Clear();
            browser.ApplyThumbnailView();
            if (remember && !string.Equals(currentPath, target, StringComparison.OrdinalIgnoreCase)) back.Add(currentPath);
            currentPath = target;
            selectedPath = null;
            Address.Text = target ?? Computer;
            NewFolderRow.Visibility = Visibility.Collapsed;
            foreach (var batch in entries.Chunk(64))
            {
                token.ThrowIfCancellationRequested();
                foreach (string entry in batch) browser.Add(entry);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            token.ThrowIfCancellationRequested();
            Feedback.Text = entries.Length == 0 ? "此位置没有可显示的项目。"
                : "Windows 能预览的图片和视频会显示缩略图。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or COMException)
        {
            Feedback.Text = "无法浏览此位置：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(navigation, request))
            {
                navigation = null;
                loading = false;
                if (!closed) UpdateButtons();
            }
        }
    }

    string[] ReadEntries(string? folder, CancellationToken token)
    {
        if (folder == null) return DriveInfo.GetDrives().Select(drive => drive.Name).ToArray();
        var entries = new List<string>();
        var directory = new DirectoryInfo(folder);
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            var attributes = item.Attributes;
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0 || MatchesFilter(item.FullName)) entries.Add(item.FullName);
        }
        return entries.ToArray();
    }

    bool MatchesFilter(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return purpose switch
        {
            PickerPurpose.Wallpaper => extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif",
            PickerPurpose.Engine => extension == ".exe",
            _ => true
        };
    }

    void UpdateButtons()
    {
        if (!loaded) return;
        Back.IsEnabled = !loading && back.Count > 0;
        Up.IsEnabled = !loading && currentPath != null;
        NewFolder.IsEnabled = !loading && currentPath != null;
        Confirm.IsEnabled = !loading && (FolderMode ? currentPath != null
            : SaveMode ? currentPath != null && !string.IsNullOrWhiteSpace(SaveName.Text) : selectedPath != null);
    }

    void ReadSelection()
    {
        if (closed || loading) return;
        selectedPath = browser.SelectedPath();
        if (selectedPath != null && File.Exists(selectedPath))
        {
            if (SaveMode && string.Equals(Path.GetExtension(selectedPath), ReportExtension, StringComparison.OrdinalIgnoreCase))
                SaveName.Text = Path.GetFileName(selectedPath);
        }
        else selectedPath = null;
        UpdateButtons();
    }

    async void ActivateItem(string path)
    {
        if (closed || loading) return;
        if (Directory.Exists(path)) { await NavigateAsync(path); return; }
        if (FolderMode || !File.Exists(path)) return;
        if (SaveMode)
        {
            if (string.Equals(Path.GetExtension(path), ReportExtension, StringComparison.OrdinalIgnoreCase))
                SaveName.Text = Path.GetFileName(path);
            return;
        }
        AcceptFile(path);
    }

    async void BackClick(object sender, RoutedEventArgs e)
    {
        if (back.Count == 0) return;
        var previous = back[^1];
        back.RemoveAt(back.Count - 1);
        await NavigateAsync(previous, remember: false);
    }

    async void UpClick(object sender, RoutedEventArgs e)
    {
        if (currentPath != null) await NavigateAsync(Path.GetDirectoryName(currentPath));
    }

    async void DrivesClick(object sender, RoutedEventArgs e) => await NavigateAsync(null);
    async void RefreshClick(object sender, RoutedEventArgs e) => await NavigateAsync(currentPath, remember: false);
    async void PlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<Environment.SpecialFolder>(name, out var place))
        {
            string path = Environment.GetFolderPath(place);
            if (path.Length > 0) await NavigateAsync(path);
            else Feedback.Text = "此常用位置不可用。";
        }
    }

    async void AddressClick(object sender, RoutedEventArgs e) => await GoToAddress();
    async void AddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GoToAddress();
    }
    Task GoToAddress()
    {
        string input = Address.Text.Trim().Trim('"');
        if (input == Computer) return NavigateAsync(null);
        if (Path.IsPathFullyQualified(input)) return NavigateAsync(input);
        Feedback.Text = "请输入完整的文件夹地址，例如 D:\\素材。";
        return Task.CompletedTask;
    }

    void SourceDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        bool valid = FolderDrop.TryRead(e.Data, out _, out var error);
        bool accepted = FolderDrop.SetEffect(e, valid) != DragDropEffects.None;
        SourceDropHint.Text = accepted ? "松开鼠标，选择该文件夹并开始扫描" : valid ? DropInstruction : error;
        SourceDropZone.Background = Brush(accepted ? "#D8EDFA" : "#FFF1E2");
    }
    void SourceDragLeave(object sender, DragEventArgs e)
    {
        SourceDropHint.Text = DropInstruction;
        SourceDropZone.Background = Brush("#EDF5FD");
    }
    void SourceDrop(object sender, DragEventArgs e)
    {
        SourceDragLeave(sender, e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        bool valid = FolderDrop.TryRead(e.Data, out var folder, out var error);
        if (FolderDrop.SetEffect(e, valid) == DragDropEffects.None) { Feedback.Text = valid ? "无法接收此次拖放。" : error; return; }
        Finish(folder);
    }

    void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (FolderMode && currentPath != null)
        {
            if (Directory.Exists(currentPath)) Finish(currentPath);
            else Feedback.Text = "此文件夹已不存在，请重新选择。";
        }
        else if (SaveMode && currentPath != null) AcceptSave(currentPath);
        else if (selectedPath != null) AcceptFile(selectedPath);
    }

    void AcceptFile(string path)
    {
        if (File.Exists(path) && MatchesFilter(path)) Finish(path);
        else Feedback.Text = "文件不可用或类型不匹配。";
    }

    void AcceptSave(string folder)
    {
        var name = SaveName.Text.Trim();
        if (!IsValidName(name)) { Feedback.Text = "请输入有效的文件名。"; return; }
        if (Path.GetExtension(name).Length == 0) name += ReportExtension;
        else if (!string.Equals(Path.GetExtension(name), ReportExtension, StringComparison.OrdinalIgnoreCase))
        {
            Feedback.Text = "报告文件名必须以 " + ReportExtension + " 结尾。";
            return;
        }
        if (!Directory.Exists(folder)) { Feedback.Text = "此文件夹已不存在，请重新选择。"; return; }
        string target = Path.Combine(folder, name);
        if (Directory.Exists(target)) { Feedback.Text = "已存在同名文件夹。"; return; }
        if (File.Exists(target) && MessageBox.Show(this, "此位置已有同名报告。是否覆盖？", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Finish(target);
    }

    void Finish(string path)
    {
        SelectedResult = path;
        DialogResult = true;
    }

    void SaveNameChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) UpdateButtons();
    }
    void NewFolderClick(object sender, RoutedEventArgs e)
    {
        NewFolderRow.Visibility = Visibility.Visible;
        NewFolderName.Text = "";
        NewFolderName.Focus();
    }
    void CancelNewFolder(object sender, RoutedEventArgs e) => NewFolderRow.Visibility = Visibility.Collapsed;
    void NewFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CreateFolder(sender, e);
    }
    async void CreateFolder(object sender, RoutedEventArgs e)
    {
        if (currentPath == null || loading) return;
        string name = NewFolderName.Text.Trim();
        if (!IsValidName(name)) { Feedback.Text = "请输入有效的文件夹名称。"; return; }
        string path = Path.Combine(currentPath, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) { Feedback.Text = "此位置已有同名项目。"; return; }
            Directory.CreateDirectory(path);
            await NavigateAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = "无法创建文件夹：" + ex.Message; }
    }

    static bool IsValidName(string name)
    {
        if (name.Length == 0 || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string firstPart = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        if (firstPart is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return false;
        return !(firstPart.Length == 4 && firstPart[..3] is "COM" or "LPT" && "123456789¹²³".Contains(firstPart[3]));
    }
    static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
