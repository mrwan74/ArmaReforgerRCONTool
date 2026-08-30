using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class SetCommentDialog : UserControl
{
    public SetCommentDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[SetCommentDialog] Component initialization failed.", ex);
        }
    }
}