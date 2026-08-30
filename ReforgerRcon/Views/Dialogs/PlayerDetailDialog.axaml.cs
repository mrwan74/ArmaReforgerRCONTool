using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class PlayerDetailDialog : UserControl
{
    public PlayerDetailDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDetailDialog] Component initialization failed.", ex);
        }
    }
}