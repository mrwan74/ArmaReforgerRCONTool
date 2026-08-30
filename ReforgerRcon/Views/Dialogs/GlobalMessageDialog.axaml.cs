using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class GlobalMessageDialog : UserControl
{
    public GlobalMessageDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GlobalMessageDialog] Component initialization failed.", ex);
        }
    }
}