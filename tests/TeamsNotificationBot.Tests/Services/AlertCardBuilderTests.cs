using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class AlertCardBuilderTests
{
    [Theory]
    [InlineData("Sev0", "Attention")]
    [InlineData("Sev1", "Attention")]
    [InlineData("Sev2", "Warning")]
    [InlineData("Sev3", "Accent")]
    [InlineData("Sev4", "Default")]
    [InlineData(null, "Default")]
    public void SeverityColorMapping(string? severity, string expectedColor)
    {
        var alert = CreateAlert(severity: severity);

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        var titleBlock = body[0];
        Assert.Equal(expectedColor, titleBlock.GetProperty("color").GetString());
    }

    [Theory]
    [InlineData("Sev0")]
    [InlineData("Sev2")]
    [InlineData("Sev4")]
    public void ResolvedAlert_IsGreenWhateverItsSeverity(string severity)
    {
        var alert = CreateAlert(severity: severity, monitorCondition: "Resolved");

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal("Good", body[0].GetProperty("color").GetString());
    }

    [Fact]
    public void IncludesAlertRuleInFacts()
    {
        var alert = CreateAlert(alertRule: "HighMemoryUsage");

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        var factSet = body[1];
        var facts = factSet.GetProperty("facts");

        var alertRuleFact = facts.EnumerateArray().First();
        Assert.Equal("Alert Rule", alertRuleFact.GetProperty("title").GetString());
        Assert.Equal("HighMemoryUsage", alertRuleFact.GetProperty("value").GetString());
    }

    [Fact]
    public void IncludesDescriptionWhenPresent()
    {
        var alert = CreateAlert(description: "CPU exceeded 95% for 5 minutes");

        var cardJson = AlertCardBuilder.Build(alert);

        Assert.Contains("CPU exceeded 95% for 5 minutes", cardJson);
    }

    [Fact]
    public void OmitsDescriptionWhenNull()
    {
        var alert = CreateAlert(description: null);

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        // Should have title + factset only (no description text block, no target block)
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public void IncludesTargetResource()
    {
        var alert = CreateAlert(targetResourceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/func-test");

        var cardJson = AlertCardBuilder.Build(alert);

        Assert.Contains("func-test", cardJson);
    }

    [Fact]
    public void ResolvedConditionUsesCheckmarkEmoji()
    {
        var alert = CreateAlert(monitorCondition: "Resolved");

        var cardJson = AlertCardBuilder.Build(alert);

        // JsonSerializer escapes non-ASCII to \uXXXX — check for the escaped form
        Assert.Contains("\\u2705", cardJson);
    }

    [Fact]
    public void NullEssentials_AreLeftOut()
    {
        // A budget alert: no rule, severity, signal type or target.
        var alert = new CommonAlertPayload
        {
            SchemaId = "azureMonitorCommonAlertSchema",
            Data = new AlertData
            {
                Essentials = new AlertEssentials
                {
                    MonitorCondition = "Fired",
                    MonitoringService = "CostAlerts",
                    FiredDateTime = "2026-02-14T12:00:00Z",
                    Description = "   ",
                    AlertTargetIDs = [""]
                }
            }
        };

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal("\ud83d\udea8 Azure Monitor Alert: CostAlerts", body[0].GetProperty("text").GetString());
        var titles = body[1].GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("title").GetString());
        Assert.Equal(["Condition", "Fired", "Monitoring Service"], titles);
        Assert.Equal(2, body.GetArrayLength()); // no description, no target
    }

    [Fact]
    public void NoEssentials_GiveATitleOnly()
    {
        var alert = new CommonAlertPayload { SchemaId = "azureMonitorCommonAlertSchema" };

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        // A null monitor condition counts as firing, and nothing is left to put in a FactSet.
        Assert.Equal("\ud83d\udea8 Azure Monitor Alert", Assert.Single(body.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public void TargetWithTrailingSlash_ShowsTheResourceName()
    {
        var alert = CreateAlert(targetResourceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/func-test/");

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal("**Target:** func-test", body[2].GetProperty("text").GetString());
    }

    [Fact]
    public void CustomProperties_AreShownUnderTheirOwnHeading()
    {
        var card = Card(customProperties: """{ "team": "platform", "runbook": "restart the service" }""");

        var heading = Array.FindIndex(card.Body, b => b.GetProperty("type").GetString() == "TextBlock" && b.GetProperty("text").GetString() == "Custom properties");
        Assert.True(heading > 0);
        Assert.Equal([("team", "platform"), ("runbook", "restart the service")], Facts(card.Body[heading + 1]));
    }

    [Fact]
    public void CustomPropertyHoldingEscapedJson_IsUnpacked()
    {
        // What a custom property set to ${data.alertContext.properties.statusMessage} carries.
        var statusMessage = JsonSerializer.Serialize(new { status = "Failed", error = new { code = "E1", message = "not enough time left" } });
        var card = Card(customProperties: JsonSerializer.Serialize(new { reason = statusMessage }));

        Assert.Equal(
            [("reason.status", "Failed"), ("reason.error.code", "E1"), ("reason.error.message", "not enough time left")],
            Facts(card.FactSets[1]));
    }

    [Fact]
    public void AlertContext_IsBehindShowDetails()
    {
        var card = Card(alertContext: """{ "operationName": "Microsoft.Compute/virtualMachines/restart/action", "status": "Failed" }""");

        var action = Assert.Single(card.Body.Last().GetProperty("actions").EnumerateArray());
        Assert.Equal("Action.ShowCard", action.GetProperty("type").GetString());
        Assert.Equal("Show details", action.GetProperty("title").GetString());
        Assert.Equal(
            [("operationName", "Microsoft.Compute/virtualMachines/restart/action"), ("status", "Failed")],
            Facts(action.GetProperty("card").GetProperty("body")[0]));
    }

    [Fact]
    public void HiddenContextFields_AreLeftOutAtAnyDepth()
    {
        var card = Card(alertContext: """
            {
              "claims": "{\"ipaddr\":\"192.0.2.10\"}",
              "httpRequest": "{\"clientIpAddress\":\"192.0.2.10\"}",
              "authorization": { "action": "x", "scope": "y" },
              "properties": {
                "impactedServicesTableRows": "<tr><td>x</td></tr>",
                "defaultLanguageTitle": "t", "DefaultLanguageContent": "c",
                "SearchResults": { "tables": [] },
                "linkToSearchResultsAPI": "https://api.loganalytics.io/v1/x",
                "LinkToFilteredSearchResultsAPI": "https://api.loganalytics.io/v1/y",
                "kept": "yes"
              }
            }
            """);

        Assert.Equal([("properties.kept", "yes")], Facts(card.Details!.Value[0]));
    }

    [Fact]
    public void CustomProperties_AreNotFiltered()
    {
        // The rule's author chose them; a property named like a hidden context field stays.
        var card = Card(customProperties: """{ "claims": "the author's own text" }""");

        Assert.Equal([("claims", "the author's own text")], Facts(card.FactSets[1]));
    }

    [Fact]
    public void LongValuesAndTitles_AreCut()
    {
        var card = Card(customProperties: JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [new string('k', 150)] = new string('v', 600)
        }));

        var (title, value) = Assert.Single(Facts(card.FactSets[1]));
        Assert.Equal(AlertCardBuilder.MaxTitleLength, title.Length);
        Assert.Equal(AlertCardBuilder.MaxValueLength, value.Length);
        Assert.EndsWith("\u2026", value);
    }

    [Fact]
    public void Cutting_DoesNotSplitASurrogatePair()
    {
        // An emoji straddling the cut.
        var text = new string('a', AlertCardBuilder.MaxValueLength - 2) + "\ud83d\ude80" + "tail";
        var card = Card(customProperties: JsonSerializer.Serialize(new { k = text }));

        var value = Assert.Single(Facts(card.FactSets[1])).Value;
        Assert.Equal(new string('a', AlertCardBuilder.MaxValueLength - 2) + "\u2026", value);
    }

    [Fact]
    public void MarkdownLinksInPayloadText_AreBrokenUp()
    {
        var card = Card(alertContext: """{ "message": "failed, see [the fix](https://example.com/phish)", "[t](https://example.com)": "v" }""");

        Assert.Equal(
            [("message", "failed, see [the fix] (https://example.com/phish)"), ("[t] (https://example.com)", "v")],
            Facts(card.Details!.Value[0]));
    }

    [Fact]
    public void ContextBeyondTheCap_IsCountedInTheDetails()
    {
        var context = Enumerable.Range(0, AlertCardBuilder.MaxContextFacts + 5).ToDictionary(i => $"f{i:D2}", i => "x");
        var card = Card(alertContext: JsonSerializer.Serialize(context));

        var details = card.Details!.Value;
        Assert.Equal(AlertCardBuilder.MaxContextFacts, Facts(details[0]).Count);
        Assert.Equal("5 more fields are not shown.", details[1].GetProperty("text").GetString());
    }

    [Fact]
    public void CustomPropertiesBeyondTheCap_AreCountedOnTheCard_WhenThereIsNoContext()
    {
        var custom = Enumerable.Range(0, AlertCardBuilder.MaxCustomFacts + 1).ToDictionary(i => $"k{i:D2}", i => "x");
        var card = Card(customProperties: JsonSerializer.Serialize(custom));

        Assert.Equal(AlertCardBuilder.MaxCustomFacts, Facts(card.FactSets[1]).Count);
        Assert.Null(card.Details);
        Assert.Equal("1 more field is not shown.", card.Body.Last().GetProperty("text").GetString());
    }

    [Fact]
    public void OversizedCard_IsTrimmedToTheQueueBudget_ContextFirst()
    {
        var big = new string('x', AlertCardBuilder.MaxValueLength);
        var custom = Enumerable.Range(0, AlertCardBuilder.MaxCustomFacts).ToDictionary(i => $"k{i:D2}", i => big);
        var context = Enumerable.Range(0, AlertCardBuilder.MaxContextFacts).ToDictionary(i => $"f{i:D2}", i => big);

        var json = AlertCardBuilder.Build(Payload(JsonSerializer.Serialize(custom), JsonSerializer.Serialize(context)));
        var card = new BuiltCard(json);

        Assert.True(AlertCardBuilder.QueuedSize(json) <= AlertCardBuilder.MaxQueuedCardBytes);
        Assert.Equal(AlertCardBuilder.MaxCustomFacts, Facts(card.FactSets[1]).Count);
        var shown = Facts(card.Details!.Value[0]).Count;
        Assert.InRange(shown, 1, AlertCardBuilder.MaxContextFacts - 1);
        var omitted = AlertCardBuilder.MaxContextFacts - shown;
        Assert.Equal($"{omitted} more fields are not shown.", card.Details!.Value[1].GetProperty("text").GetString());
    }

    [Fact]
    public void LongRuleName_IsCut_AndTheDetailsStay()
    {
        var payload = Payload("""{ "team": "platform" }""", """{ "status": "Failed" }""");
        payload.Data!.Essentials!.AlertRule = new string('r', AlertCardBuilder.MaxQueuedCardBytes);

        var json = AlertCardBuilder.Build(payload);
        var card = new BuiltCard(json);

        Assert.True(AlertCardBuilder.QueuedSize(json) <= AlertCardBuilder.MaxQueuedCardBytes);
        Assert.EndsWith(new string('r', AlertCardBuilder.MaxValueLength - 1) + "\u2026", card.Body[0].GetProperty("text").GetString());
        Assert.Equal(AlertCardBuilder.MaxValueLength, Facts(card.FactSets[0])[0].Value.Length);
        Assert.NotNull(card.Details);
    }

    [Fact]
    public void EssentialsThatOutgrowTheBudget_AreCutHarder_AndTheDetailsGo()
    {
        // Only a payload built to be odd gets here: every essential is long and all quotes,
        // which the queue envelope escapes to seven bytes each.
        var json = AlertCardBuilder.Build(OddPayload("""{ "team": "platform" }""", """{ "status": "Failed" }"""));
        var card = new BuiltCard(json);

        Assert.True(AlertCardBuilder.QueuedSize(json) <= AlertCardBuilder.MaxQueuedCardBytes);
        Assert.Null(card.Details);
        Assert.Single(card.FactSets); // the essentials'
        Assert.All(Facts(card.FactSets[0]), f => Assert.Equal(AlertCardBuilder.MinValueLength, f.Value.Length));
        Assert.Equal("2 more fields are not shown.", card.Body.Last().GetProperty("text").GetString());
    }

    [Fact]
    public void DetailsThatCannotBeRendered_WithEssentialsOverTheBudget_AreCutHarder()
    {
        JsonElement disposed;
        using (var document = JsonDocument.Parse("""{ "k": "v" }"""))
            disposed = document.RootElement;
        var payload = OddPayload(null, null);
        payload.Data!.CustomProperties = disposed;

        var json = AlertCardBuilder.Build(payload);

        Assert.True(AlertCardBuilder.QueuedSize(json) <= AlertCardBuilder.MaxQueuedCardBytes);
        Assert.All(Facts(new BuiltCard(json).FactSets[0]), f => Assert.Equal(AlertCardBuilder.MinValueLength, f.Value.Length));
    }

    /// <summary>A payload whose essentials are all 5,000 quotes.</summary>
    private static CommonAlertPayload OddPayload(string? customProperties, string? alertContext)
    {
        var quotes = new string('"', 5000);
        var payload = Payload(customProperties, alertContext);
        payload.Data!.Essentials = new AlertEssentials
        {
            AlertRule = quotes, Severity = quotes, MonitorCondition = quotes, SignalType = quotes,
            FiredDateTime = quotes, MonitoringService = quotes, Description = quotes, AlertTargetIDs = ["/x/" + quotes]
        };
        return payload;
    }

    [Fact]
    public void LongDescription_IsCut()
    {
        var alert = CreateAlert(description: new string('d', AlertCardBuilder.MaxDescriptionLength + 1));

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal(AlertCardBuilder.MaxDescriptionLength, body[2].GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public void DetailsThatCannotBeRendered_LeaveTheEssentials_AndAreLogged()
    {
        JsonElement disposed;
        using (var document = JsonDocument.Parse("""{ "status": "Failed" }"""))
            disposed = document.RootElement; // reading it after this throws
        var alert = CreateAlert(alertRule: "HighCPU");
        alert.Data!.AlertContext = disposed;
        var logger = new ListLogger<AlertCardBuilderTests>();

        var card = new BuiltCard(AlertCardBuilder.Build(alert, logger, "alert-1"));

        Assert.Null(card.Details);
        Assert.Equal("HighCPU", Facts(card.FactSets[0])[0].Value);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("alert-1", entry.Message);
        Assert.IsType<ObjectDisposedException>(entry.Exception);
    }

    [Fact]
    public void DetailsThatCannotBeRendered_LeaveTheEssentials_WithoutALogger()
    {
        JsonElement disposed;
        using (var document = JsonDocument.Parse("""{ "k": "v" }"""))
            disposed = document.RootElement;
        var alert = CreateAlert();
        alert.Data!.CustomProperties = disposed;

        var card = new BuiltCard(AlertCardBuilder.Build(alert));

        Assert.Single(card.FactSets);
    }

    [Fact]
    public void AlertKeptByAlertsManagement_LinksToItsPortalPage()
    {
        var alert = CreateAlert();
        alert.Data!.Essentials!.AlertId = "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/11111111-0000-0000-0000-000000000002";

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal(
            "[Open in Azure portal](https://portal.azure.com/#view/Microsoft_Azure_Monitoring_Alerts/AlertDetails.ReactView/alertId~/" +
            "%2Fsubscriptions%2F00000000-0000-0000-0000-000000000001%2Fproviders%2FMicrosoft.AlertsManagement%2Falerts%2F11111111-0000-0000-0000-000000000002)",
            body.EnumerateArray().Last().GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/subscriptions/s/resourceGroups/rg/providers/microsoft.insights/activityLogAlerts/Test_Alert")] // an action group test
    [InlineData("/subscriptions/s/providers/Microsoft.CostManagement/alerts/Test_Alert")]                          // a budget alert
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/11111111-0000-0000-0000-000000000002/extra")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/11111111-0000-0000-0000-000000000002\n")]
    [InlineData("/subscriptions/s/providers/Microsoft.AlertsManagement/alerts/11111111-0000-0000-0000-000000000002")] // not GUIDs
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/a")]
    public void OtherAlertIds_GetNoPortalLink(string? alertId)
    {
        Assert.Null(AlertCardBuilder.AlertPageUrl(alertId));
    }

    [Fact]
    public void AnAlertIdThatWouldMakeAHugeLink_GetsNoPortalLink()
    {
        // 9,000 characters that each percent-encode to six would make a 54 KB link the card
        // couldn't trim.
        Assert.Null(AlertCardBuilder.AlertPageUrl(
            "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/" + new string('\u00e9', 9000)));
    }

    [Fact]
    public void PortalUrlsInTheContext_BecomeLinks_AndAreNotCut()
    {
        var search = "https://portal.azure.com#@00000000-0000-0000-0000-000000000001/blade/Microsoft_Azure_Monitoring_Logs/LogsBlade/q/" + new string('q', 900) + "(x)";
        var card = Card(alertContext: JsonSerializer.Serialize(new { linkToSearchResultsUI = search }));

        var value = Assert.Single(Facts(card.Details!.Value[0])).Value;
        Assert.StartsWith("[Open in Azure portal](https://portal.azure.com/#@00000000-0000-0000-0000-000000000001/blade/", value);
        Assert.EndsWith(new string('q', 900) + "%28x%29)", value);
    }

    [Theory]
    [InlineData("https://example.com/portal.azure.com/x")]
    [InlineData("https://portal.azure.com.example.com/x")]
    [InlineData("https://portal.azure.com@example.com/x")]
    [InlineData("https://portal.azure.com:8443/x")]
    [InlineData("http://portal.azure.com/x")]
    [InlineData("see https://portal.azure.com/x")]
    public void OtherUrls_StayText(string url)
    {
        Assert.Null(AlertCardBuilder.PortalLinkTarget(url));
        var card = Card(alertContext: JsonSerializer.Serialize(new { link = url }));

        Assert.Equal(url, Assert.Single(Facts(card.Details!.Value[0])).Value);
    }

    [Fact]
    public void Titles_CanBreakAtTheirDotsAndIndexes()
    {
        var card = Card(alertContext: """{ "condition": { "details": [ { "message": "a" }, { "message": "b" } ] } }""");

        var title = card.Details!.Value[0].GetProperty("facts")[0].GetProperty("title").GetString();
        Assert.Equal("condition.\u200Bdetails\u200B[0].\u200Bmessage", title);
    }

    [Theory]
    [InlineData("<p>Hello</p><p>World &amp; more</p>", "Hello\nWorld & more")]
    [InlineData("Line one<br/>Line two<BR >three", "Line one\nLine two\nthree")]
    [InlineData("<ul>\r\n<li>a</li>\r\n<li>b</li></ul>", "a\nb")]
    [InlineData("<a href=\"https://example.com\">the status page</a> has more", "the status page has more")]
    [InlineData("<p>Thisisatest</p>", "Thisisatest")]
    [InlineData("<P>Upper case</P>", "Upper case")]
    [InlineData("<span class=\"x\">a</span><div>b</div>", "a\nb")]
    [InlineData("<p>First<p>Second", "First\nSecond")]                      // end tags left out
    [InlineData("<ul><li>Storage<li>Compute</ul>", "Storage\nCompute")]
    [InlineData("<p title=\"load > 90%\">Degraded</p>", "Degraded")]          // brackets in quoted attributes
    [InlineData("<a title='a < b'>Status</a> page", "Status page")]
    [InlineData("<p title=\"x\">a</p><p class='y > z'>b</p>", "a\nb")]
    [InlineData("<a title=\"Use <p for paragraphs\">Service is degraded <span title=\"load > 90%\">until 02:00</span></a>", "Service is degraded until 02:00")]
    [InlineData("<div data-x=1 class=\"a\" id='b'>one</div><BR class='c'/>two", "one\ntwo")]
    [InlineData("<LI>upper</LI><TD title=\"t\">cell</TD>", "upper\ncell")]
    [InlineData("<span title=\"'quoted'\" lang='\"x\"'>mixed</span>", "mixed")]
    [InlineData("<h3 >Spaced name end</h3 ><em/>", "Spaced name end")]
    [InlineData("<td>a</td><th>b</th><tbody></tbody><thead></thead><ol><u>c</u></ol>", "abc")]
    [InlineData("<h2>Title</h2><table><tr><td>x</td><td>y</td></tr></table>", "Title\nxy")]
    [InlineData("<i>em</i> &lt;tag&gt;", "em <tag>")]
    [InlineData("<strong>Bold</strong><br>next", "Bold\nnext")]
    public void HtmlText_BecomesPlainText(string html, string expected)
    {
        Assert.Equal(expected, AlertCardBuilder.TextFromHtml(html));
    }

    [Theory]
    [InlineData("List<string> was null")]
    [InlineData("a < b > c")]
    [InlineData("1 &amp; 2")]
    [InlineData("<MESSAGE>")]
    [InlineData("<at>someone</at>")]
    [InlineData("<pre>code</pre>")]
    [InlineData("<b")]
    [InlineData("</>")]
    [InlineData("< p>spaced</ p>")]
    [InlineData("<brand>name</brand>")]
    [InlineData("Dictionary<A,B> was null")]
    [InlineData("<a:Status>Failed</a:Status>")]
    [InlineData("<p-x>not an element</p-x>")]
    [InlineData("<p title=\"unterminated>text")]
    [InlineData("<span title='unterminated>text")]
    [InlineData("<br title=\"a\"")]
    [InlineData("<li class=x")]
    [InlineData("text <br/")]
    [InlineData("text </p")]
    [InlineData("<div")]
    public void TextThatIsNotHtml_IsLeftAsItIs(string text)
    {
        Assert.Equal(text, AlertCardBuilder.TextFromHtml(text));
    }

    [Fact]
    public void HtmlInTheContext_IsShownAsText()
    {
        var card = Card(alertContext: """{ "properties": { "communication": "<p>Starting at 01:00 UTC, &quot;Storage&quot; is degraded.</p><p>Next update in 60 minutes.</p>" } }""");

        Assert.Equal(
            [("properties.communication", "Starting at 01:00 UTC, \"Storage\" is degraded.\nNext update in 60 minutes.")],
            Facts(card.Details!.Value[0]));
    }

    [Theory]
    [InlineData("2025-04-15T11:26:21.386Z", "2025-04-15 11:26:21 UTC")]
    [InlineData("2019-03-22T13:56:31.2917159+00:00", "2019-03-22 13:56:31 UTC")]
    [InlineData("2026-10-16T03:20:42+02:00", "2026-10-16 01:20:42 UTC")]
    [InlineData("2026-10-16T01:20z", "2026-10-16 01:20:00 UTC")]
    [InlineData("2026-10-16T01:20:42.5-05:30", "2026-10-16 06:50:42 UTC")]
    public void IsoTimestampsWithAZone_AreShownInUtc(string text, string expected)
    {
        Assert.Equal(expected, AlertCardBuilder.Timestamp(text));
    }

    [Theory]
    [InlineData("2022-11-01")]                    // a date alone
    [InlineData("2026-10-16T01:20:42")]           // no zone: can't be told as UTC
    [InlineData("3/22/2019 1:36:31 PM")]
    [InlineData("Wed, 21 Aug 2019 16:17:47 GMT")]
    [InlineData("3.0")]
    [InlineData("2026-13-45T25:61:00Z")]          // looks right, isn't a time
    [InlineData("since 2026-10-16T01:20:42Z")]
    [InlineData("2026-10-16T01")]
    [InlineData("2026-10-16T01:20:4Z")]
    [InlineData("2026-10-16 01:20:42Z")]
    [InlineData("2026-10-16T01:20:42.Z")]
    [InlineData("2026-10-16T01:20:42+0200")]
    [InlineData("20261016T012042Z")]
    [InlineData("2026-10-16T01:20:42Z trailing")]
    [InlineData("2026-10-16T01:20:42+02")]
    [InlineData("2026-10-16T01:20:42-")]
    [InlineData("2026-10-16T01:20:")]
    public void AnythingElse_IsLeftAsItIs(string text)
    {
        Assert.Equal(text, AlertCardBuilder.Timestamp(text));
    }

    [Fact]
    public void FiredAndContextTimestamps_AreShownInUtc()
    {
        var card = Card(alertContext: """{ "eventTimestamp": "2026-10-16T03:20:42.1234567+02:00", "threshold": "3.0" }""");

        Assert.Equal("2026-02-14 12:00:00 UTC", Facts(card.FactSets[0]).Single(f => f.Title == "Fired").Value);
        Assert.Equal([("eventTimestamp", "2026-10-16 01:20:42 UTC"), ("threshold", "3.0")], Facts(card.Details!.Value[0]));
    }

    /// <summary>A payload with the given sections, as the alert function reads it.</summary>
    private static CommonAlertPayload Payload(string? customProperties, string? alertContext)
    {
        var data = new JsonObject
        {
            ["essentials"] = new JsonObject { ["alertRule"] = "rule", ["severity"] = "Sev2", ["monitorCondition"] = "Fired", ["firedDateTime"] = "2026-02-14T12:00:00Z" }
        };
        if (customProperties is not null)
            data["customProperties"] = JsonNode.Parse(customProperties);
        if (alertContext is not null)
            data["alertContext"] = JsonNode.Parse(alertContext);
        var json = new JsonObject { ["schemaId"] = "azureMonitorCommonAlertSchema", ["data"] = data }.ToJsonString();
        return JsonSerializer.Deserialize<CommonAlertPayload>(json, JsonSerializerOptions.Web)!;
    }

    private static BuiltCard Card(string? customProperties = null, string? alertContext = null) =>
        new(AlertCardBuilder.Build(Payload(customProperties, alertContext)));

    /// <summary>A fact set's facts, the titles without their line-break opportunities.</summary>
    private static List<(string Title, string Value)> Facts(JsonElement factSet) =>
        factSet.GetProperty("facts").EnumerateArray()
            .Select(f => (f.GetProperty("title").GetString()!.Replace("\u200B", ""), f.GetProperty("value").GetString()!)).ToList();

    /// <summary>The parts of a built card the tests look at.</summary>
    private sealed class BuiltCard(string json)
    {
        public JsonElement[] Body { get; } = JsonDocument.Parse(json).RootElement.GetProperty("body").EnumerateArray().ToArray();

        public JsonElement[] FactSets => Body.Where(b => b.GetProperty("type").GetString() == "FactSet").ToArray();

        /// <summary>The body of the "Show details" card, or null when the card has none.</summary>
        public JsonElement? Details => Body
            .Where(b => b.GetProperty("type").GetString() == "ActionSet")
            .Select(b => (JsonElement?)b.GetProperty("actions")[0].GetProperty("card").GetProperty("body"))
            .SingleOrDefault();
    }

    private static CommonAlertPayload CreateAlert(
        string alertRule = "TestRule",
        string? severity = "Sev2",
        string monitorCondition = "Fired",
        string? description = null,
        string? targetResourceId = null)
    {
        return new CommonAlertPayload
        {
            SchemaId = "azureMonitorCommonAlertSchema",
            Data = new AlertData
            {
                Essentials = new AlertEssentials
                {
                    AlertRule = alertRule,
                    Severity = severity,
                    MonitorCondition = monitorCondition,
                    SignalType = "Metric",
                    FiredDateTime = "2026-02-14T12:00:00Z",
                    Description = description,
                    AlertTargetIDs = targetResourceId != null ? [targetResourceId] : null,
                    MonitoringService = "Platform"
                }
            }
        };
    }
}
