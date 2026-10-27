using System.Windows;

namespace WindowsPartitionManager.App;

public partial class VolumePropertiesWindow : Window
{
    public VolumePropertiesWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
