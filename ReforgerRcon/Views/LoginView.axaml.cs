using System;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[LoginView] Component initialization failed.", ex);
        }
    }
}