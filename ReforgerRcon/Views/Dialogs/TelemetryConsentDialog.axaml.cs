using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class TelemetryConsentDialog : UserControl
{
    public TelemetryConsentDialog()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[TelemetryConsentDialog] Failed during component initialization.", ex);
        }
    }
}