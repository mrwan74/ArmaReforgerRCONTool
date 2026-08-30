using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class AnnouncementDialog : UserControl
{
    public AnnouncementDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AnnouncementDialog] Component initialization failed.", ex);
        }
    }
}