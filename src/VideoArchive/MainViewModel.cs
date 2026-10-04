using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoArchive.Core;

namespace VideoArchive;

public sealed class RelayCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class MainViewModel : Observable
{
    readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    readonly DispatcherTimer replanTimer;
    readonly string dataDirectory = JsonStore.DataDirectory;
    readonly DispatcherTimer clockTimer;
    AppConfiguration configuration = new();
    ScanService? scanner; ArchiveService? archive;
    CancellationTokenSource? cancellation;
    ArchiveJob? job;
    List<Capability> capabilities = [];
    EncodeSettings settings = new();
    Preset? selectedPreset; ArchiveJob? selectedHistory; PlanItem? selectedItem;
    string sourceRoot = "", outputRoot = "", status = "正在准备编码器检测…", ffmpegPath = "", ffprobePath = "", presetName = "我的备份方案", page = "压缩任务", filter = "全部";
    bool busy, initialized, paused, advanced, refreshingOptions, otherOutput, detectingHardware;
    string capabilityCacheNote = "正在检查硬件环境，可先选择源文件夹";
    BitmapSource? wallpaperImage;
    public event Action? ReviewRequested;
    string outputParent = "";
    DateTime runStarted;
    public MainViewModel()
    {
        replanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        replanTimer.Tick += (_, _) => { replanTimer.Stop(); Replan(); };
        clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (_, _) => UpdateProgress();
        settings.PropertyChanged += SettingsChanged;
        NavigateCommand = new RelayCommand(p => Page = p?.ToString() ?? "压缩任务");
        ChooseSourceCommand = new RelayCommand(_ => PickFolder(true), () => FolderSelectionEnabled);
        ChooseOutputCommand = new RelayCommand(_ => PickFolder(false), () => FolderSelectionEnabled);
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), () => !Busy && initialized);
        RunCommand = new RelayCommand(async _ => await RunAsync(), () => !Busy && job != null && initialized && job.PlanVersion == MediaPolicy.PlanVersion && !job.Items.Any(i => i.RequiresReview));
        SelectReviewCommand = new RelayCommand(_ => { foreach (var i in Items.Where(i => i.CanReview && i.RequiresReview)) i.ReviewSelected = true; }, () => CanMakeChoices);
        ClearReviewCommand = new RelayCommand(_ => { foreach (var i in Items) i.ReviewSelected = false; }, () => CanMakeChoices);
        CopyReviewCommand = new RelayCommand(p => ReviewTimecodes(p, "原样备份"), () => CanMakeChoices);
        DropReviewCommand = new RelayCommand(p => ReviewTimecodes(p, "舍弃时间码"), () => CanMakeChoices);
        OpenReviewCommand = new RelayCommand(_ => ReviewRequested?.Invoke(), () => CanMakeChoices);
        ChooseWallpaperCommand = new RelayCommand(_ => ChooseWallpaper(), () => !Busy);
        ResetWallpaperCommand = new RelayCommand(_ => { configuration.Appearance = new(); LoadWallpaper(); Raise(nameof(WallpaperOpacity)); SaveConfiguration(); Status = "已恢复默认山海主题背景"; }, () => !Busy);
        PauseCommand = new RelayCommand(_ => { if (archive == null) return; if (Paused) archive.Continue(); else archive.Pause(); Paused = archive.Paused; }, () => Busy && archive != null && job?.State == "执行中");
        CancelCommand = new RelayCommand(_ => { cancellation?.Cancel(); Status = "正在停止并清理临时文件…"; }, () => Busy);
        RetryCommand = new RelayCommand(async _ => { if (job == null) return; foreach (var p in job.Items.Where(p => p.Status == "失败")) p.Status = "待处理"; await RunAsync(); }, () => !Busy && job?.Items.Any(i => i.Status == "失败") == true);
        SavePresetCommand = new RelayCommand(_ => SavePreset(false), () => !Busy);
        DuplicatePresetCommand = new RelayCommand(_ => SavePreset(true), () => !Busy);
        RenamePresetCommand = new RelayCommand(_ => { if (SelectedPreset == null) return; SelectedPreset.Name = RequireName(); SaveConfiguration(); Raise(nameof(Presets)); RefreshPresetList(); }, () => !Busy && SelectedPreset != null);
        DeletePresetCommand = new RelayCommand(_ => { if (SelectedPreset == null) return; configuration.Presets.Remove(SelectedPreset); if (configuration.DefaultPresetId == SelectedPreset.Id) configuration.DefaultPresetId = configuration.Presets.FirstOrDefault()?.Id ?? ""; RefreshPresetList(); SaveConfiguration(); }, () => !Busy && SelectedPreset != null && Presets.Count > 1);
        DefaultPresetCommand = new RelayCommand(_ => { if (SelectedPreset == null) return; configuration.DefaultPresetId = SelectedPreset.Id; SaveConfiguration(); Raise(nameof(DefaultPresetText)); Status = "已设为默认方案"; }, () => !Busy && SelectedPreset != null);
        LoadHistoryCommand = new RelayCommand(_ => LoadHistory(), () => !Busy && SelectedHistory != null);
        OpenOutputCommand = new RelayCommand(_ => { if (Directory.Exists(OutputRoot)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { OutputRoot } }); }, () => Directory.Exists(OutputRoot));
        ExportCsvCommand = new RelayCommand(_ => Export(false), () => job != null && !Busy);
        ExportJsonCommand = new RelayCommand(_ => Export(true), () => job != null && !Busy);
        ShowLogCommand = new RelayCommand(_ => ShowLog(), () => SelectedItem != null);
        ApplyToolsCommand = new RelayCommand(async _ => { configuration.FfmpegPath = FfmpegPath; configuration.FfprobePath = FfprobePath; SaveConfiguration(); await InitializeAsync(false, true); }, () => !Busy);
        BrowseFfmpegCommand = new RelayCommand(_ => PickTool(true), () => !Busy);
        BrowseFfprobeCommand = new RelayCommand(_ => PickTool(false), () => !Busy);
        AdvancedCommand = new RelayCommand(_ => Advanced = !Advanced);
        NewTaskCommand = new RelayCommand(_ => { job = null; Items.Clear(); Filter = "全部"; RaiseJob(); Status = "选择源目录和输出目录后扫描"; Page = "压缩任务"; }, () => !Busy);
    }
    public ObservableCollection<Preset> Presets { get; } = [];
    public ObservableCollection<PlanItem> Items { get; } = [];
    public ObservableCollection<ArchiveJob> History { get; } = [];
    public ObservableCollection<string> BackendOptions { get; } = ["CPU"];
    public ObservableCollection<string> CodecOptions { get; } = ["H.264", "HEVC", "AV1"];
    public ObservableCollection<string> RateOptions { get; } = ["VBR", "CBR", "恒定质量"];
    public ObservableCollection<string> DepthOptions { get; } = ["随源", "8-bit", "10-bit"];
    public ObservableCollection<string> ProfileOptions { get; } = ["自动", "main", "main10"];
    public ObservableCollection<string> EncoderPresets { get; } = ["自动"];
    public double[] QualityOptions { get; } = [.75, 1, 1.25, 1.5];
    public string[] BitrateOptions { get; } = ["智能推荐", "固定上限"];
    public string[] AudioOptions { get; } = ["原样保留", "AAC转码"];
    public string[] CollisionOptions { get; } = ["跳过", "覆盖", "自动重命名"];
    public string[] FilterOptions { get; } = ["全部", "视频", "待决定", "时间码选择", "待压缩", "原样复制", "失败", "未处理"];
    public ICollectionView ItemView => CollectionViewSource.GetDefaultView(Items);
    public EncodeSettings Settings { get => settings; private set { settings.PropertyChanged -= SettingsChanged; settings = value; settings.PropertyChanged += SettingsChanged; Raise(); Raise(nameof(ParameterSummary)); Raise(nameof(FixedBitrateEnabled)); RefreshOptions(); } }
    public Preset? SelectedPreset { get => selectedPreset; set { if (!Set(ref selectedPreset, value) || value == null) return; Settings = value.Settings.Snapshot(); PresetName = value.Name; ScheduleReplan(); Raise(nameof(DefaultPresetText)); } }
    public ArchiveJob? SelectedHistory { get => selectedHistory; set => Set(ref selectedHistory, value); }
    public PlanItem? SelectedItem { get => selectedItem; set => Set(ref selectedItem, value); }
    public string SourceRoot { get => sourceRoot; set { if (Set(ref sourceRoot, value)) { UpdateOutputRoot(); InvalidateScan(); } } }
    public string OutputRoot { get => outputRoot; private set { if (Set(ref outputRoot, value)) { Raise(nameof(OutputFolderName)); InvalidateScan(); } } }
    public string OutputFolderName => Path.GetFileName(OutputRoot);
    public bool OtherOutput { get => otherOutput; set { if (Set(ref otherOutput, value)) UpdateOutputRoot(); } }
    public string OutputParent { get => outputParent; set { if (Set(ref outputParent, value)) UpdateOutputRoot(); } }
    void UpdateOutputRoot()
    {
        try { OutputRoot = string.IsNullOrWhiteSpace(SourceRoot) || OtherOutput && string.IsNullOrWhiteSpace(OutputParent) ? "" : Files.BackupOutput(SourceRoot, OtherOutput ? OutputParent : null); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException) { OutputRoot = ""; }
    }
    public string Status { get => status; set => Set(ref status, value); }
    public string FfmpegPath { get => ffmpegPath; set => Set(ref ffmpegPath, value); }
    public string FfprobePath { get => ffprobePath; set => Set(ref ffprobePath, value); }
    public string PresetName { get => presetName; set => Set(ref presetName, value); }
    public string Page { get => page; set { Set(ref page, value); Raise(nameof(TaskPage)); Raise(nameof(PresetPage)); Raise(nameof(HistoryPage)); Raise(nameof(SettingsPage)); } }
    public bool TaskPage => Page == "压缩任务"; public bool PresetPage => Page == "压缩方案"; public bool HistoryPage => Page == "任务历史"; public bool SettingsPage => Page == "设置";
    public string Filter { get => filter; set { Set(ref filter, value); ApplyFilter(); } }
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(Idle)); Raise(nameof(FolderSelectionEnabled)); Raise(nameof(CanMakeChoices)); CommandManager.InvalidateRequerySuggested(); } }
    public bool DetectingHardware { get => detectingHardware; private set { Set(ref detectingHardware, value); Raise(nameof(FolderSelectionEnabled)); CommandManager.InvalidateRequerySuggested(); } }
    public bool FolderSelectionEnabled => !Busy || DetectingHardware;
    public string CapabilityCacheNote { get => capabilityCacheNote; private set => Set(ref capabilityCacheNote, value); }
    public bool HasReviewItems => job?.Items.Any(i => i.CanReview) == true;
    public bool CanMakeChoices => !Busy && job?.State == "已扫描" && HasReviewItems;
    public int PendingReviewCount => job?.Items.Count(i => i.RequiresReview) ?? 0;
    public string ReviewAlert => PendingReviewCount > 0 ? $"{PendingReviewCount} 个文件等待你决定如何备份" : "特殊文件的处理选择已保存，可重新查看与调整";
    public BitmapSource? WallpaperImage { get => wallpaperImage; private set { Set(ref wallpaperImage, value); Raise(nameof(HasWallpaper)); Raise(nameof(WallpaperDescription)); } }
    public bool HasWallpaper => WallpaperImage != null;
    public string WallpaperDescription => HasWallpaper ? "自定义壁纸已保存到本机，下次启动自动恢复" : "山海 · 默认几何背景，可选择你自己的图片";
    public double WallpaperOpacity { get => configuration.Appearance.WallpaperOpacity; set { if (!double.IsFinite(value)) return; configuration.Appearance.WallpaperOpacity = Math.Clamp(value, .05, 1); Raise(); SaveConfiguration(); } }
    public string ReviewSummary => $"时间码待决定：{job?.Items.Count(i => i.RequiresReview) ?? 0} 个。可按行选择，或勾选后批量处理。舍弃时间码会移除 tmcd 及 timecode 标签，音轨、字幕仍保留；其他保护规则继续生效。";
    public bool Idle => !Busy;
    public bool JobRunning => Busy && job?.State == "执行中";
    public bool Paused { get => paused; private set { Set(ref paused, value); Raise(nameof(PauseLabel)); } }
    public bool Advanced { get => advanced; set => Set(ref advanced, value); }
    public string PauseLabel => Paused ? "继续队列" : "暂停队列";
    public string ParameterSummary => Settings.Summary;
    public string DefaultPresetText => SelectedPreset?.Id == configuration.DefaultPresetId ? "★ 默认方案" : "可保存当前参数或设为默认";
    public string CapabilityText => capabilities.Count == 0 ? "正在检测…" : string.Join("\n", capabilities.Select(c => $"{c.Backend,-7} {c.Codec,-6}  {(c.Available ? "可用" + (c.TenBit ? " · 8/10-bit" : " · 8-bit") : "不可用 · " + c.Error.Split('\n')[0])}"));
    public string ScanSummary => job == null ? "尚未扫描 · 不会修改源文件" : $"{job.Items.Count(i => i.VideoCandidate || i.Media != null)} 个视频 · {job.Items.Count(i => !i.VideoCandidate && i.Media == null && i.Action != "未处理")} 个配套文件 · {job.Directories.Count} 个文件夹 · 原始 {Format.Bytes(job.SourceBytes)} · 时长 {Format.Time(job.Items.Sum(i => i.Media?.Duration ?? 0))}";
    public string EstimateSummary => job == null ? "扫描后显示预计容量与处理原因" : $"预计新增输出 {Format.Bytes(job.Items.Sum(i => i.EstimatedSize))} · 待压缩 {job.Items.Count(i => i.Action == "压缩")} · 复制 {job.Items.Count(i => i.Action == "复制")} · 待决定 {job.Items.Count(i => i.RequiresReview)} · 跳过 {job.Items.Count(i => i.Action == "跳过")} · 未处理 {job.Items.Count(i => i.Action == "未处理")}（容量为估计）";
    public string Distribution => job == null ? "" : string.Join("   |   ", job.Items.Where(i => i.Media != null).GroupBy(i => $"{i.Media!.Width}×{i.Media.Height} / {i.Media.Codec}").OrderByDescending(g => g.Count()).Take(7).Select(g => $"{g.Key}：{g.Count()}"));
    public string Notices => job == null ? "" : string.Join("\n", job.Notices);
    public string Report => job == null ? "完成后在此显示实际结果" : ReportService.Summary(job);
    public double OverallProgress => job == null || job.Items.Count == 0 ? 0 : job.Items.Average(i => i.Action == "未处理" || i.Status is "完成" or "已跳过" or "失败" ? 100 : i.Progress);
    public string ProgressText => job == null ? "等待扫描" : $"{job.Items.Count(i => i.Status is "完成" or "已跳过" or "未处理")}/{job.Items.Count} 个文件 · {OverallProgress:0.#}%";
    public string CurrentFile => job == null ? "" : string.Join("\n", job.Items.Where(i => i.Status is "压缩中" or "复制中" or "验证中").Select(i => $"{i.RelativePath} · {i.Progress:0.#}% · {i.Detail}"));
    public string Eta => !Busy || job == null || OverallProgress <= 0 ? "剩余时间：—" : $"预计剩余 {Format.Time((DateTime.Now - runStarted).TotalSeconds * (100 - OverallProgress) / OverallProgress)} · 已验证输出 {Format.Bytes(job.OutputBytes)}";
    public bool PeakLimitSupported => !(Settings.Backend == "CPU" && Settings.Codec == "AV1" && Settings.RateMode == "VBR");
    public bool FixedBitrateEnabled => Settings.BitrateMode == "固定上限";
    public string RateNote => Settings.RateMode == "恒定质量" ? "CRF/CQ 控制画质，容量为粗略估计；成品更大时原样备份。" : Settings.RateMode == "CBR" ? "CBR 不推荐用于离线备份；码率目标仍受源码率限制。" : !PeakLimitSupported ? "SVT-AV1 的 VBR 支持平均目标，不支持独立峰值上限；成品仍执行不增大校验。" : $"VBR 允许复杂场景短时达到平均目标的 {Settings.MaxMultiplier:0.##} 倍。1 Mbps ≈ 0.45 GB/小时（不含音频）。";
    public ICommand NavigateCommand { get; } public ICommand ChooseSourceCommand { get; } public ICommand ChooseOutputCommand { get; } public ICommand ScanCommand { get; } public ICommand RunCommand { get; }
    public ICommand PauseCommand { get; } public ICommand CancelCommand { get; } public ICommand RetryCommand { get; } public ICommand SavePresetCommand { get; } public ICommand DuplicatePresetCommand { get; }
    public ICommand RenamePresetCommand { get; } public ICommand DeletePresetCommand { get; } public ICommand DefaultPresetCommand { get; } public ICommand LoadHistoryCommand { get; }
    public ICommand OpenOutputCommand { get; } public ICommand ExportCsvCommand { get; } public ICommand ExportJsonCommand { get; } public ICommand ShowLogCommand { get; }
    public ICommand ApplyToolsCommand { get; } public ICommand BrowseFfmpegCommand { get; } public ICommand BrowseFfprobeCommand { get; } public ICommand AdvancedCommand { get; } public ICommand NewTaskCommand { get; }
    public ICommand SelectReviewCommand { get; } public ICommand ClearReviewCommand { get; } public ICommand CopyReviewCommand { get; } public ICommand DropReviewCommand { get; }
    public ICommand OpenReviewCommand { get; } public ICommand ChooseWallpaperCommand { get; } public ICommand ResetWallpaperCommand { get; }
    public async Task InitializeAsync(bool first = true, bool forceDetection = false)
    {
        if (Busy) return;
        DetectingHardware = true;
        try
        {
        await Guard(async () =>
        {
            if (first)
            {
                try { configuration = JsonStore.Read<AppConfiguration>(Path.Combine(dataDirectory, "settings.json")) ?? new(); }
                catch (Exception ex) { Status = "配置读取失败，恢复默认：" + ex.Message; configuration = new(); }
                configuration.Appearance ??= new();
                configuration.Appearance.WallpaperOpacity = double.IsFinite(configuration.Appearance.WallpaperOpacity) ? Math.Clamp(configuration.Appearance.WallpaperOpacity, .05, 1) : .35;
                LoadWallpaper(); Raise(nameof(WallpaperOpacity));
                FfmpegPath = File.Exists(configuration.FfmpegPath) ? configuration.FfmpegPath : Files.FindTool("ffmpeg");
                FfprobePath = File.Exists(configuration.FfprobePath) ? configuration.FfprobePath : Files.FindTool("ffprobe");
            }
            if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath)) throw new IOException("请在设置页面选择 FFmpeg 与 FFprobe。");
            Status = "检查硬件环境与编码缓存…可先选择源文件夹";
            var capabilityCache = new CapabilityCache(FfmpegPath, Path.Combine(dataDirectory, "capabilities-v1.json"));
            capabilities = await capabilityCache.GetAsync(forceDetection, cancellation!.Token);
            CapabilityCacheNote = capabilityCache.Note;
            scanner = new ScanService(new ProbeService(FfprobePath), (s, depth) => capabilities.Any(c => c.Available && c.Backend == s.Backend && c.Codec == s.Codec && (depth <= 8 || c.TenBit)), Path.Combine(dataDirectory, "probe-cache-v2.json")); archive = null;
            BackendOptions.Clear(); foreach (var b in capabilities.Where(c => c.Available).Select(c => c.Backend).Distinct()) BackendOptions.Add(b);
            if (configuration.Presets.Count == 0)
            {
                var backend = new[] { "NVIDIA", "Intel", "CPU" }.FirstOrDefault(b => capabilities.Any(c => c.Backend == b && c.Codec == "HEVC" && c.Available)) ?? "CPU";
                configuration.Presets = [new() { Name = "平衡备份", Settings = new() { Backend = backend } }, new() { Name = "空间优先", Settings = new() { Backend = backend, Quality = .75 } }, new() { Name = "高质量备份", Settings = new() { Backend = backend, Quality = 1.25 } }, new() { Name = "1080P备份", Settings = new() { Backend = backend, Limit1080 = true } }];
                configuration.DefaultPresetId = configuration.Presets[0].Id;
            }
            RefreshPresetList();
            SelectedPreset = configuration.Presets.FirstOrDefault(p => p.Id == configuration.DefaultPresetId) ?? configuration.Presets[0];
            Settings = SelectedPreset.Settings.Snapshot(); PresetName = SelectedPreset.Name;
            RefreshOptions(); RefreshHistory(); SaveConfiguration(); initialized = true;
            Raise(nameof(CapabilityText)); Status = "就绪 · " + CapabilityCacheNote + " · 选择方案后扫描";
        });
        }
        finally { DetectingHardware = false; }
    }
    async Task Guard(Func<Task> action)
    {
        if (Busy) return; Busy = true; cancellation = new(); runStarted = DateTime.Now; clockTimer.Start();
        try { await action(); }
        catch (OperationCanceledException) { Status = "已取消"; }
        catch (Exception ex) { Status = ex.Message; MessageBox.Show(ex.Message, "VideoArchive", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; Paused = false; clockTimer.Stop(); cancellation.Dispose(); cancellation = null; RaiseJob(); }
    }
    async Task ScanAsync()
    {
        var completed = false;
        await Guard(async () =>
        {
            ValidateEncoding();
            job = await scanner!.ScanAsync(SourceRoot, OutputRoot, Settings, text => dispatcher.BeginInvoke(() => Status = text), cancellation!.Token);
            PopulateItems(); new ArchiveService(FfmpegPath, FfprobePath, dataDirectory).Save(job);
            Status = $"扫描完成 · {scanner.LastScanTime.TotalSeconds:0.0} 秒 · 复用 {scanner.LastCacheHits} 个视频缓存 · " + (job.Items.Any(i => i.RequiresReview) ? "请先选择时间码待决定文件的处理方式" : "查看处理原因后开始备份"); RaiseJob(); RefreshHistory();
            completed = true;
        });
        if (completed && PendingReviewCount > 0) ReviewRequested?.Invoke();
    }
    async Task RunAsync()
    {
        if (job == null) return;
        replanTimer.Stop(); if (job.State == "已扫描") Replan();
        await Guard(async () =>
        {
            ValidateEncoding(); archive = new(FfmpegPath, FfprobePath, dataDirectory);
            if (job.Items.Any(i => i.Action == "压缩" && !capabilities.Any(c => c.Backend == job.Settings.Backend && c.Codec == (i.TargetCodec.Length > 0 ? i.TargetCodec : job.Settings.Codec) && c.Available && (i.TargetDepth <= 8 || c.TenBit)))) throw new InvalidOperationException("历史任务需要对应编码器和位深支持，请更换编码器并重新扫描。");
            archive.Changed += (_, text) => dispatcher.BeginInvoke(() => { Status = text; UpdateProgress(); ItemView.Refresh(); });
            await Task.Run(() => archive.RunAsync(job, cancellation!.Token));
            Status = job.State; UpdateProgress(); RefreshHistory();
        });
    }
    void ValidateEncoding()
    {
        if (HasValidationError(Application.Current.MainWindow)) throw new InvalidOperationException("有输入框不是有效数字，请修正红框参数。");
        Settings.Validate();
        var c = capabilities.FirstOrDefault(c => c.Backend == Settings.Backend && c.Codec == Settings.Codec && c.Available) ?? throw new InvalidOperationException("所选编码器不可用，请更换编码方式或编码标准。");
        if (Settings.Depth == "10-bit" && !c.TenBit) throw new InvalidOperationException("此编码组合不支持10-bit。");
        if (Settings.RateMode == "CBR" && !c.Cbr) throw new InvalidOperationException("此编码器未验证CBR支持，请选择VBR或恒定质量。");
    }
    void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    { if (refreshingOptions) return; Raise(nameof(ParameterSummary)); Raise(nameof(RateNote)); Raise(nameof(PeakLimitSupported)); Raise(nameof(FixedBitrateEnabled)); if (e.PropertyName is nameof(EncodeSettings.Backend) or nameof(EncodeSettings.Codec) or nameof(EncodeSettings.Depth)) RefreshOptions(); ScheduleReplan(); }
    void ScheduleReplan() { if (Busy) return; replanTimer.Stop(); replanTimer.Start(); }
    void Replan()
    {
        if (job == null || Busy) return;
        if (job.State != "已扫描") { InvalidateScan(); return; }
        try { scanner?.Replan(job, Settings); new ArchiveService(FfmpegPath, FfprobePath, dataDirectory).Save(job); PopulateItems(); RaiseJob(); Status = "已按当前参数更新扫描计划，保留逐文件选择"; }
        catch (Exception ex) { Status = ex.Message; }
    }
    void RefreshOptions()
    {
        if (capabilities.Count == 0 || refreshingOptions) return;
        refreshingOptions = true;
        var chosen = (Settings.Codec, Settings.Depth, Settings.RateMode, Settings.Profile, Settings.EncoderPreset);
        static void Replace(ObservableCollection<string> collection, IEnumerable<string> values)
        { var arr = values.ToArray(); if (collection.SequenceEqual(arr)) return; collection.Clear(); foreach (var v in arr) collection.Add(v); }
        Replace(CodecOptions, capabilities.Where(c => c.Backend == Settings.Backend && c.Available).Select(c => c.Codec));
        Settings.Codec = CodecOptions.Contains(chosen.Codec) ? chosen.Codec : CodecOptions.Contains("HEVC") ? "HEVC" : CodecOptions.FirstOrDefault() ?? chosen.Codec;
        var c = capabilities.FirstOrDefault(c => c.Backend == Settings.Backend && c.Codec == Settings.Codec);
        Replace(DepthOptions, c?.TenBit == true ? ["随源", "8-bit", "10-bit"] : ["随源", "8-bit"]);
        Replace(RateOptions, c?.Cbr == true ? ["VBR", "CBR", "恒定质量"] : ["VBR", "恒定质量"]);
        Replace(ProfileOptions, ["自动"]);
        Replace(EncoderPresets, Settings.Backend switch { "NVIDIA" => ["自动", "p1", "p2", "p3", "p4", "p5", "p6", "p7"], "CPU" when Settings.Codec == "AV1" => ["自动", "4", "6", "8", "10", "12"], "AMD" => ["自动", "speed", "balanced", "quality"], _ => ["自动", "fast", "medium", "slow"] });
        Settings.Depth = DepthOptions.Contains(chosen.Depth) ? chosen.Depth : "随源";
        Settings.RateMode = RateOptions.Contains(chosen.RateMode) ? chosen.RateMode : "VBR";
        Settings.Profile = "自动";
        Settings.EncoderPreset = EncoderPresets.Contains(chosen.EncoderPreset) ? chosen.EncoderPreset : "自动";
        refreshingOptions = false; Raise(nameof(ParameterSummary));
    }
    static bool HasValidationError(DependencyObject obj)
    {
        if (System.Windows.Controls.Validation.GetHasError(obj)) return true;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(obj, i))) return true;
        return false;
    }
    void InvalidateScan() { if (Busy && !DetectingHardware) return; job = null; Items.Clear(); RaiseJob(); }
    void PopulateItems() { Items.Clear(); if (job != null) foreach (var i in job.Items) Items.Add(i); ApplyFilter(); }
    void ApplyFilter() { ItemView.Filter = value => value is PlanItem i && (Filter switch { "视频" => i.VideoCandidate || i.Media != null, "待决定" => i.RequiresReview, "时间码选择" => i.ReviewIssue.Length > 0, "待压缩" => i.Action == "压缩", "原样复制" => i.Action == "复制", "失败" => i.Status == "失败", "未处理" => i.Action == "未处理", _ => true }); ItemView.Refresh(); }
    void RaiseJob() { foreach (var name in new[] { nameof(ScanSummary), nameof(EstimateSummary), nameof(Distribution), nameof(Notices), nameof(Report), nameof(OverallProgress), nameof(ProgressText), nameof(CurrentFile), nameof(Eta), nameof(HasReviewItems), nameof(CanMakeChoices), nameof(ReviewSummary), nameof(PendingReviewCount), nameof(ReviewAlert) }) Raise(name); CommandManager.InvalidateRequerySuggested(); }
    public bool ReviewTimecodes(object? parameter, string choice)
    {
        if (!CanMakeChoices || job == null || scanner == null) return false;
        try
        {
            replanTimer.Stop();
            var chosen = parameter is PlanItem item ? new[] { item } : parameter is IEnumerable<PlanItem> selection ? selection.Where(i => i.CanReview).ToArray() : Items.Where(i => i.ReviewSelected && i.CanReview).ToArray();
            if (chosen.Length == 0) { Status = "请先勾选需要处理的文件"; return false; }
            var next = JsonStore.Clone(job);
            var selectedPaths = chosen.Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checkedPaths = Items.Where(i => i.ReviewSelected).Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            next.Settings = Settings.Snapshot();
            scanner.Review(next, next.Items.Where(i => selectedPaths.Contains(i.RelativePath)).ToArray(), choice);
            foreach (var row in next.Items) row.ReviewSelected = checkedPaths.Contains(row.RelativePath);
            new ArchiveService(FfmpegPath, FfprobePath, dataDirectory).Save(next);
            job = next;
            PopulateItems(); RaiseJob();
            Status = $"已保存 {chosen.Length} 个文件的选择：{choice} · 剩余待决定 {job.Items.Count(i => i.RequiresReview)} 个";
            return true;
        }
        catch (Exception ex) { Status = ex.Message; return false; }
    }
    static BitmapSource ReadWallpaper(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 2560; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    void LoadWallpaper()
    {
        try { WallpaperImage = File.Exists(configuration.Appearance.WallpaperFile) ? ReadWallpaper(configuration.Appearance.WallpaperFile) : null; }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException) { WallpaperImage = null; }
    }
    public void SetWallpaper(string path)
    {
        var image = ReadWallpaper(path);
        var directory = Path.Combine(dataDirectory, "appearance"); Directory.CreateDirectory(directory);
        var stored = Path.Combine(directory, "wallpaper-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant());
        File.Copy(path, stored);
        var previous = configuration.Appearance.WallpaperFile;
        configuration.Appearance.WallpaperFile = stored;
        try { SaveConfiguration(); }
        catch { configuration.Appearance.WallpaperFile = previous; File.Delete(stored); throw; }
        WallpaperImage = image; Status = "壁纸已保存 · 可在设置中调整显示强度";
    }
    void ChooseWallpaper()
    {
        var dialog = new OpenFileDialog { Title = "选择自定义壁纸", Filter = "图片（PNG / JPG / BMP / GIF）|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;
        try { SetWallpaper(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or UnauthorizedAccessException) { Status = "壁纸无法读取：" + ex.Message; }
    }
    void UpdateProgress() { foreach (var name in new[] { nameof(Report), nameof(OverallProgress), nameof(ProgressText), nameof(CurrentFile), nameof(Eta), nameof(JobRunning) }) Raise(name); CommandManager.InvalidateRequerySuggested(); }
    void PickFolder(bool source) { var dialog = new OpenFolderDialog { Title = source ? "选择源素材目录" : "选择其他存放位置，将创建“源文件夹名_已压缩”" }; if (dialog.ShowDialog() == true) { if (source) SourceRoot = dialog.FolderName; else { OutputParent = dialog.FolderName; OtherOutput = true; } } }
    void PickTool(bool ffmpeg) { var dialog = new OpenFileDialog { Filter = "可执行文件|*.exe" }; if (dialog.ShowDialog() == true) { if (ffmpeg) FfmpegPath = dialog.FileName; else FfprobePath = dialog.FileName; } }
    string RequireName() => string.IsNullOrWhiteSpace(PresetName) ? "我的备份方案" : PresetName.Trim();
    void SavePreset(bool duplicate)
    {
        try
        {
            Settings.Validate(); var p = duplicate || SelectedPreset == null ? new Preset() : SelectedPreset;
            p.Name = RequireName() + (duplicate ? " 副本" : ""); p.Settings = Settings.Snapshot();
            if (!configuration.Presets.Contains(p)) configuration.Presets.Add(p);
            RefreshPresetList(); SelectedPreset = p; SaveConfiguration(); Status = "预设已保存";
        }
        catch (Exception ex) { Status = ex.Message; }
    }
    void RefreshPresetList() { var previous = selectedPreset; Presets.Clear(); foreach (var p in configuration.Presets) Presets.Add(p); SelectedPreset = previous != null && Presets.Contains(previous) ? previous : Presets.FirstOrDefault(); Raise(nameof(SelectedPreset)); }
    void SaveConfiguration() { configuration.FfmpegPath = FfmpegPath; configuration.FfprobePath = FfprobePath; JsonStore.Write(Path.Combine(dataDirectory, "settings.json"), configuration); }
    void RefreshHistory()
    {
        History.Clear(); var dir = Path.Combine(dataDirectory, "jobs"); if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTime).Take(100))
            try { var j = JsonStore.Read<ArchiveJob>(path); if (j != null) History.Add(j); } catch { /* A damaged history file must not prevent startup. */ }
    }
    void LoadHistory()
    {
        if (SelectedHistory == null) return; replanTimer.Stop(); var j = JsonStore.Clone(SelectedHistory);
        SourceRoot = j.SourceRoot;
        otherOutput = !j.OutputRoot.Equals(Files.BackupOutput(j.SourceRoot), StringComparison.OrdinalIgnoreCase);
        outputParent = Path.GetDirectoryName(j.OutputRoot) ?? ""; Raise(nameof(OtherOutput)); Raise(nameof(OutputParent));
        OutputRoot = j.OutputRoot; Settings = j.Settings.Snapshot(); job = j;
        selectedPreset = null; Raise(nameof(SelectedPreset));
        foreach (var p in job.Items.Where(i => i.Status is "压缩中" or "复制中" or "验证中" or "已取消")) { p.Status = "待处理"; p.Progress = 0; }
        PopulateItems(); RaiseJob(); Page = "压缩任务"; Status = job.PlanVersion == MediaPolicy.PlanVersion ? "已载入历史 · 恢复执行会先验证已完成的输出" : "已载入旧任务 · 请重新扫描以使用新的时间码选择与Main10规则";
        if (job.PlanVersion == MediaPolicy.PlanVersion && PendingReviewCount > 0) ReviewRequested?.Invoke();
    }
    void Export(bool json)
    {
        if (job == null) return;
        var dialog = new SaveFileDialog { FileName = "VideoArchive-" + job.Started.ToString("yyyyMMdd-HHmm"), Filter = json ? "JSON 报告|*.json" : "CSV 报告|*.csv" };
        if (dialog.ShowDialog() != true) return;
        try { if (json) JsonStore.Write(dialog.FileName, job); else ReportService.ExportCsv(job, dialog.FileName); Status = "报告已导出"; } catch (Exception ex) { Status = ex.Message; }
    }
    void ShowLog()
    {
        if (SelectedItem == null) return;
        var p = SelectedItem;
        var box = new System.Windows.Controls.TextBox { Text = $"{p.RelativePath}\n{p.SourceSpec}\n{p.TargetSpec}\n{p.Reason}\n{p.DisplayStatus}\n\n{(p.Action == "压缩" && p.Media != null ? CommandBuilder.Preview(p, job!.Settings, FfmpegPath) : "原样复制无需编码命令")}\n\n{p.Log}", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI") };
        new Window { Title = "文件详情 / FFmpeg 命令（可选择复制）", Owner = Application.Current.MainWindow, Width = 850, Height = 540, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    public void DropFolder(string path) { if (FolderSelectionEnabled && Directory.Exists(path)) { SourceRoot = path; Page = "压缩任务"; } }
    public async Task PrepareCaptureAsync(string? source, string? output, string? targetPage, bool showAdvanced)
    {
        if (source != null) { SourceRoot = source; if (output != null) { OutputParent = output; OtherOutput = true; } await ScanAsync(); }
        if (targetPage != null) Page = targetPage;
        Advanced = showAdvanced;
    }
    public void PrepareReviewDemo()
    {
        var media = job?.Items.FirstOrDefault(i => i.Media != null)?.Media;
        if (media == null) throw new InvalidOperationException("Review UI verification requires sample source media");
        var demoMedia = JsonStore.Clone(media);
        demoMedia.Streams.Add(new MediaStream { Index = demoMedia.Streams.Max(i => i.Index) + 1, Type = "data", CodecTag = "tmcd" });
        var demoSettings = Settings.Snapshot(); demoSettings.Unified = true;
        var engine = new DecisionEngine();
        job = new ArchiveJob { SourceRoot = SourceRoot, OutputRoot = OutputRoot, Settings = demoSettings, PlanVersion = MediaPolicy.PlanVersion, Items = Enumerable.Range(1, 3).Select(i =>
        {
            var file = JsonStore.Clone(demoMedia);
            file.Path = Path.Combine(SourceRoot, $"时间码待确认演示 {i}.mp4");
            return engine.Create(SourceRoot, OutputRoot, file.Path, file.Size, file.ModifiedUtc, file, demoSettings);
        }).ToList() };
        Settings = demoSettings; PopulateItems(); RaiseJob();
    }
    public async Task VerifyUiAsync()
    {
        if (string.IsNullOrEmpty(Settings.Backend) || string.IsNullOrEmpty(Settings.Codec)) throw new InvalidOperationException("UI verification: empty default selection");
        var initial = Settings.Snapshot(); var initialPage = Page; var initialAdvanced = Advanced; var initialPreset = SelectedPreset;
        var initialSource = SourceRoot; var initialOutput = OutputRoot; var initialOtherOutput = OtherOutput; var initialOutputParent = OutputParent; var initialJob = job;
        if (initialJob?.Items.Any(i => i.RequiresReview) == true)
        {
            job = JsonStore.Clone(initialJob); PopulateItems(); RaiseJob();
            if (RunCommand.CanExecute(null)) throw new InvalidOperationException("UI verification: pending choices must block execution");
            using var decisions = new ReviewDialogViewModel(this);
            if (decisions.CopySelectedCommand.CanExecute(null)) throw new InvalidOperationException("UI verification: empty decision selection must disable actions");
            decisions.AllSelected = true;
            if (decisions.SelectedCount != decisions.Rows.Count || decisions.AllSelected != true) throw new InvalidOperationException("UI verification: select all failed");
            decisions.AllSelected = false;
            decisions.Rows[0].ReviewSelected = true;
            if (decisions.AllSelected != null) throw new InvalidOperationException("UI verification: mixed selection must be indeterminate");
            decisions.CopySelectedCommand.Execute(null);
            if (job.Items.Count(i => i.RequiresReview) != initialJob.Items.Count(i => i.RequiresReview) - 1) throw new InvalidOperationException("UI verification: individual decision failed");
            decisions.SelectPendingCommand.Execute(null); decisions.CompressSelectedCommand.Execute(null);
            if (!decisions.AllDecided || decisions.SelectedCount != 0) throw new InvalidOperationException("UI verification: dialog decisions failed");
            if (job.Items.Any(i => i.RequiresReview) || !job.Items.Any(i => i.DropTimecode) || !job.Items.Any(i => i.TimecodeChoice == "原样备份")) throw new InvalidOperationException("UI verification: batch decision failed");
            var persistedJob = JsonStore.Read<ArchiveJob>(new ArchiveService(FfmpegPath, FfprobePath, dataDirectory).JobPath(job));
            if (persistedJob?.Items.Any(i => i.RequiresReview) != false) throw new InvalidOperationException("UI verification: choices were not persisted");
            job = initialJob; PopulateItems(); RaiseJob();
        }
        SourceRoot = @"D:\UI验证素材"; OtherOutput = false;
        if (OutputRoot != @"D:\UI验证素材_已压缩") throw new InvalidOperationException("UI verification: default output must be a sibling");
        OutputParent = @"E:\UI验证备份"; OtherOutput = true;
        if (OutputRoot != @"E:\UI验证备份\UI验证素材_已压缩") throw new InvalidOperationException("UI verification: other folder must contain a complete suffixed backup");
        sourceRoot = initialSource; outputRoot = initialOutput; otherOutput = initialOtherOutput; outputParent = initialOutputParent; job = initialJob;
        Raise(nameof(SourceRoot)); Raise(nameof(OutputRoot)); Raise(nameof(OutputFolderName)); Raise(nameof(OtherOutput)); Raise(nameof(OutputParent)); PopulateItems(); RaiseJob();
        foreach (var backend in BackendOptions.ToArray())
        {
            Settings.Backend = backend;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var codec in CodecOptions.ToArray())
            {
                Settings.Codec = codec;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (Settings.Codec != codec || Settings.Profile != "自动" || string.IsNullOrEmpty(Settings.Depth) || string.IsNullOrEmpty(Settings.RateMode) || string.IsNullOrEmpty(Settings.EncoderPreset)) throw new InvalidOperationException("UI verification: options lost while switching");
            }
        }
        Settings = initial;
        PresetName = "UI验证方案"; SavePreset(true);
        var saved = JsonStore.Read<AppConfiguration>(Path.Combine(dataDirectory, "settings.json"));
        if (!saved!.Presets.Any(p => p.Name == "UI验证方案 副本")) throw new InvalidOperationException("UI verification: preset not persisted");
        foreach (var target in new[] { "压缩任务", "压缩方案", "任务历史", "设置" })
        {
            Page = target; await dispatcher.InvokeAsync(() => Application.Current.MainWindow.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        SelectedPreset = initialPreset; Settings = initial; Page = initialPage; Advanced = initialAdvanced;
        var originalAppearance = JsonStore.Clone(configuration.Appearance);
        var wallpaperSource = Path.Combine(dataDirectory, "ui-wallpaper-source.png");
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(48, 135, 150), System.Windows.Media.Color.FromRgb(247, 183, 139), 40), null, new Rect(0, 0, 1280, 720));
            context.DrawEllipse(System.Windows.Media.Brushes.LightGoldenrodYellow, null, new Point(1060, 135), 95, 95);
            context.DrawGeometry(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 99, 132)), null, System.Windows.Media.Geometry.Parse("M0,720 L0,550 320,290 620,650 900,410 1280,650 1280,720 Z"));
        }
        var wallpaper = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 720, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); wallpaper.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(wallpaper));
        using (var file = File.Create(wallpaperSource)) encoder.Save(file);
        SetWallpaper(wallpaperSource); WallpaperOpacity = .65;
        File.Delete(wallpaperSource); LoadWallpaper();
        var appearance = JsonStore.Read<AppConfiguration>(Path.Combine(dataDirectory, "settings.json"))!.Appearance;
        if (!HasWallpaper || appearance.WallpaperOpacity != .65 || !File.Exists(appearance.WallpaperFile)) throw new InvalidOperationException("UI verification: wallpaper copy and persistence failed");
        await dispatcher.InvokeAsync(() => Application.Current.MainWindow.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        ResetWallpaperCommand.Execute(null);
        if (HasWallpaper || JsonStore.Read<AppConfiguration>(Path.Combine(dataDirectory, "settings.json"))!.Appearance.WallpaperFile != "") throw new InvalidOperationException("UI verification: wallpaper reset failed");
        configuration.Appearance = originalAppearance; LoadWallpaper(); Raise(nameof(WallpaperOpacity)); SaveConfiguration();
    }
    public void RequestClose(CancelEventArgs e)
    {
        if (!Busy) return;
        e.Cancel = true; cancellation?.Cancel(); Status = "正在停止任务，请稍后再关闭；已完成文件可从历史恢复。";
    }
}
