using System.Windows;

namespace WindowsPartitionManager.App;

public partial class DiskHealthWindow : Window
{
    public DiskHealthWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
