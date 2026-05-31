using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Interfaces;
using SyncSession.Samples.Desktop;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Desktop.ViewModels;

/// <summary>
/// Manages the order list. Orders are create-only (no edit); delete via soft-delete.
/// Form is always visible.
/// </summary>
public partial class OrdersViewModel : ObservableObject
{
    private readonly IClientDatabase _db;
    private readonly AppSettings _settings;
    private readonly ILogger<OrdersViewModel> _logger;

    [ObservableProperty] private ObservableCollection<Order> _orders = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    public int TotalCount => Orders.Count;
    public int DirtyCount => Orders.Count(o => o.IsDirty);

    // Form fields
    [ObservableProperty] private string  _editCustomerId  = string.Empty;
    [ObservableProperty] private string  _editOrderNumber = string.Empty;
    [ObservableProperty] private decimal _editTotalAmount;

    public OrdersViewModel(IClientDatabase db, AppSettings settings, ILogger<OrdersViewModel> logger)
    {
        _db       = db;
        _settings = settings;
        _logger   = logger;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading    = true;
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            var rows = await conn.QueryAsync<Order>(
                "SELECT * FROM Orders WHERE IsDeleted = 0 ORDER BY OrderDate DESC");
            Orders = new ObservableCollection<Order>(rows);
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DirtyCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load orders");
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void NewOrder()
    {
        EditCustomerId  = string.Empty;
        EditOrderNumber = string.Empty;
        EditTotalAmount = 0;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        try
        {
            if (!Guid.TryParse(EditCustomerId, out var customerId))
            {
                ErrorMessage = "Invalid Customer ID — paste a valid GUID.";
                return;
            }

            var conn = await _db.GetConnectionAsync();
            var o = new Order
            {
                Id               = Guid.NewGuid(),
                TenantId         = _settings.TenantId ?? Guid.Empty,
                CustomerId       = customerId,
                OrderNumber      = EditOrderNumber,
                TotalAmount      = EditTotalAmount,
                OrderDate        = DateTime.UtcNow,
                Status           = "Pending",
                ModifiedByUserId = _settings.UserId,
                IsDirty          = true,
                ModifiedAtUtc    = DateTime.UtcNow
            };

            await conn.ExecuteAsync(
                @"INSERT INTO Orders (Id, TenantId, CustomerId, OrderNumber, TotalAmount, OrderDate,
                    Status, ModifiedByUserId, IsDirty, ModifiedAtUtc, IsDeleted)
                  VALUES (@Id, @TenantId, @CustomerId, @OrderNumber, @TotalAmount, @OrderDate,
                    @Status, @ModifiedByUserId, 1, @ModifiedAtUtc, 0)", o);

            await LoadAsync();
            NewOrderCommand.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save order");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(Order order)
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE Orders SET IsDeleted=1, IsDirty=1, ModifiedAtUtc=@Now WHERE Id=@Id",
                new { Now = DateTime.UtcNow, order.Id });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete order");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task MarkAllDirtyAsync()
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE Orders SET IsDirty=1, ModifiedAtUtc=@Now WHERE IsDeleted=0",
                new { Now = DateTime.UtcNow });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark all dirty");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CancelEdit() => NewOrderCommand.Execute(null);
}
