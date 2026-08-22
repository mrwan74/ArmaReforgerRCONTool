using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class ConnectionLostDialog : UserControl
{
    public ConnectionLostDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[ConnectionLostDialog] Failed during component initialization.", ex);
        }
    }
}