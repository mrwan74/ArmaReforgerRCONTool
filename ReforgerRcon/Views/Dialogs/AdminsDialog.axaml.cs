using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class AdminsDialog : UserControl
{
    public AdminsDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AdminsDialog] Component initialization failed.", ex);
        }
    }
}