using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Samples.Shared.Entities;
/// <summary>
/// OrderItem line item - references Order and Product.
/// </summary>
[SyncTable("OrderItems", Priority = 3)]
public class OrderItem : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public Guid OrderId { get; set; } = Guid.Empty;
    public Guid ProductId { get; set; } = Guid.Empty;  // FK to Product
    public string ProductName { get; set; } = string.Empty;  // Denormalized for display
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    // ISyncEntity properties
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "TestUser";
    public bool IsDeleted { get; set; } = false;
}
