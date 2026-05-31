using Xunit;

namespace SyncSession.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection definition for sharing the MariaDB container across all integration tests.
/// All test classes marked with [Collection("MariaDB Collection")] will share the same container.
/// </summary>
[CollectionDefinition("MariaDB Collection")]
public class MariaDbCollection : ICollectionFixture<MariaDbFixture>
{
    // This class is never instantiated.
    // It exists solely to apply ICollectionFixture<MariaDbFixture> to the collection.
}
