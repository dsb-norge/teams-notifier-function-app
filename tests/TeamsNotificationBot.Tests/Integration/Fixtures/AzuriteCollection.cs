using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Fixtures;

[CollectionDefinition("Azurite")]
public class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    // Marker class — xUnit uses this to share AzuriteFixture across test classes
}
