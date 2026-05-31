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
/// Manages the customer list and CRUD operations.
/// Form is always visible; selecting a row populates it for editing.
/// </summary>
public partial class CustomersViewModel : ObservableObject
{
    private readonly IClientDatabase _db;
    private readonly AppSettings _settings;
    private readonly ILogger<CustomersViewModel> _logger;

    [ObservableProperty] private ObservableCollection<Customer> _customers = [];
    [ObservableProperty] private Customer? _selectedCustomer;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    public int TotalCount => Customers.Count;
    public int DirtyCount => Customers.Count(c => c.IsDirty);

    // Form fields
    [ObservableProperty] private string _editName    = string.Empty;
    [ObservableProperty] private string _editEmail   = string.Empty;
    [ObservableProperty] private string _editPhone   = string.Empty;
    [ObservableProperty] private string _editAddress = string.Empty;

    // True when form holds an unsaved record (new or edited)
    public bool HasFormData => !string.IsNullOrWhiteSpace(EditName);

    public CustomersViewModel(IClientDatabase db, AppSettings settings, ILogger<CustomersViewModel> logger)
    {
        _db       = db;
        _settings = settings;
        _logger   = logger;
    }

    // Called by CommunityToolkit when SelectedCustomer changes
    partial void OnSelectedCustomerChanged(Customer? value)
    {
        if (value is null) return;
        EditName    = value.Name;
        EditEmail   = value.Email;
        EditPhone   = value.Phone    ?? string.Empty;
        EditAddress = value.Address  ?? string.Empty;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading    = true;
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            var rows = await conn.QueryAsync<Customer>(
                "SELECT * FROM Customers WHERE IsDeleted = 0 ORDER BY Name");
            Customers = new ObservableCollection<Customer>(rows);
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DirtyCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load customers");
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void NewCustomer()
    {
        SelectedCustomer = null;
        EditName         = string.Empty;
        EditEmail        = string.Empty;
        EditPhone        = string.Empty;
        EditAddress      = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            if (SelectedCustomer is null)
            {
                var c = new Customer
                {
                    Id               = Guid.NewGuid(),
                    TenantId         = _settings.TenantId ?? Guid.Empty,
                    Name             = EditName,
                    Email            = EditEmail,
                    Phone            = EditPhone,
                    Address          = EditAddress,
                    ModifiedByUserId = _settings.UserId,
                    IsDirty          = true,
                    ModifiedAtUtc    = DateTime.UtcNow
                };
                await conn.ExecuteAsync(
                    @"INSERT INTO Customers (Id, TenantId, Name, Email, Phone, Address,
                        ModifiedByUserId, IsDirty, ModifiedAtUtc, IsDeleted)
                      VALUES (@Id, @TenantId, @Name, @Email, @Phone, @Address,
                        @ModifiedByUserId, 1, @ModifiedAtUtc, 0)", c);
            }
            else
            {
                SelectedCustomer.Name             = EditName;
                SelectedCustomer.Email            = EditEmail;
                SelectedCustomer.Phone            = EditPhone;
                SelectedCustomer.Address          = EditAddress;
                SelectedCustomer.ModifiedByUserId = _settings.UserId;
                SelectedCustomer.IsDirty          = true;
                SelectedCustomer.ModifiedAtUtc    = DateTime.UtcNow;

                await conn.ExecuteAsync(
                    @"UPDATE Customers SET Name=@Name, Email=@Email, Phone=@Phone, Address=@Address,
                        ModifiedByUserId=@ModifiedByUserId, IsDirty=1, ModifiedAtUtc=@ModifiedAtUtc
                      WHERE Id=@Id", SelectedCustomer);
            }

            await LoadAsync();
            NewCustomerCommand.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save customer");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(Customer customer)
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE Customers SET IsDeleted=1, IsDirty=1, ModifiedAtUtc=@Now WHERE Id=@Id",
                new { Now = DateTime.UtcNow, customer.Id });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete customer");
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
                "UPDATE Customers SET IsDirty=1, ModifiedAtUtc=@Now WHERE IsDeleted=0",
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
    private void CancelEdit() => NewCustomerCommand.Execute(null);
}
