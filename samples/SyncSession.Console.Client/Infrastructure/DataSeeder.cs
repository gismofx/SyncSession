using System;
using System.Collections.Generic;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Console.Infrastructure;

/// <summary>
/// Generates realistic sample customer data for demonstrations
/// </summary>
public class DataSeeder
{
    private static readonly string[] FirstNames = new[]
    {
        "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda",
        "William", "Barbara", "David", "Elizabeth", "Richard", "Susan", "Joseph", "Jessica",
        "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Nancy", "Daniel", "Lisa"
    };

    private static readonly string[] LastNames = new[]
    {
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas"
    };

    private readonly Random _random;

    public DataSeeder(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>
    /// Generate sample customers
    /// </summary>
    public List<Customer> GenerateCustomers(int count, Guid tenantId)
    {
        var customers = new List<Customer>();
        for (int i = 0; i < count; i++)
        {
            var firstName = FirstNames[_random.Next(FirstNames.Length)];
            var lastName = LastNames[_random.Next(LastNames.Length)];
            var email = $"{firstName.ToLower()}.{lastName.ToLower()}@example.com";

            customers.Add(new Customer
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = $"{firstName} {lastName}",
                Email = email,
                IsDirty = true,  // Needs to be synced
                ModifiedAtUtc = DateTime.UtcNow,
                ModifiedByUserId = "DemoUser"
            });
        }

        return customers;
    }
}
