using System.Windows;
using ProcBridge.Launcher.Wpf.ViewModels;

namespace ProcBridge.Launcher.Wpf.Views;

public partial class RemoteFolderPickerWindow : Window
{
    public RemoteFolderPickerWindow()
    {
        InitializeComponent();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteFolderPickerViewModel { Tree.SelectedNode: { HasError: false } })
            DialogResult = true;
        else
            MessageBox.Show(this, "Chọn một thư mục máy B cho phép duyệt.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
