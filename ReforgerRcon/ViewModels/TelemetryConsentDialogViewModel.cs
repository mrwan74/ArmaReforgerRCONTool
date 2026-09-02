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
        AppLogger.Info("[TelemetryConsentDialog:Decision] User accepted diagnostic telemetry reporting.");
        _onDecision(true);
    }

    [RelayCommand]
    private void Decline()
    {
        AppLogger.Info("[TelemetryConsentDialog:Decision] User declined diagnostic telemetry reporting.");
        _onDecision(false);
    }
}