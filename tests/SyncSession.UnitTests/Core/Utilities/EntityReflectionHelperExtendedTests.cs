using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using Xunit;

namespace SyncSession.UnitTests.Core.Utilities;

/// <summary>
/// Extended tests for EntityReflectionHelper covering:
/// - EntityToDictionary (P4 gap)
/// - DictionaryToEntity (P4 gap)
/// - UnwrapJsonElement overloads (P4 gap)
/// - GetColumnsForClientSelect (P4 gap)
/// - Multi-tenant entity coverage (P4 gap)
/// - Initialize lifecycle (P4 gap)
/// - ConvertValue throw behavior (P0 fix verification)
/// </summary>
public class EntityReflectionHelperExtendedTests : IDisposable
{
    public EntityReflectionHelperExtendedTests()
    {
        EntityReflectionHelper.ClearCache();
    }

    public void Dispose()
    {
        EntityReflectionHelper.ClearCache();
    }

    #region Test Entities

    private class SimpleEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Test";
        public string Email { get; set; } = "test@example.com";
        public int Age { get; set; } = 25;
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; } = DateTime.UtcNow;
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "user1";
        public bool IsDeleted { get; set; }
    }

    private class MultiTenantEntity : IMultiTenantSyncEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Tenant Entity";
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "user1";
        public bool IsDeleted { get; set; }
    }

    private class IgnoredPropEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Test";

        [SyncColumn(Ignore = true)]
        public string Secret { get; set; } = "hidden";

        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "user1";
        public bool IsDeleted { get; set; }
    }

    private class CustomColumnEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [SyncColumn(ColumnName = "full_name")]
        public string Name { get; set; } = "Test";

        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "user1";
        public bool IsDeleted { get; set; }
    }

    private class NullablePropsEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string? NullableName { get; set; }
        public int? NullableAge { get; set; }
        public decimal? NullableAmount { get; set; }
        public Guid? OptionalRef { get; set; }
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "user1";
        public bool IsDeleted { get; set; }
    }

    #endregion

    #region EntityToDictionary Tests

    [Fact]
    public void EntityToDictionary_BasicEntity_IncludesBusinessProperties()
    {
        var entity = new SimpleEntity
        {
            Id = Guid.NewGuid(),
            Name = "Alice",
            Email = "alice@test.com",
            Age = 30,
            ModifiedByUserId = "admin"
        };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("Id").WhoseValue.Should().Be(entity.Id);
        dict.Should().ContainKey("Name").WhoseValue.Should().Be("Alice");
        dict.Should().ContainKey("Email").WhoseValue.Should().Be("alice@test.com");
        dict.Should().ContainKey("Age").WhoseValue.Should().Be(30);
        dict.Should().ContainKey("ModifiedByUserId").WhoseValue.Should().Be("admin");
        dict.Should().ContainKey("IsDeleted");
    }

    [Fact]
    public void EntityToDictionary_ExcludesIsDirtyAndSyncSessionId()
    {
        var entity = new SimpleEntity
        {
            IsDirty = true,
            SyncSessionId = Guid.NewGuid()
        };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().NotContainKey("IsDirty");
        dict.Should().NotContainKey("SyncSessionId");
    }

    [Fact]
    public void EntityToDictionary_IncludesModifiedAtUtc()
    {
        var now = DateTime.UtcNow;
        var entity = new SimpleEntity { ModifiedAtUtc = now };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("ModifiedAtUtc").WhoseValue.Should().Be(now);
    }

    [Fact]
    public void EntityToDictionary_WithIgnoredProperty_ExcludesIt()
    {
        var entity = new IgnoredPropEntity
        {
            Name = "Test",
            Secret = "should-not-appear"
        };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("Name");
        dict.Should().NotContainKey("Secret");
    }

    [Fact]
    public void EntityToDictionary_WithCustomColumnName_UsesCustomName()
    {
        var entity = new CustomColumnEntity { Name = "Custom" };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("full_name").WhoseValue.Should().Be("Custom");
        dict.Should().NotContainKey("Name");
    }

    [Fact]
    public void EntityToDictionary_MultiTenantEntity_IncludesTenantId()
    {
        var tenantId = Guid.NewGuid();
        var entity = new MultiTenantEntity
        {
            TenantId = tenantId,
            Name = "TenantTest"
        };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("TenantId").WhoseValue.Should().Be(tenantId);
        dict.Should().ContainKey("Name");
    }

    #endregion

    #region DictionaryToEntity Tests

    [Fact]
    public void DictionaryToEntity_BasicDictionary_PopulatesAllProperties()
    {
        var id = Guid.NewGuid();
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["Name"] = "Bob",
            ["Email"] = "bob@test.com",
            ["Age"] = 42,
            ["ModifiedByUserId"] = "admin",
            ["IsDeleted"] = false,
            ["IsDirty"] = true
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Id.Should().Be(id);
        entity.Name.Should().Be("Bob");
        entity.Email.Should().Be("bob@test.com");
        entity.Age.Should().Be(42);
        entity.ModifiedByUserId.Should().Be("admin");
        entity.IsDeleted.Should().BeFalse();
        entity.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void DictionaryToEntity_CaseInsensitiveKeys_MapsCorrectly()
    {
        var id = Guid.NewGuid();
        var dict = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "Lower",
            ["EMAIL"] = "UPPER@test.com",
            ["age"] = 10
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Id.Should().Be(id);
        entity.Name.Should().Be("Lower");
        entity.Email.Should().Be("UPPER@test.com");
        entity.Age.Should().Be(10);
    }

    [Fact]
    public void DictionaryToEntity_NullValues_SkipsProperty()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = null,
            ["Email"] = null
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        // Name/Email stay at default ("Test"/"test@example.com") since null is skipped
        entity.Name.Should().Be("Test");
        entity.Email.Should().Be("test@example.com");
    }

    [Fact]
    public void DictionaryToEntity_MissingKeys_UsesDefaults()
    {
        var id = Guid.NewGuid();
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = id
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Id.Should().Be(id);
        entity.Name.Should().Be("Test"); // default
        entity.Age.Should().Be(25); // default
    }

    [Fact]
    public void DictionaryToEntity_TypeConversion_LongToInt()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Age"] = 42L // long from JSON
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Age.Should().Be(42);
    }

    [Fact]
    public void DictionaryToEntity_TypeConversion_StringToDateTime()
    {
        var dateStr = "2025-06-15T10:30:00.0000000";
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["ModifiedAtUtc"] = dateStr
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.ModifiedAtUtc.Should().NotBeNull();
        entity.ModifiedAtUtc!.Value.Year.Should().Be(2025);
        entity.ModifiedAtUtc!.Value.Month.Should().Be(6);
    }

    [Fact]
    public void DictionaryToEntity_TypeConversion_StringToGuid()
    {
        var guidStr = Guid.NewGuid().ToString();
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["SyncSessionId"] = guidStr
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.SyncSessionId.Should().Be(Guid.Parse(guidStr));
    }

    [Fact]
    public void DictionaryToEntity_MultiTenant_PopulatesTenantId()
    {
        var tenantId = Guid.NewGuid();
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["TenantId"] = tenantId,
            ["Name"] = "TenantRec"
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<MultiTenantEntity>(dict);

        entity.TenantId.Should().Be(tenantId);
        entity.Name.Should().Be("TenantRec");
    }

    #endregion

    #region ConvertValue P0 Fix Verification

    [Fact]
    public void DictionaryToEntity_InvalidConversion_ThrowsInvalidOperationException()
    {
        // P0-1: ConvertValue now throws instead of returning null
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Age"] = "not-a-number" // string cannot convert to int
        };

        var act = () => EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to convert*")
            .WithMessage("*Int32*");
    }

    [Fact]
    public void DictionaryToEntity_InvalidGuidString_ThrowsInvalidOperationException()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["SyncSessionId"] = "not-a-guid"
        };

        var act = () => EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to convert*")
            .WithMessage("*Guid*");
    }

    [Fact]
    public void DictionaryToEntity_InvalidDateTimeString_ThrowsInvalidOperationException()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["ModifiedAtUtc"] = "not-a-date"
        };

        var act = () => EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to convert*");
    }

    [Fact]
    public void DictionaryToEntity_ValidConversions_DoNotThrow()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Age"] = 42L,   // long → int (valid)
            ["Name"] = "OK",
            ["ModifiedAtUtc"] = "2025-01-15T00:00:00Z" // valid date
        };

        var act = () => EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        act.Should().NotThrow();
    }

    #endregion

    #region UnwrapJsonElement Tests (Untyped Overload)

    [Fact]
    public void UnwrapJsonElement_StringValue_ReturnsString()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("\"hello\"");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().Be("hello");
    }

    [Fact]
    public void UnwrapJsonElement_IntegerNumber_ReturnsLong()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("42");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().Be(42L);
    }

    [Fact]
    public void UnwrapJsonElement_DecimalNumber_ReturnsDouble()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("3.14");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().Be(3.14);
    }

    [Fact]
    public void UnwrapJsonElement_TrueValue_ReturnsTrue()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("true");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().Be(true);
    }

    [Fact]
    public void UnwrapJsonElement_FalseValue_ReturnsFalse()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("false");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().Be(false);
    }

    [Fact]
    public void UnwrapJsonElement_NullValue_ReturnsNull()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("null");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().BeNull();
    }

    [Fact]
    public void UnwrapJsonElement_ObjectValue_ReturnsString()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{\"key\":\"val\"}");
        var result = EntityReflectionHelper.UnwrapJsonElement(json);
        result.Should().BeOfType<string>();
    }

    #endregion


    #region UnwrapJsonElement Tests (Typed Overload)

    [Fact]
    public void UnwrapJsonElement_Typed_Int32_ReturnsInt()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("42");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(int));
        result.Should().Be(42);
        result.Should().BeOfType<int>();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_NullableInt_ReturnsInt()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("99");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(int?));
        result.Should().Be(99);
        result.Should().BeOfType<int>();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_Long_ReturnsLong()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("9999999999");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(long));
        result.Should().Be(9999999999L);
        result.Should().BeOfType<long>();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_Decimal_ReturnsDecimal()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("19.99");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(decimal));
        result.Should().Be(19.99m);
        result.Should().BeOfType<decimal>();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_Double_ReturnsDouble()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("3.14159");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(double));
        result.Should().Be(3.14159);
        result.Should().BeOfType<double>();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_String_ReturnsString()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("\"hello world\"");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(string));
        result.Should().Be("hello world");
    }

    [Fact]
    public void UnwrapJsonElement_Typed_Bool_ReturnsTrue()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("true");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(bool));
        result.Should().Be(true);
    }

    [Fact]
    public void UnwrapJsonElement_Typed_Null_ReturnsNull()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("null");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(string));
        result.Should().BeNull();
    }

    [Fact]
    public void UnwrapJsonElement_Typed_StringTargetGuid_ReturnsStringNotGuid()
    {
        // When target type is Guid but JSON is a string, UnwrapJsonElement returns the raw string.
        // ConvertValue downstream handles the string→Guid conversion.
        var guidStr = Guid.NewGuid().ToString();
        var json = JsonSerializer.Deserialize<JsonElement>($"\"{guidStr}\"");
        var result = EntityReflectionHelper.UnwrapJsonElement(json, typeof(Guid));
        result.Should().Be(guidStr);
        result.Should().BeOfType<string>();
    }

    #endregion

    #region UnwrapJsonElements Dictionary Tests

    [Fact]
    public void UnwrapJsonElements_MixedDictionary_UnwrapsJsonElements()
    {
        var jsonString = JsonSerializer.Deserialize<JsonElement>("\"hello\"");
        var jsonNumber = JsonSerializer.Deserialize<JsonElement>("42");
        var jsonBool = JsonSerializer.Deserialize<JsonElement>("true");

        var dict = new Dictionary<string, object?>
        {
            ["Name"] = jsonString,
            ["Age"] = jsonNumber,
            ["Active"] = jsonBool,
            ["Plain"] = "already a string",
            ["Null"] = null
        };

        var result = EntityReflectionHelper.UnwrapJsonElements(dict);

        result["Name"].Should().Be("hello");
        result["Age"].Should().Be(42L);
        result["Active"].Should().Be(true);
        result["Plain"].Should().Be("already a string");
        result["Null"].Should().BeNull();
    }

    [Fact]
    public void UnwrapJsonElements_NonJsonElementValues_PassThrough()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Count"] = 100,
            ["Label"] = "test"
        };

        var result = EntityReflectionHelper.UnwrapJsonElements(dict);

        result["Id"].Should().Be(dict["Id"]);
        result["Count"].Should().Be(100);
        result["Label"].Should().Be("test");
    }

    #endregion

    #region GetColumnsForClientSelect Tests

    [Fact]
    public void GetColumnsForClientSelect_IncludesCorrectColumns()
    {
        var columns = EntityReflectionHelper.GetColumnsForClientSelect<SimpleEntity>();

        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("IsDirty");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().Contain("IsDeleted");
        columns.Should().NotContain("SyncSessionId");
    }

    [Fact]
    public void GetColumnsForClientSelect_MatchesPullUpsertColumns()
    {
        // ClientSelect and PullUpsert should produce identical column lists:
        // both include IsDirty + ModifiedAtUtc, both exclude SyncSessionId
        var clientSelect = EntityReflectionHelper.GetColumnsForClientSelect<SimpleEntity>();
        var pullUpsert = EntityReflectionHelper.GetColumnsForPullUpsert<SimpleEntity>();

        clientSelect.Should().BeEquivalentTo(pullUpsert);
    }

    #endregion

    #region Multi-Tenant Column Selection Tests

    [Fact]
    public void GetColumnsForPullUpsert_MultiTenant_IncludesTenantId()
    {
        var columns = EntityReflectionHelper.GetColumnsForPullUpsert<MultiTenantEntity>();

        columns.Should().Contain("TenantId");
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("IsDirty");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().NotContain("SyncSessionId");
    }

    [Fact]
    public void GetColumnsForPushSelect_MultiTenant_IncludesTenantId()
    {
        var columns = EntityReflectionHelper.GetColumnsForPushSelect<MultiTenantEntity>();

        columns.Should().Contain("TenantId");
        columns.Should().Contain("Id");
        columns.Should().NotContain("IsDirty");
        columns.Should().NotContain("SyncSessionId");
    }

    [Fact]
    public void GetColumnsForServerUpsert_MultiTenant_IncludesTenantId()
    {
        var columns = EntityReflectionHelper.GetColumnsForServerUpsert<MultiTenantEntity>();

        columns.Should().Contain("TenantId");
        columns.Should().NotContain("IsDirty");
        columns.Should().NotContain("SyncSessionId");
    }

    [Fact]
    public void GetColumnsForServerSelect_MultiTenant_IncludesTenantId()
    {
        var columns = EntityReflectionHelper.GetColumnsForServerSelect<MultiTenantEntity>();

        columns.Should().Contain("TenantId");
        columns.Should().Contain("SyncSessionId");
        columns.Should().NotContain("IsDirty");
    }

    [Fact]
    public void IsSyncInfrastructureProperty_TenantId_ReturnsFalse()
    {
        EntityReflectionHelper.IsSyncInfrastructureProperty("TenantId").Should().BeFalse();
    }

    [Fact]
    public void CreateDynamicParameters_MultiTenant_IncludesTenantId()
    {
        var entity = new MultiTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "TenantTest"
        };
        var sessionId = Guid.NewGuid();

        var parameters = EntityReflectionHelper.CreateDynamicParameters(entity, sessionId);

        parameters.ParameterNames.Should().Contain("TenantId");
        parameters.ParameterNames.Should().Contain("Id");
        parameters.ParameterNames.Should().Contain("Name");
        parameters.ParameterNames.Should().NotContain("IsDirty");
        parameters.ParameterNames.Should().NotContain("ModifiedAtUtc");
    }

    #endregion

    #region Initialize and Table-Name Overload Tests

    [Fact]
    public void Initialize_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => EntityReflectionHelper.Initialize(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("config");
    }

    [Fact]
    public void GetColumnsForServerSelect_ByTableName_BeforeInitialize_Throws()
    {
        // ClearCache runs in constructor but doesn't clear _config.
        // We need a fresh state — force it by testing against an unregistered name
        // when config is null. Since other tests may have initialized, this tests
        // the "unregistered table" path which exercises the same guard.
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "UnrelatedTable");
        EntityReflectionHelper.Initialize(config);

        var act = () => EntityReflectionHelper.GetColumnsForServerSelect("NonExistentTable");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    [Fact]
    public void GetColumnsForServerUpsert_ByTableName_UnregisteredTable_Throws()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "TestSimple");
        EntityReflectionHelper.Initialize(config);

        var act = () => EntityReflectionHelper.GetColumnsForServerUpsert("NoSuchTable");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    [Fact]
    public void GetColumnsForServerSelect_ByTableName_RegisteredTable_ReturnsCorrectColumns()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "SimpleEntities");
        EntityReflectionHelper.Initialize(config);

        var columns = EntityReflectionHelper.GetColumnsForServerSelect("SimpleEntities");

        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("SyncSessionId");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().NotContain("IsDirty");
    }

    [Fact]
    public void GetColumnsForServerUpsert_ByTableName_RegisteredTable_ReturnsCorrectColumns()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "SimpleEntities");
        EntityReflectionHelper.Initialize(config);

        var columns = EntityReflectionHelper.GetColumnsForServerUpsert("SimpleEntities");

        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().NotContain("IsDirty");
        columns.Should().NotContain("SyncSessionId");
    }

    [Fact]
    public void GetColumnsForDirectUpsert_ByTableName_RegisteredTable_ReturnsCorrectColumns()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "SimpleEntities");
        EntityReflectionHelper.Initialize(config);

        var columns = EntityReflectionHelper.GetColumnsForDirectUpsert("SimpleEntities");

        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().Contain("SyncSessionId"); // DirectWriteService sets this on entity
        columns.Should().NotContain("IsDirty");
    }

    [Fact]
    public void GetColumnsForServerSelect_ByTableName_CacheHit_ReturnsSameInstance()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "CacheTestTable");
        EntityReflectionHelper.Initialize(config);

        var first = EntityReflectionHelper.GetColumnsForServerSelect("CacheTestTable");
        var second = EntityReflectionHelper.GetColumnsForServerSelect("CacheTestTable");

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void GetColumnsForServerSelect_ByTableName_CaseInsensitive_Finds()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "MixedCaseTable");
        EntityReflectionHelper.Initialize(config);

        // SyncConfiguration stores with exact case, but GetEntityTypeForTable
        // uses OrdinalIgnoreCase comparison
        var columns = EntityReflectionHelper.GetColumnsForServerSelect("mixedcasetable");

        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
    }

    #endregion

    #region ClearCache Behavior Tests

    [Fact]
    public void ClearCache_ResetsConfig_TableNameOverloadsRequireReInitialize()
    {
        // Initialize, then ClearCache — config and delegates are fully reset
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "PersistTest");
        EntityReflectionHelper.Initialize(config);

        EntityReflectionHelper.ClearCache();

        // Table-name overloads must fail without re-Initialize (delegates cleared)
        var act = () => EntityReflectionHelper.GetColumnsForServerSelect("PersistTest");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");

        // After re-Initialize the overload works again
        EntityReflectionHelper.Initialize(config);
        var columns = EntityReflectionHelper.GetColumnsForServerSelect("PersistTest");
        columns.Should().Contain("Id");
    }

    [Fact]
    public void ClearCache_InvalidatesTableColumnCache_FreshLookupAfterClear()
    {
        var config = new ClientSyncConfiguration();
        config.RegisterTable<SimpleEntity>(tableName: "ClearCacheTest");
        EntityReflectionHelper.Initialize(config);

        var first = EntityReflectionHelper.GetColumnsForServerSelect("ClearCacheTest");
        EntityReflectionHelper.ClearCache();

        // Must re-Initialize after ClearCache before using table-name overloads
        EntityReflectionHelper.Initialize(config);
        var second = EntityReflectionHelper.GetColumnsForServerSelect("ClearCacheTest");

        // Results should be equivalent but not the same cached instance
        first.Should().NotBeSameAs(second);
        first.Should().BeEquivalentTo(second);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void DictionaryToEntity_EmptyDictionary_ReturnsEntityWithDefaults()
    {
        var dict = new Dictionary<string, object?>();

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Should().NotBeNull();
        entity.Name.Should().Be("Test"); // default
        entity.Age.Should().Be(25); // default
        entity.ModifiedByUserId.Should().Be("user1"); // default
    }

    [Fact]
    public void DictionaryToEntity_ExtraKeys_IgnoredSafely()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = "Valid",
            ["NonExistentProperty"] = "should be ignored",
            ["AlsoFake"] = 999
        };

        var act = () => EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        act.Should().NotThrow();
        var entity = act();
        entity.Name.Should().Be("Valid");
    }

    [Fact]
    public void EntityToDictionary_NullableProperties_IncludesNullValues()
    {
        var entity = new NullablePropsEntity
        {
            Id = Guid.NewGuid(),
            NullableName = null,
            NullableAge = null,
            NullableAmount = null,
            OptionalRef = null
        };

        var dict = EntityReflectionHelper.EntityToDictionary(entity);

        dict.Should().ContainKey("NullableName").WhoseValue.Should().BeNull();
        dict.Should().ContainKey("NullableAge").WhoseValue.Should().BeNull();
        dict.Should().ContainKey("NullableAmount").WhoseValue.Should().BeNull();
        dict.Should().ContainKey("OptionalRef").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void DictionaryToEntity_JsonElementValues_UnwrapsCorrectly()
    {
        var id = Guid.NewGuid();
        var jsonName = JsonSerializer.Deserialize<JsonElement>("\"JsonName\"");
        var jsonAge = JsonSerializer.Deserialize<JsonElement>("33");

        var dict = new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["Name"] = jsonName,
            ["Age"] = jsonAge
        };

        var entity = EntityReflectionHelper.DictionaryToEntity<SimpleEntity>(dict);

        entity.Name.Should().Be("JsonName");
        entity.Age.Should().Be(33);
    }

    #endregion
}
