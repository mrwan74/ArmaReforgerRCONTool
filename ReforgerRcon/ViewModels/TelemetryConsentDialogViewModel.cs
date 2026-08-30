using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class TelemetryConsentDialogViewModel(Action<bool> onDecision) : ViewModelBase
{
    private readonly Action<bool> _onDecision = onDecision;

    [RelayCommand]
    private void Enable()
    {
        AppLogger.Info("[TelemetryConsentDialog] User accepted anonymous diagnostic telemetry.");
        _onDecision(true);
    }

    [RelayCommand]
    private void Decline()
    {
        AppLogger.Info("[TelemetryConsentDialog] User declined anonymous diagnostic telemetry.");
        _onDecision(false);
    }
}