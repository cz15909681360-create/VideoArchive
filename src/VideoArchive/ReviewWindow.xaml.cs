using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoArchive;

public partial class ReviewWindow : Window
{
    public ReviewDialogViewModel Model { get; }
    public ReviewWindow(MainViewModel parent)
    {
        InitializeComponent(); Model = new(parent); DataContext = Model;
        Closed += (_, _) => Model.Dispose();
    }
    public async Task VerifyBindingsAsync()
    {
        if (CopyButton.IsEnabled || CompressButton.IsEnabled || DoneButton.IsEnabled) throw new InvalidOperationException("Decision dialog: empty selection or pending completion not disabled");
        SelectAllBox.IsChecked = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (Model.SelectedCount != Model.Rows.Count || !CopyButton.IsEnabled || !CompressButton.IsEnabled) throw new InvalidOperationException("Decision dialog: header select-all binding failed");
        SelectAllBox.IsChecked = false; UpdateLayout();
        ReviewGrid.ScrollIntoView(Model.Rows[0]); ReviewGrid.UpdateLayout();
        var row = (DataGridRow)ReviewGrid.ItemContainerGenerator.ContainerFromItem(Model.Rows[0]);
        T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
        var checkbox = Find<CheckBox>(row) ?? throw new InvalidOperationException("Decision dialog: per-file checkbox missing");
        checkbox.IsChecked = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (Model.SelectedCount != 1 || SelectAllBox.IsChecked != null) throw new InvalidOperationException("Decision dialog: single/mixed selection binding failed");
        CopyButton.Command.Execute(null);
        Model.SelectPendingCommand.Execute(null); CompressButton.Command.Execute(null);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!DoneButton.IsEnabled || !Model.AllDecided || Model.SelectedCount != 0) throw new InvalidOperationException("Decision dialog: completion binding failed");
    }
    void Done_Click(object sender, RoutedEventArgs e) => Close();
    void Later_Click(object sender, RoutedEventArgs e) => Close();
}
