using Avalonia.Controls;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views.Tabs;

public partial class SettingsTabView : UserControl
{
    public SettingsTabView()
    {
        InitializeComponent();
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is ComboBox comboBox && comboBox.SelectedItem is string selectedText)
        {
            vm.OnThemeSettingChanged(selectedText);
        }
    }
}