using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Moq;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

/// <summary>
/// Shared turn-context mock factory and invoke-response assertions for handler tests.
/// AgentApplication's turn pipeline writes to Services and StackState on every turn, so every
/// mocked ITurnContext needs real TurnContextStateCollections or the pipeline NREs deep inside
/// OnTurnAsync — this is the one place to stub the next required member, instead of once per
/// fixture factory.
/// </summary>
internal static class TurnContextStub
{
    internal static Mock<ITurnContext<T>> Wrap<T>(Activity activity) where T : class, IActivity
    {
        var turnContext = new Mock<ITurnContext<T>>();
        turnContext.Setup(t => t.Activity).Returns((T)(object)activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.Services).Returns(new TurnContextStateCollection());
        turnContext.Setup(t => t.StackState).Returns(new TurnContextStateCollection());
        turnContext.Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());
        return turnContext;
    }

    /// <summary>The invokeResponse activities sent through the mocked context.</summary>
    internal static List<IActivity> SentInvokeResponses<T>(Mock<ITurnContext<T>> turnContext)
        where T : class, IActivity
        => turnContext.Invocations
            .Where(i => i.Method.Name == nameof(ITurnContext.SendActivityAsync))
            .Select(i => i.Arguments[0])
            .OfType<IActivity>()
            .Where(a => a.Type == ActivityTypes.InvokeResponse)
            .ToList();

    /// <summary>
    /// Asserts exactly one invokeResponse was sent and returns its AdaptiveCardInvokeResponse body.
    /// </summary>
    internal static AdaptiveCardInvokeResponse GetInvokeResponseBody<T>(Mock<ITurnContext<T>> turnContext)
        where T : class, IActivity
    {
        var sent = SentInvokeResponses(turnContext);
        var invokeResponse = Assert.IsType<InvokeResponse>(((Activity)Assert.Single(sent)).Value);
        return Assert.IsType<AdaptiveCardInvokeResponse>(invokeResponse.Body);
    }
}
