using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        try
        {
            InitializeComponent();

            if (this.FindControl<TextBox>("PortInputBox") is { } portBox)
            {
                portBox.AddHandler(TextInputEvent, OnPortTextInput, RoutingStrategies.Tunnel);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[LoginView] Component initialization failed.", ex);
        }
    }

    private static void OnPortTextInput(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsDigit))
        {
            e.Handled = true;
        }
    }
}