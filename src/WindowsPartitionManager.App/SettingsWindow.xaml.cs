using System.Windows;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            new HistoryWindow { DataContext = new HistoryViewModel(settings.LogPath), Owner = this }.ShowDialog();
        }
    }
}
