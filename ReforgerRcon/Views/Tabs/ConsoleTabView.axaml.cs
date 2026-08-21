using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views.Tabs;

public partial class ConsoleTabView : UserControl
{
    private readonly ScrollViewer? _logScrollViewer;
    private ConsoleViewModel? _currentViewModel;

    public ConsoleTabView()
    {
        InitializeComponent();
        _logScrollViewer = this.FindControl<ScrollViewer>("PART_LogScrollViewer");

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();

        if (DataContext is ConsoleViewModel vm)
        {
            _currentViewModel = vm;
            _currentViewModel.ScrollToEndRequested += OnScrollToEndRequested;
            _currentViewModel.FilteredLogs.CollectionChanged += OnLogsCollectionChanged;

            if (_currentViewModel.AutoScroll)
            {
                OnScrollToEndRequested();
            }
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void DetachViewModel()
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _currentViewModel.FilteredLogs.CollectionChanged -= OnLogsCollectionChanged;
            _currentViewModel = null;
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_currentViewModel is { AutoScroll: true })
        {
            OnScrollToEndRequested();
        }
    }

    private void OnScrollToEndRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _logScrollViewer?.ScrollToEnd();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[ConsoleTabView] Non-fatal layout notification during ScrollToEnd: {ex.Message}");
            }
        }, DispatcherPriority.Loaded);
    }
}