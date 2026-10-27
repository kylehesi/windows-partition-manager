using System.Windows;

namespace WindowsPartitionManager.App;

public partial class OptimizeVolumeWindow : Window
{
    public OptimizeVolumeWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
