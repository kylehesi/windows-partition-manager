using System.Windows;

namespace WindowsPartitionManager.App;

public partial class CheckVolumeWindow : Window
{
    public CheckVolumeWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
