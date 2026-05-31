using Avalonia.Controls;
using SyncSession.Samples.Desktop.ViewModels;

namespace SyncSession.Samples.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.Customers.LoadAsync();
                await vm.Products.LoadAsync();
                await vm.Orders.LoadAsync();
            }
        };
    }
}
