using System.IO;
using System.Windows;
using Microsoft.Win32;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App;

public partial class CreateVirtualDiskWindow : Window
{
    public CreateVirtualDiskWindow()
    {
        InitializeComponent();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CreateVirtualDiskViewModel form)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = Localization.Get("Vhd_FileFilterVhdx"),
            DefaultExt = ".vhdx",
            AddExtension = true,
            OverwritePrompt = false, // the planner refuses existing files with a clearer message
            FileName = Path.GetFileName(form.Path),
            InitialDirectory = Path.GetDirectoryName(form.Path) is { } folder && Directory.Exists(folder) ? folder : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            form.Path = dialog.FileName;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
