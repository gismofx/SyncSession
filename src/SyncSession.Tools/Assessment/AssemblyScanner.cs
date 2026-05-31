using System.Reflection;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Assessment;

/// <summary>
/// Loads a consumer's assembly via MetadataLoadContext and scans for ISyncEntity types.
/// MetadataLoadContext is read-only and does not execute any code from the loaded assembly.
/// </summary>
public class AssemblyScanner
{
    private readonly string _assemblyPath;

    public AssemblyScanner(string assemblyPath) =>
        _assemblyPath = assemblyPath;

    public List<(string ClassName, string? TableName, bool IsMultiTenant)> ScanEntities()
    {
        var assemblyDir = Path.GetDirectoryName(_assemblyPath)!;

        var dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dlls.Add(_assemblyPath);
        dlls.Add(typeof(ISyncEntity).Assembly.Location); // always include Core

        try { foreach (var f in Directory.GetFiles(assemblyDir, "*.dll")) dlls.Add(f); }
        catch (UnauthorizedAccessException) { }

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        try { foreach (var f in Directory.GetFiles(runtimeDir, "*.dll")) dlls.Add(f); }
        catch (UnauthorizedAccessException) { }

        var resolver = new PathAssemblyResolver(dlls);
        using var mlc = new MetadataLoadContext(resolver);
        var assembly = mlc.LoadFromAssemblyPath(_assemblyPath);

        Type? syncEntityType = null;
        Type? multiTenantType = null;
        Type? syncTableAttrType = null;
        try
        {
            var coreAssembly = mlc.LoadFromAssemblyPath(typeof(ISyncEntity).Assembly.Location);
            syncEntityType    = coreAssembly.GetType(typeof(ISyncEntity).FullName!);
            multiTenantType   = coreAssembly.GetType(typeof(IMultiTenantSyncEntity).FullName!);
            syncTableAttrType = coreAssembly.GetType(typeof(SyncTableAttribute).FullName!);
        }
        catch { /* fall back to name-based matching */ }

        // Safe GetTypes() — recovers partial results when some types fail to load
        Type[] allTypes;
        try { allTypes = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        var results = new List<(string, string?, bool)>();

        foreach (var type in allTypes)
        {
            if (type.IsAbstract || type.IsInterface) continue;

            bool isSyncEntity;
            try
            {
                isSyncEntity = syncEntityType != null
                    ? ImplementsInterface(type, syncEntityType)
                    : type.GetInterfaces().Any(i => i.Name == "ISyncEntity");
            }
            catch { continue; }

            if (!isSyncEntity) continue;

            var tableName = GetSyncTableName(type, syncTableAttrType);

            bool isMultiTenant = false;
            try
            {
                isMultiTenant = multiTenantType != null
                    ? ImplementsInterface(type, multiTenantType)
                    : type.GetInterfaces().Any(i => i.Name == "IMultiTenantSyncEntity");
            }
            catch { }

            results.Add((type.Name, tableName, isMultiTenant));
        }

        return results;
    }

    private static bool ImplementsInterface(Type type, Type interfaceType) =>
        type.GetInterfaces().Any(i =>
            i.FullName == interfaceType.FullName || i.Name == interfaceType.Name);

    private static string? GetSyncTableName(Type type, Type? syncTableAttrType)
    {
        var attr = type.GetCustomAttributesData().FirstOrDefault(a =>
            (syncTableAttrType != null && a.AttributeType.FullName == syncTableAttrType.FullName)
            || a.AttributeType.Name == "SyncTableAttribute");

        if (attr != null)
        {
            var tableNameArg = attr.ConstructorArguments.FirstOrDefault();
            return tableNameArg.Value as string ?? type.Name;
        }

        return type.Name;
    }
}
