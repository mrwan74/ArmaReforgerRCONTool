using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class GeoIpUpdateDialog : UserControl
{
    public GeoIpUpdateDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GeoIpUpdateDialog] Component initialization failed.", ex);
        }
    }
}