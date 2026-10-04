using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using VideoArchive.Core;

namespace VideoArchive;

public sealed class ReviewDialogViewModel : Observable, IDisposable
{
    readonly MainViewModel parent;
    string message = "勾选文件，然后选择上方的处理方式。选择立即保存，但不会开始压缩。";
    bool refreshing;
    public ObservableCollection<PlanItem> Rows { get; } = [];
    public ICommand CopySelectedCommand { get; }
    public ICommand CompressSelectedCommand { get; }
    public ICommand SelectPendingCommand { get; }
    public string Message { get => message; private set => Set(ref message, value); }
    public int PendingCount => Rows.Count(i => i.RequiresReview);
    public int SelectedCount => Rows.Count(i => i.ReviewSelected);
    public int DecidedCount => Rows.Count - PendingCount;
    public string SelectionText => $"已勾选 {SelectedCount} / {Rows.Count} 个文件";
    public bool AllDecided => PendingCount == 0;
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Rows.Count ? true : null;
        set { foreach (var row in Rows) row.ReviewSelected = value == true; Notify(); }
    }
    public ReviewDialogViewModel(MainViewModel parent)
    {
        this.parent = parent;
        CopySelectedCommand = new RelayCommand(_ => Apply("原样备份"), () => SelectedCount > 0 && parent.CanMakeChoices);
        CompressSelectedCommand = new RelayCommand(_ => Apply("舍弃时间码"), () => SelectedCount > 0 && parent.CanMakeChoices);
        SelectPendingCommand = new RelayCommand(_ => { foreach (var row in Rows) row.ReviewSelected = row.RequiresReview; Notify(); });
        parent.PropertyChanged += ParentChanged;
        Refresh();
    }
    void ParentChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.HasReviewItems)) Refresh(); }
    void Refresh()
    {
        refreshing = true;
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows.Clear();
        foreach (var row in parent.Items.Where(i => i.CanReview)) { Rows.Add(row); row.PropertyChanged += RowChanged; }
        refreshing = false; Notify();
    }
    void RowChanged(object? sender, PropertyChangedEventArgs e) { if (!refreshing && e.PropertyName == nameof(PlanItem.ReviewSelected)) Notify(); }
    void Notify()
    {
        foreach (var name in new[] { nameof(AllSelected), nameof(PendingCount), nameof(SelectedCount), nameof(DecidedCount), nameof(SelectionText), nameof(AllDecided) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }
    void Apply(string choice)
    {
        var selected = Rows.Where(i => i.ReviewSelected).ToArray();
        if (parent.ReviewTimecodes(selected, choice))
        {
            foreach (var row in Rows) row.ReviewSelected = false;
            Message = $"已保存 {selected.Length} 个文件的选择：{(choice == "舍弃时间码" ? "舍弃时间码后按方案压缩" : choice)}。" + (AllDecided ? "全部处理方式已确认，可返回开始备份。" : $"还剩 {PendingCount} 个待决定。");
        }
        else Message = parent.Status;
        Notify();
    }
    public void Dispose()
    {
        parent.PropertyChanged -= ParentChanged;
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
    }
}
