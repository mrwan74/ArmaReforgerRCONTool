using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class BanDialog : UserControl
{
    public BanDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[BanDialog] Component initialization failed.", ex);
        }
    }
}