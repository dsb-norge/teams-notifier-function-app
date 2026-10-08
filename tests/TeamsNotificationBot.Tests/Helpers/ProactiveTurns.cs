using System.Security.Claims;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore;
using Moq;

namespace TeamsNotificationBot.Tests.Helpers;

/// <summary>
/// Runs a mocked adapter's proactive turns the way CloudAdapter does. The real adapter runs the
/// callback inside its turn pipeline, and its default turn-error handler takes whatever the callback
/// throws: it posts the exception's message into the conversation and returns normally. A mock that
/// calls the callback directly lets every exception through, so a test can't see one the real
/// adapter would swallow. Here the swallowed exceptions are collected instead, and an empty list
/// means nothing would have been posted.
/// </summary>
public static class ProactiveTurns
{
    public static void RunLikeTheSdk(
        Mock<CloudAdapter> adapter, ITurnContext turnContext, List<Exception> turnErrors,
        Action<ConversationReference>? onReference = null)
    {
        adapter
            .Setup(a => a.ContinueConversationAsync(
                It.IsAny<ClaimsIdentity>(), It.IsAny<ConversationReference>(),
                It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()))
            .Returns(async (ClaimsIdentity _, ConversationReference reference, AgentCallbackHandler callback, CancellationToken ct) =>
            {
                onReference?.Invoke(reference);
                var turn = callback(turnContext, ct);
                await turn.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (turn.Exception?.InnerException is { } error)
                    turnErrors.Add(error);
            });
    }
}
