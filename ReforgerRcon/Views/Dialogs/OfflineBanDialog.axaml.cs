using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class OfflineBanDialog : UserControl
{
    public OfflineBanDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[OfflineBanDialog] Component initialization failed.", ex);
        }
    }
}