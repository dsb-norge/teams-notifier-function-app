using System.Net;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Storage;
using Moq;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Tests.Helpers;

/// <summary>
/// AgentApplicationOptions for tests. Behavior flags come from the same
/// TeamsBotHandler.ApplyHandlerOptionInvariants the production registration in Program.cs uses,
/// so test and production configuration cannot drift. Connections/HttpClientFactory satisfy the
/// TeamsAgentExtension before-turn hook, which constructs a per-turn Teams ApiClient; no HTTP
/// happens unless a handler actually uses that client, and the default HttpClient answers any
/// unexpected request with a local 500 (never the network). Tests that exercise the Teams
/// channel-list API pass their own stubbed HttpClient via <paramref name="teamsApiHttpClient"/>.
/// </summary>
internal static class TestAgentOptions
{
    private static readonly HttpMessageHandler NoNetworkHandler = new LocalErrorHandler();

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
            .Returns(() => teamsApiHttpClient ?? new HttpClient(NoNetworkHandler, disposeHandler: false));

        return TeamsBotHandler.ApplyHandlerOptionInvariants(
            new AgentApplicationOptions(new MemoryStorage())
            {
                Connections = connections.Object,
                HttpClientFactory = httpClientFactory.Object,
            });
    }

    private sealed class LocalErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("TestAgentOptions: unexpected HTTP call in test")
            });
    }
}
