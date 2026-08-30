using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DashboardView] Component initialization failed.", ex);
        }
    }
}