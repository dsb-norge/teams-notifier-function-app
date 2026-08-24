using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Storage;
using Moq;

namespace TeamsNotificationBot.Tests.Helpers;

/// <summary>
/// AgentApplicationOptions for tests, mirroring the production registration in Program.cs:
/// mention handling stays inside TeamsBotHandler (not the SDK turn pipeline), no typing timer,
/// in-memory turn state. Connections/HttpClientFactory satisfy the TeamsAgentExtension
/// before-turn hook, which constructs a per-turn Teams ApiClient — no HTTP happens unless a
/// handler actually uses that client, so plain tests can ignore <paramref name="teamsApiHttpClient"/>;
/// tests that exercise the Teams channel-list API pass a stubbed HttpClient instead.
/// </summary>
internal static class TestAgentOptions
{
    internal static AgentApplicationOptions Create(HttpClient? teamsApiHttpClient = null)
    {
        var tokenProvider = new Mock<IAccessTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(
                It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<bool>()))
            .ReturnsAsync("test-token");
        var connections = new Mock<IConnections>();
        connections.Setup(c => c.GetTokenProvider(
                It.IsAny<System.Security.Claims.ClaimsIdentity>(), It.IsAny<string>()))
            .Returns(tokenProvider.Object);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => teamsApiHttpClient ?? new HttpClient());

        return new AgentApplicationOptions(new MemoryStorage())
        {
            Connections = connections.Object,
            HttpClientFactory = httpClientFactory.Object,
            RemoveRecipientMention = false,
            NormalizeMentions = false,
            StartTypingTimer = false,
        };
    }
}
