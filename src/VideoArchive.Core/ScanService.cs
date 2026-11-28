using System.Collections.Concurrent;
using System.Diagnostics;

namespace VideoArchive.Core;

public sealed class ScanService(ProbeService probe, Func<EncodeSettings, int, bool>? depthSupported = null, string? cachePath = null, int scanParallelism = 4)
{
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".mts", ".m2ts", ".ts", ".webm", ".wmv", ".mpg", ".mpeg", ".mxf", ".3gp", ".vob", ".flv" };
    readonly ConcurrentDictionary<string, MediaInfo> cache = LoadCache(cachePath, probe.CacheIdentity);
    public int LastCacheHits { get; private set; }
    public TimeSpan LastScanTime { get; private set; }
    sealed class ProbeCache
    {
        public int Version { get; set; } = 3;
        public string Tool { get; set; } = "";
        public Dictionary<string, MediaInfo> Entries { get; set; } = [];
    }
    static ConcurrentDictionary<string, MediaInfo> LoadCache(string? path, string tool)
    {
        try
        {
            var stored = path == null ? null : JsonStore.Read<ProbeCache>(path);
            if (stored?.Version == 3 && stored.Tool == tool) return new(stored.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }
    public static bool RequiresCadenceRescan(ArchiveJob? job, EncodeSettings settings) =>
        job is { State: "已扫描", Settings.LimitFps: false } && settings.LimitFps &&
        job.Items.Any(item => item.Media is { CadenceChecked: false });

    public async Task<ArchiveJob> ScanAsync(string source, string output, EncodeSettings settings, Action<string>? message, CancellationToken ct, ArchiveJob? previousJob = null)
    {
        settings.Validate(); Files.ValidateRoots(source, output);
        var watch = Stopwatch.StartNew();
        var job = new ArchiveJob { SourceRoot = Files.Normalize(source), OutputRoot = Files.Normalize(output), Settings = settings.Snapshot(), PlanVersion = MediaPolicy.PlanVersion };
        var previousChoices = previousJob?.SourceRoot.Equals(job.SourceRoot, StringComparison.OrdinalIgnoreCase) == true
            ? previousJob.Items.Where(item => item.TimecodeChoice.Length > 0).ToDictionary(item => item.Source, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        var engine = new DecisionEngine(); var stack = new Stack<string>(); stack.Push(job.SourceRoot);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var directory = stack.Pop();
            job.Directories.Add(Path.GetRelativePath(job.SourceRoot, directory));
            IEnumerable<string> entries;
            try { entries = Directory.GetFileSystemEntries(directory).OrderBy(x => x).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Notices.Add($"无法读取目录 {directory}：{ex.Message}"); continue; }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (Files.Normalize(entry).Equals(job.OutputRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    { job.Items.Add(new PlanItem { Source = entry, RelativePath = Path.GetRelativePath(job.SourceRoot, entry), Action = "未处理", Reason = "目录联接 / 符号链接不跟随", Status = "未处理" }); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { stack.Push(entry); continue; }
                    files.Add(new FileInfo(entry));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { job.Items.Add(new PlanItem { Source = entry, RelativePath = Path.GetRelativePath(job.SourceRoot, entry), Action = "未处理", Reason = ex.Message, Status = "未处理" }); }
            }
        }
        var items = new ConcurrentBag<PlanItem>(job.Items);
        var occupied = OccupiedNames(job, files.Select(f => Path.GetRelativePath(job.SourceRoot, f.FullName))
            .Concat(job.Items.Select(item => item.RelativePath)));
        int completed = 0, hits = 0, dirty = 0; var progressLock = new object(); long lastUpdate = -200;
        try
        {
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(scanParallelism, 1, 8), CancellationToken = ct }, async (f, token) =>
            {
                var entry = f.FullName;
                try
                {
                    MediaInfo? media = null; string? warning = null;
                    if (VideoExtensions.Contains(f.Extension))
                    {
                        if (cache.TryGetValue(entry, out var previous) && previous.Size == f.Length && previous.ModifiedUtc == f.LastWriteTimeUtc &&
                            (!settings.LimitFps || previous.CadenceChecked) &&
                            (!MxfPolicy.IsInput(previous) || previous.Streams.All(s => s.Type != "audio" || s.SampleRate > 0 && s.Channels > 0)))
                        { media = previous; Interlocked.Increment(ref hits); }
                        else
                        {
                            try { media = await probe.ProbeAsync(entry, settings.LimitFps, token); cache[entry] = media; Interlocked.Exchange(ref dirty, 1); }
                            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { warning = "媒体探测失败，原样保留：" + ex.Message; }
                        }
                    }
                    var choice = previousChoices.TryGetValue(entry, out var earlier) && earlier.SourceSize == f.Length && earlier.SourceModifiedUtc == f.LastWriteTimeUtc
                        ? earlier.TimecodeChoice : "";
                    var item = engine.Create(job.SourceRoot, job.OutputRoot, entry, f.Length, f.LastWriteTimeUtc, media, settings, choice);
                    item.VideoCandidate = VideoExtensions.Contains(f.Extension);
                    if (warning != null) item.Reason = warning;
                    FinalizePlan(job, item, settings, occupied);
                    items.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { items.Add(new PlanItem { Source = entry, RelativePath = Path.GetRelativePath(job.SourceRoot, entry), Action = "未处理", Reason = ex.Message, Status = "未处理" }); }
                var done = Interlocked.Increment(ref completed);
                lock (progressLock)
                {
                    if (done == files.Count || watch.ElapsedMilliseconds - lastUpdate >= 150)
                    { lastUpdate = watch.ElapsedMilliseconds; message?.Invoke($"扫描 {done}/{files.Count} · 缓存 {Volatile.Read(ref hits)} · {watch.Elapsed.TotalSeconds:0.0} 秒 · {Path.GetRelativePath(source, entry)}"); }
                }
            });
        }
        finally
        {
            LastCacheHits = hits;
            if (cachePath != null && dirty != 0)
            {
                try { JsonStore.Write(cachePath, new ProbeCache { Tool = probe.CacheIdentity, Entries = cache.OrderByDescending(entry => Files.Contains(job.SourceRoot, entry.Key)).ThenByDescending(entry => entry.Value.ModifiedUtc).Take(20000).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Notices.Add("探测缓存未保存：" + ex.Message); }
            }
            LastScanTime = watch.Elapsed;
        }
        job.Items = items.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        return job;
    }
    public void Replan(ArchiveJob job, EncodeSettings settings)
    {
        if (job.PlanVersion != MediaPolicy.PlanVersion) throw new InvalidOperationException("旧任务的探测信息需要更新，请重新扫描以使用新的数据轨规则。");
        if (RequiresCadenceRescan(job, settings)) throw new InvalidOperationException("开启限帧后需要重新扫描，以检测峰值帧率。");
        settings.Validate(); var engine = new DecisionEngine(); job.Settings = settings.Snapshot();
        var occupied = OccupiedNames(job, job.Items.Select(item => item.RelativePath));
        job.Items = job.Items.Select(p =>
        {
            if (p.Action == "未处理") return p;
            var n = engine.Create(job.SourceRoot, job.OutputRoot, p.Source, p.SourceSize, p.SourceModifiedUtc, p.Media, settings, p.TimecodeChoice);
            n.ReviewSelected = p.ReviewSelected;
            n.VideoCandidate = p.VideoCandidate;
            if (p.Media == null && p.Reason.StartsWith("媒体探测失败")) n.Reason = p.Reason;
            FinalizePlan(job, n, settings, occupied);
            return n;
        }).ToList();
    }
    public void Review(ArchiveJob job, IEnumerable<PlanItem> items, string choice)
    {
        if (job.State != "已扫描") throw new InvalidOperationException("请重新扫描后修改处理选择。");
        if (job.PlanVersion != MediaPolicy.PlanVersion) throw new InvalidOperationException("请重新扫描以使用新的处理规则。");
        job.Settings.Validate();
        if (choice is not ("原样备份" or "舍弃时间码")) throw new ArgumentException("未知的时间码处理方式。", nameof(choice));
        foreach (var item in items.Where(i => job.Items.Contains(i) && i.CanReview)) item.TimecodeChoice = choice;
        Replan(job, job.Settings);
    }
    static HashSet<string> OccupiedNames(ArchiveJob job, IEnumerable<string> files) =>
        job.Directories.Concat(files).ToHashSet(StringComparer.OrdinalIgnoreCase);

    void FinalizePlan(ArchiveJob job, PlanItem item, EncodeSettings settings, HashSet<string> occupied)
    {
        bool mxfCandidate = item.MxfToMov && item.Action is ("压缩" or "待决定");
        if (mxfCandidate && occupied.Contains(Path.ChangeExtension(item.RelativePath, ".mov")))
            CopyInstead("同一任务存在同名 MOV 路径，MXF 原样保留");
        else if ((item.Action == "压缩" || mxfCandidate) && depthSupported != null &&
            !depthSupported(MediaPolicy.EffectiveSettings(item, settings), item.TargetDepth))
            CopyInstead(item.MxfToMov ? "当前编码设备不支持 HEVC Main10，MXF 原样保留" : "当前编码组合不支持源位深，原样保留");
        if (!(item.MxfToMov && item.RequiresReview) && File.Exists(item.Destination) && settings.Collision == "跳过")
        {
            item.Action = "跳过";
            item.EstimatedSize = 0;
            item.Reason = "输出已存在，按规则跳过";
        }
        item.PlannedAction = item.Action;

        void CopyInstead(string reason)
        {
            item.Action = "复制";
            item.Reason = reason;
            item.EstimatedSize = item.SourceSize;
            if (!item.MxfToMov) return;
            item.Destination = Path.Combine(job.OutputRoot, item.RelativePath);
            item.ReviewIssue = "";
            item.TimecodeChoice = "";
        }
    }
}
