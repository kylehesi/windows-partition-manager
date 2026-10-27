using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App;

public partial class SpaceAnalysisWindow : Window
{
    public SpaceAnalysisWindow()
    {
        InitializeComponent();
    }

    /// <summary>Double-click opens a folder, or shows a file in Explorer.</summary>
    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(sender);

    /// <summary>Enter opens, Backspace goes up, like in Explorer.</summary>
    private void Items_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SpaceAnalysisViewModel space)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenSelected(sender);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && space.UpCommand.CanExecute(null))
        {
            space.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenSelected(object sender)
    {
        if (DataContext is SpaceAnalysisViewModel space && sender is ListBox { SelectedItem: SpaceItem item } && space.OpenCommand.CanExecute(item))
        {
            space.OpenCommand.Execute(item);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
