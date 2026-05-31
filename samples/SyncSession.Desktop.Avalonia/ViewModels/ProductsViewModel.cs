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
/// Manages the product list and CRUD operations.
/// Form is always visible; selecting a row populates it for editing.
/// </summary>
public partial class ProductsViewModel : ObservableObject
{
    private readonly IClientDatabase _db;
    private readonly AppSettings _settings;
    private readonly ILogger<ProductsViewModel> _logger;

    [ObservableProperty] private ObservableCollection<Product> _products = [];
    [ObservableProperty] private Product? _selectedProduct;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    public int TotalCount => Products.Count;
    public int DirtyCount => Products.Count(p => p.IsDirty);

    // Form fields
    [ObservableProperty] private string  _editName  = string.Empty;
    [ObservableProperty] private string  _editSku   = string.Empty;
    [ObservableProperty] private decimal _editPrice;

    public ProductsViewModel(IClientDatabase db, AppSettings settings, ILogger<ProductsViewModel> logger)
    {
        _db       = db;
        _settings = settings;
        _logger   = logger;
    }

    partial void OnSelectedProductChanged(Product? value)
    {
        if (value is null) return;
        EditName  = value.Name;
        EditSku   = value.SKU;
        EditPrice = value.Price;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading    = true;
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            var rows = await conn.QueryAsync<Product>(
                "SELECT * FROM Products WHERE IsDeleted = 0 ORDER BY Name");
            Products = new ObservableCollection<Product>(rows);
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DirtyCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load products");
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void NewProduct()
    {
        SelectedProduct = null;
        EditName        = string.Empty;
        EditSku         = string.Empty;
        EditPrice       = 0;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            if (SelectedProduct is null)
            {
                var p = new Product
                {
                    Id               = Guid.NewGuid(),
                    Name             = EditName,
                    SKU              = EditSku,
                    Price            = EditPrice,
                    ModifiedByUserId = _settings.UserId,
                    IsDirty          = true,
                    ModifiedAtUtc    = DateTime.UtcNow
                };
                await conn.ExecuteAsync(
                    @"INSERT INTO Products (Id, Name, SKU, Price, ModifiedByUserId, IsDirty, ModifiedAtUtc, IsDeleted)
                      VALUES (@Id, @Name, @SKU, @Price, @ModifiedByUserId, 1, @ModifiedAtUtc, 0)", p);
            }
            else
            {
                SelectedProduct.Name             = EditName;
                SelectedProduct.SKU              = EditSku;
                SelectedProduct.Price            = EditPrice;
                SelectedProduct.ModifiedByUserId = _settings.UserId;
                SelectedProduct.IsDirty          = true;
                SelectedProduct.ModifiedAtUtc    = DateTime.UtcNow;

                await conn.ExecuteAsync(
                    @"UPDATE Products SET Name=@Name, SKU=@SKU, Price=@Price,
                        ModifiedByUserId=@ModifiedByUserId, IsDirty=1, ModifiedAtUtc=@ModifiedAtUtc
                      WHERE Id=@Id", SelectedProduct);
            }

            await LoadAsync();
            NewProductCommand.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save product");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(Product product)
    {
        ErrorMessage = null;
        try
        {
            var conn = await _db.GetConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE Products SET IsDeleted=1, IsDirty=1, ModifiedAtUtc=@Now WHERE Id=@Id",
                new { Now = DateTime.UtcNow, product.Id });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete product");
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
                "UPDATE Products SET IsDirty=1, ModifiedAtUtc=@Now WHERE IsDeleted=0",
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
    private void CancelEdit() => NewProductCommand.Execute(null);
}
