using System;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Handlers;

/// <summary>
/// Factory for creating strongly-typed table sync handlers.
/// Uses reflection once at construction to create typed handler instances,
/// eliminating reflection from the sync hot path.
/// </summary>
public class TableSyncHandlerFactory
{
    private readonly IClientDatabase _clientDb;
    private readonly ISyncServerApi _serverClient;
    private readonly ClientSyncConfiguration _config;

    /// <summary>
    /// Initializes a new instance of <see cref="TableSyncHandlerFactory"/>.
    /// </summary>
    /// <param name="clientDatabase">Client database used by created handlers.</param>
    /// <param name="serverClient">Server API client used by created handlers.</param>
    /// <param name="configuration">Client sync configuration used by created handlers.</param>
    public TableSyncHandlerFactory(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        ClientSyncConfiguration configuration)
    {
        _clientDb = clientDatabase ?? throw new ArgumentNullException(nameof(clientDatabase));
        _serverClient = serverClient ?? throw new ArgumentNullException(nameof(serverClient));
        _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Create a strongly-typed handler for the specified table configuration.
    /// Uses reflection to instantiate the generic TableSyncHandler{T}.
    /// </summary>
    /// <param name="tableConfig">Table configuration including entity type</param>
    /// <returns>Strongly-typed table sync handler</returns>
    /// <exception cref="ArgumentNullException">If tableConfig is null</exception>
    /// <exception cref="InvalidOperationException">If handler creation fails</exception>
    public ITableSyncHandler CreateHandler(TableConfig tableConfig)
    {
        if (tableConfig == null)
            throw new ArgumentNullException(nameof(tableConfig));

        if (tableConfig.EntityType == null)
            throw new ArgumentException("TableConfig.EntityType cannot be null", nameof(tableConfig));

        try
        {
            // Create TableSyncHandler<T> where T = tableConfig.EntityType
            var handlerType = typeof(TableSyncHandler<>).MakeGenericType(tableConfig.EntityType);
            
            var handler = Activator.CreateInstance(
                handlerType,
                _clientDb,
                _serverClient,
                tableConfig,
                _config);

            if (handler == null)
                throw new InvalidOperationException($"Failed to create handler for table {tableConfig.TableName}");

            return (ITableSyncHandler)handler;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create sync handler for table '{tableConfig.TableName}' with entity type '{tableConfig.EntityType.Name}'. " +
                $"Ensure the entity type implements ISyncEntity.", 
                ex);
        }
    }
}
