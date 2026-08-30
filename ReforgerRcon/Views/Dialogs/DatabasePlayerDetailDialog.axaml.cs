using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class DatabasePlayerDetailDialog : UserControl
{
    public DatabasePlayerDetailDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DatabasePlayerDetailDialog] Component initialization failed.", ex);
        }
    }
}