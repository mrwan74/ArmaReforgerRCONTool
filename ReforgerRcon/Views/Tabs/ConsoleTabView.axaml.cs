using System;
using System.Collections.Specialized;
using System.Diagnostics;
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
    private bool _isScrollPending;

    public ConsoleTabView()
    {
        var start = Stopwatch.GetTimestamp();
        InitializeComponent();
        _logScrollViewer = this.FindControl<ScrollViewer>("PART_LogScrollViewer");

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        AppLogger.Debug($"[ConsoleTabView:Init] Initialized in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
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
            AppLogger.Trace($"[ConsoleTabView:Bind] ViewModel attached in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        DetachViewModel();
        AppLogger.Trace($"[ConsoleTabView:Unload] Detached in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
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
        if (_isScrollPending) return;
        _isScrollPending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _isScrollPending = false;
            try
            {
                _logScrollViewer?.ScrollToEnd();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[ConsoleTabView:Scroll] Non-fatal notice: {ex.Message}");
            }
        }, DispatcherPriority.Background);
    }
}