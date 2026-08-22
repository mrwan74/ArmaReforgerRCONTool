using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class ProtocolHelpDialog : UserControl
{
    public ProtocolHelpDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[ProtocolHelpDialog] Failed during component initialization.", ex);
        }
    }
}