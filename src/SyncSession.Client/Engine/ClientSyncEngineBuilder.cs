using System;
using System.Reflection;
using SyncSession.Client.Handlers;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Engine;

/// <summary>
/// Builder for creating ClientSyncEngine with automatic table discovery and handler registration.
/// Eliminates manual configuration by scanning assemblies for entities marked with [SyncTable].
/// </summary>
public static class ClientSyncEngineBuilder
{
    /// <summary>
    /// Build a <see cref="ClientSyncEngine"/> using only the tables already registered in
    /// <paramref name="configuration"/>. No assembly scanning is performed — use this when
    /// you want full control over which tables are synced.
    /// </summary>
    /// <param name="clientDatabase">Client database implementation.</param>
    /// <param name="serverClient">Server API client implementation.</param>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="configuration">Fully configured <see cref="ClientSyncConfiguration"/> with tables already registered.</param>
    /// <returns>Fully configured <see cref="ClientSyncEngine"/> ready for synchronization.</returns>
    public static ClientSyncEngine Build(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        Guid deviceId,
        ClientSyncConfiguration configuration)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        return BuildCore(clientDatabase, serverClient, deviceId, configuration);
    }

    /// <summary>
    /// Build a <see cref="ClientSyncEngine"/> with automatic table discovery from a single assembly.
    /// </summary>
    /// <param name="clientDatabase">Client database implementation.</param>
    /// <param name="serverClient">Server API client implementation.</param>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="configuration">Optional client sync configuration (uses defaults if null).</param>
    /// <param name="entitiesAssembly">Assembly containing entity types marked with <c>[SyncTable]</c>.</param>
    /// <returns>Fully configured <see cref="ClientSyncEngine"/> ready for synchronization.</returns>
    public static ClientSyncEngine Build(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        Guid deviceId,
        ClientSyncConfiguration? configuration,
        Assembly entitiesAssembly)
    {
        if (entitiesAssembly == null)
            throw new ArgumentNullException(nameof(entitiesAssembly));

        return Build(clientDatabase, serverClient, deviceId, configuration, new[] { entitiesAssembly });
    }

    /// <summary>
    /// Build a <see cref="ClientSyncEngine"/> with automatic table discovery from multiple assemblies.
    /// </summary>
    /// <param name="clientDatabase">Client database implementation.</param>
    /// <param name="serverClient">Server API client implementation.</param>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="configuration">Optional client sync configuration (uses defaults if null).</param>
    /// <param name="assemblies">Assemblies containing entity types marked with <c>[SyncTable]</c>.</param>
    /// <returns>Fully configured <see cref="ClientSyncEngine"/> ready for synchronization.</returns>
    public static ClientSyncEngine Build(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        Guid deviceId,
        ClientSyncConfiguration? configuration = null,
        params Assembly[] assemblies)
    {
        if (assemblies == null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided", nameof(assemblies));

        configuration ??= new ClientSyncConfiguration();

        foreach (var assembly in assemblies)
            configuration.DiscoverAndRegisterTables(assembly);

        return BuildCore(clientDatabase, serverClient, deviceId, configuration);
    }

    private static ClientSyncEngine BuildCore(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        Guid deviceId,
        ClientSyncConfiguration configuration)
    {
        configuration.Validate();

        var factory = new TableSyncHandlerFactory(clientDatabase, serverClient, configuration);
        foreach (var table in configuration.GetTables())
            table.Handler = factory.CreateHandler(table);

        return new ClientSyncEngine(clientDatabase, serverClient, deviceId, configuration);
    }
}
