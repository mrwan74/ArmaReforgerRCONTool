using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class ConfirmDialog : UserControl
{
    public ConfirmDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[ConfirmDialog] Component initialization failed.", ex);
        }
    }
}