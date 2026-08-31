using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Tabs;

public partial class SettingsTabView : UserControl
{
    public SettingsTabView()
    {
        try
        {
            InitializeComponent();
            AppLogger.Debug("[SettingsTabView] SettingsTabView component initialized.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[SettingsTabView] Component initialization failed.", ex);
            CrashReportService.HandleFatalException("SettingsTabView.Constructor", ex, isTerminating: false);
        }
    }
}