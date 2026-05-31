using System;
using System.Collections.Generic;
using System.Linq;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Shared.TestData;

/// <summary>
/// Helper class for generating test data with realistic values.
/// Provides methods to create customers, orders, and order items.
/// Shared across integration tests, benchmarks, and sample apps.
/// </summary>
public static class TestDataGenerator
{
    private static readonly Random _random = new();

    /// <summary>
    /// Generate a product with random or specified data
    /// </summary>
    public static Product CreateProduct(
        Guid? id = null,
        string? name = null,
        string? sku = null,
        decimal? price = null,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var productNames = new[] { "Widget", "Gadget", "Doohickey", "Thingamajig", "Whatsit", "Gizmo", "Contraption" };
        
        return new Product
        {
            Id = id ?? Guid.NewGuid(),
            Name = name ?? productNames[_random.Next(productNames.Length)],
            SKU = sku ?? $"SKU-{_random.Next(1000, 9999)}",
            Price = price ?? (decimal)(_random.NextDouble() * 100 + 10),
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = isDirty ? true : false,
            SyncSessionId = null
        };
    }

    /// <summary>
    /// Generate multiple products
    /// </summary>
    public static List<Product> CreateProducts(
        int count,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var products = new List<Product>();
        for (int i = 0; i < count; i++)
        {
            products.Add(CreateProduct(
                modifiedByUserId: modifiedByUserId,
                isDirty: isDirty));
        }
        return products;
    }

    /// <summary>
    /// Generate a customer with random or specified data
    /// </summary>
    public static Customer CreateCustomer(
        Guid? id = null,
        string? name = null,
        string? email = null,
        string? phone = null,
        string? address = null,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        return new Customer
        {
            Id = id ?? Guid.NewGuid(),
            Name = name ?? $"Customer_{Guid.NewGuid():N}".Substring(0, 20),
            Email = email ?? $"customer{_random.Next(1000, 9999)}@test.com",
            Phone = phone ?? $"+1-555-{_random.Next(100, 999)}-{_random.Next(1000, 9999)}",
            Address = address ?? $"{_random.Next(100, 999)} Test St, TestCity, TS {_random.Next(10000, 99999)}",
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = isDirty ? true : false,
            SyncSessionId = null
        };
    }

    /// <summary>
    /// Generate multiple customers
    /// </summary>
    public static List<Customer> CreateCustomers(
        int count,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var customers = new List<Customer>();
        for (int i = 0; i < count; i++)
        {
            customers.Add(CreateCustomer(
                modifiedByUserId: modifiedByUserId,
                isDirty: isDirty));
        }
        return customers;
    }

    /// <summary>
    /// Generate an order for a customer
    /// </summary>
    public static Order CreateOrder(
        Guid customerId,
        Guid? id = null,
        string? orderNumber = null,
        decimal? totalAmount = null,
        DateTime? orderDate = null,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        return new Order
        {
            Id = id ?? Guid.NewGuid(),
            CustomerId = customerId,
            OrderNumber = orderNumber ?? $"ORD-{_random.Next(10000, 99999)}",
            TotalAmount = totalAmount ?? (decimal)(_random.NextDouble() * 1000 + 50),
            OrderDate = orderDate ?? DateTime.UtcNow.AddDays(-_random.Next(0, 365)),
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = isDirty ? true : false,
            SyncSessionId = null
        };
    }

    /// <summary>
    /// Generate multiple orders for a customer
    /// </summary>
    public static List<Order> CreateOrders(
        Guid customerId,
        int count,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var orders = new List<Order>();
        for (int i = 0; i < count; i++)
        {
            orders.Add(CreateOrder(
                customerId,
                modifiedByUserId: modifiedByUserId,
                isDirty: isDirty));
        }
        return orders;
    }

    /// <summary>
    /// Generate an order item
    /// </summary>
    public static OrderItem CreateOrderItem(
        Guid orderId,
        Guid productId,
        Guid? id = null,
        string? productName = null,
        int? quantity = null,
        decimal? unitPrice = null,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var products = new[] { "Widget", "Gadget", "Doohickey", "Thingamajig", "Whatsit" };
        
        return new OrderItem
        {
            Id = id ?? Guid.NewGuid(),
            OrderId = orderId,
            ProductId = productId,
            ProductName = productName ?? products[_random.Next(products.Length)],
            Quantity = quantity ?? _random.Next(1, 10),
            UnitPrice = unitPrice ?? (decimal)(_random.NextDouble() * 100 + 10),
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = isDirty ? true : false,
            SyncSessionId = null
        };
    }

    /// <summary>
    /// Generate multiple order items for an order
    /// </summary>
    public static List<OrderItem> CreateOrderItems(
        Guid orderId,
        List<Guid> productIds,
        int count,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        if (productIds.Count == 0)
            throw new ArgumentException("Must provide at least one product ID");
            
        var items = new List<OrderItem>();
        for (int i = 0; i < count; i++)
        {
            var productId = productIds[i % productIds.Count];
            items.Add(CreateOrderItem(
                orderId,
                productId,
                modifiedByUserId: modifiedByUserId,
                isDirty: isDirty));
        }
        return items;
    }

    /// <summary>
    /// Generate a complete customer with orders and order items
    /// </summary>
    public static (Customer customer, List<Order> orders, List<OrderItem> orderItems, List<Product> products) CreateCustomerWithOrders(
        int orderCount = 2,
        int itemsPerOrder = 3,
        string modifiedByUserId = "TestUser",
        bool isDirty = false)
    {
        var customer = CreateCustomer(modifiedByUserId: modifiedByUserId, isDirty: isDirty);
        var orders = new List<Order>();
        var orderItems = new List<OrderItem>();
        var products = CreateProducts(Math.Max(5, itemsPerOrder), modifiedByUserId, isDirty);
        var productIds = products.Select(p => p.Id).ToList();

        for (int i = 0; i < orderCount; i++)
        {
            var order = CreateOrder(customer.Id, modifiedByUserId: modifiedByUserId, isDirty: isDirty);
            orders.Add(order);
            var items = CreateOrderItems(order.Id, productIds, itemsPerOrder, modifiedByUserId, isDirty);
            orderItems.AddRange(items);
        }

        return (customer, orders, orderItems, products);
    }

    #region Dictionary Creation Wrappers

    public static Dictionary<string, object?> CreateCustomerDict(
        Guid? id = null, string? name = null, string? email = null,
        string? phone = null, string? address = null,
        string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        var entity = CreateCustomer(id, name, email, phone, address, modifiedByUserId, isDirty);
        return EntityReflectionHelper.EntityToDictionary(entity);
    }

    public static List<Dictionary<string, object?>> CreateCustomersDict(
        int count, string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        return CreateCustomers(count, modifiedByUserId, isDirty)
            .Select(c => EntityReflectionHelper.EntityToDictionary(c))
            .ToList();
    }

    public static Dictionary<string, object?> CreateOrderDict(
        Guid customerId, Guid? id = null, string? orderNumber = null,
        decimal? totalAmount = null, DateTime? orderDate = null,
        string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        var entity = CreateOrder(customerId, id, orderNumber, totalAmount, orderDate, modifiedByUserId, isDirty);
        return EntityReflectionHelper.EntityToDictionary(entity);
    }

    public static List<Dictionary<string, object?>> CreateOrdersDict(
        Guid customerId, int count, string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        return CreateOrders(customerId, count, modifiedByUserId, isDirty)
            .Select(o => EntityReflectionHelper.EntityToDictionary(o))
            .ToList();
    }

    public static Dictionary<string, object?> CreateOrderItemDict(
        Guid orderId, Guid productId, Guid? id = null, string? productName = null,
        int? quantity = null, decimal? unitPrice = null,
        string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        var entity = CreateOrderItem(orderId, productId, id, productName, quantity, unitPrice, modifiedByUserId, isDirty);
        return EntityReflectionHelper.EntityToDictionary(entity);
    }

    public static List<Dictionary<string, object?>> CreateOrderItemsDict(
        Guid orderId, List<Guid> productIds, int count,
        string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        return CreateOrderItems(orderId, productIds, count, modifiedByUserId, isDirty)
            .Select(i => EntityReflectionHelper.EntityToDictionary(i))
            .ToList();
    }

    public static Dictionary<string, object?> CreateProductDict(
        Guid? id = null, string? name = null, string? sku = null,
        decimal? price = null, string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        var entity = CreateProduct(id, name, sku, price, modifiedByUserId, isDirty);
        return EntityReflectionHelper.EntityToDictionary(entity);
    }

    public static List<Dictionary<string, object?>> CreateProductsDict(
        int count, string modifiedByUserId = "TestUser", bool isDirty = false)
    {
        return CreateProducts(count, modifiedByUserId, isDirty)
            .Select(p => EntityReflectionHelper.EntityToDictionary(p))
            .ToList();
    }

    public static (
        List<Dictionary<string, object?>> Products,
        List<Dictionary<string, object?>> Customers,
        List<Dictionary<string, object?>> Orders,
        List<Dictionary<string, object?>> OrderItems)
        CreateValidDatasetDict(
            int customerCount = 5,
            int ordersPerCustomer = 2,
            int itemsPerOrder = 3)
    {
        var productDicts = new List<Dictionary<string, object?>>();
        var customerDicts = new List<Dictionary<string, object?>>();
        var orderDicts = new List<Dictionary<string, object?>>();
        var orderItemDicts = new List<Dictionary<string, object?>>();

        for (int i = 0; i < customerCount; i++)
        {
            var (c, o, oi, p) = CreateCustomerWithOrders(orderCount: ordersPerCustomer, itemsPerOrder: itemsPerOrder);
            productDicts.AddRange(p.Select(prod => EntityReflectionHelper.EntityToDictionary(prod)));
            customerDicts.Add(EntityReflectionHelper.EntityToDictionary(c));
            orderDicts.AddRange(o.Select(ord => EntityReflectionHelper.EntityToDictionary(ord)));
            orderItemDicts.AddRange(oi.Select(item => EntityReflectionHelper.EntityToDictionary(item)));
        }

        return (productDicts, customerDicts, orderDicts, orderItemDicts);
    }

    #endregion

    #region Invalid Data Generation for Constraint Testing

    public static Customer CreateInvalidCustomer(
        string violationType,
        Guid? duplicateId = null,
        string modifiedByUserId = "TestUser")
    {
        return violationType switch
        {
            "duplicate" => new Customer
            {
                Id = duplicateId ?? Guid.Empty,
                Name = "Duplicate Customer",
                Email = "duplicate@test.com",
                ModifiedByUserId = modifiedByUserId,
                ModifiedAtUtc = DateTime.UtcNow,
                IsDirty = false
            },

            "null-name" => new Customer
            {
                Id = Guid.NewGuid(),
                Name = null!,
                Email = "valid@test.com",
                ModifiedByUserId = modifiedByUserId,
                ModifiedAtUtc = DateTime.UtcNow,
                IsDirty = false
            },

            "null-email" => new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Valid Name",
                Email = null!,
                ModifiedByUserId = modifiedByUserId,
                ModifiedAtUtc = DateTime.UtcNow,
                IsDirty = false
            },

            "too-long-name" => new Customer
            {
                Id = Guid.NewGuid(),
                Name = new string('X', 300),
                Email = "valid@test.com",
                ModifiedByUserId = modifiedByUserId,
                ModifiedAtUtc = DateTime.UtcNow,
                IsDirty = false
            },

            "too-long-email" => new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Valid Name",
                Email = new string('e', 250) + "@test.com",
                ModifiedByUserId = modifiedByUserId,
                ModifiedAtUtc = DateTime.UtcNow,
                IsDirty = false
            },

            _ => throw new ArgumentException($"Unknown violation type: {violationType}")
        };
    }

    public static Order CreateOrderWithInvalidFK(
        Guid? nonExistentCustomerId = null,
        string modifiedByUserId = "TestUser")
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = nonExistentCustomerId ?? Guid.Empty,
            OrderNumber = $"ORD-{_random.Next(10000, 99999)}",
            TotalAmount = 100.00m,
            OrderDate = DateTime.UtcNow,
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = false
        };
    }

    public static OrderItem CreateOrderItemWithInvalidFK(
        Guid? nonExistentOrderId = null,
        Guid? validProductId = null,
        string modifiedByUserId = "TestUser")
    {
        return new OrderItem
        {
            Id = Guid.NewGuid(),
            OrderId = nonExistentOrderId ?? Guid.Empty,
            ProductId = validProductId ?? Guid.NewGuid(),
            ProductName = "Widget",
            Quantity = 5,
            UnitPrice = 19.99m,
            ModifiedByUserId = modifiedByUserId,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDirty = false
        };
    }

    public static List<Customer> CreateBatchWithInvalidCustomer(
        int totalCount,
        int invalidPosition,
        string violationType,
        Guid? duplicateId = null,
        string modifiedByUserId = "TestUser")
    {
        if (invalidPosition < 0 || invalidPosition >= totalCount)
            throw new ArgumentException($"Invalid position {invalidPosition} for batch size {totalCount}");

        var batch = new List<Customer>();
        for (int i = 0; i < totalCount; i++)
        {
            if (i == invalidPosition)
            {
                batch.Add(CreateInvalidCustomer(violationType, duplicateId, modifiedByUserId));
            }
            else
            {
                batch.Add(CreateCustomer(modifiedByUserId: modifiedByUserId));
            }
        }
        
        return batch;
    }

    #endregion
}
