using System;
using LuminaUI.Controls;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views;

public partial class ConsoleWindow : LuminaWindow
{
    private readonly Action? _onReattach;

    public ConsoleWindow()
    {
        try
        {
            InitializeComponent();
            WindowStateStorageService.BindWindowPersistence(this, "ConsoleWindow");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed initializing ConsoleWindow component.", ex);
        }
    }

    public ConsoleWindow(ConsoleViewModel vm, Action onReattach) : this()
    {
        DataContext = vm;
        _onReattach = onReattach;
        Closed += (_, _) =>
        {
            try
            {
                _onReattach?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exception during console window reattach callback.", ex);
            }
        };
    }
}