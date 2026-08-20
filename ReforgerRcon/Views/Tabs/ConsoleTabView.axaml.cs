using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views.Tabs;

public partial class ConsoleTabView : UserControl
{
    private readonly ScrollViewer? _logScrollViewer;

    public ConsoleTabView()
    {
        InitializeComponent();
        _logScrollViewer = this.FindControl<ScrollViewer>("PART_LogScrollViewer");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConsoleViewModel vm)
            {
                vm.FilteredLogs.CollectionChanged += OnLogsCollectionChanged;
            }
        };
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is ConsoleViewModel { AutoScroll: true } && _logScrollViewer != null)
        {
            Dispatcher.UIThread.Post(() => _logScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }
    }
}