using CommunityToolkit.Mvvm.ComponentModel;

namespace SyncSession.Samples.Desktop.ViewModels;

/// <summary>
/// Root view model hosting the tab navigation.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public CustomersViewModel Customers { get; }
    public ProductsViewModel Products { get; }
    public OrdersViewModel Orders { get; }
    public SyncStatusViewModel SyncStatus { get; }
    public SyncLogViewModel SyncLog { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainWindowViewModel(
        CustomersViewModel customers,
        ProductsViewModel products,
        OrdersViewModel orders,
        SyncStatusViewModel syncStatus,
        SyncLogViewModel syncLog)
    {
        Customers  = customers;
        Products   = products;
        Orders     = orders;
        SyncStatus = syncStatus;
        SyncLog    = syncLog;

        // Wire sync completion → refresh all entity tabs
        SyncStatus.SyncCompleted += OnSyncCompleted;
    }

    private async void OnSyncCompleted()
    {
        await Customers.LoadAsync();
        await Products.LoadAsync();
        await Orders.LoadAsync();
        if (SyncLog.IsOnline)
            await SyncLog.LoadAsync();
    }
}
