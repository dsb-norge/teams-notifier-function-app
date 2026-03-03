using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Extensions.Teams.Compat;
using Microsoft.Agents.Extensions.Teams.Connector;
using Microsoft.Agents.Extensions.Teams.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class TeamsBotHandler : TeamsActivityHandler
{
    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly IQueueManagementService? _queueService;
    private readonly TableClient _teamLookupTable;
    private readonly QueueClient _botOpsQueue;
    private readonly ILogger<TeamsBotHandler> _logger;

    private static readonly Regex AliasNameRegex = new(@"^[a-z0-9][a-z0-9\-]{0,48}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly HashSet<string> ValidPoisonQueues = ["notifications-poison", "botoperations-poison"];

    // Poison alias nudge cache (v1.5 §5)
    private string? _poisonAliasNudgeCache;
    private DateTimeOffset _poisonAliasNudgeCacheExpiry;

    public TeamsBotHandler(
        IBotService botService,
        IAliasService aliasService,
        [FromKeyedServices("teamlookup")] TableClient teamLookupTable,
        [FromKeyedServices("botoperations")] QueueClient botOpsQueue,
        ILogger<TeamsBotHandler> logger,
        IQueueManagementService? queueService = null)
    {
        _botService = botService;
        _aliasService = aliasService;
        _teamLookupTable = teamLookupTable;
        _botOpsQueue = botOpsQueue;
        _logger = logger;
        _queueService = queueService;
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        // Handle Adaptive Card Action.Submit (arrives as message with Value, not invoke)
        if (turnContext.Activity.Value != null)
        {
            await HandleCardSubmitAsync(turnContext, cancellationToken);
            return;
        }

        turnContext.Activity.RemoveRecipientMention();
        var text = turnContext.Activity.Text?.Trim() ?? string.Empty;
        var command = text.TrimStart('/').ToLower();

        _logger.LogInformation("Received message from Teams: {Text}", text);

        // Auto-refresh conversation reference on every message
        await AutoRefreshReferenceAsync(turnContext);

        var showNudge = false;

        if (command.StartsWith("set-alias"))
        {
            await HandleSetAliasAsync(turnContext, text, command, cancellationToken);
        }
        else if (command.StartsWith("remove-alias"))
        {
            await HandleRemoveAliasAsync(turnContext, command, cancellationToken);
        }
        else if (command is "list-aliases" or "list-alias")
        {
            await HandleListAliasesAsync(turnContext, cancellationToken);
            showNudge = true;
        }
        else if (command == "checkin")
        {
            var timestamp = DateTimeOffset.UtcNow;
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    $"\u2705 Bot is online | Version: {AppInfo.Version} | Time: {timestamp:u}"),
                cancellationToken);
            showNudge = true;
        }
        else if (command == "delete-post")
        {
            await HandleDeletePostAsync(turnContext, cancellationToken);
        }
        else if (command == "setup-guide")
        {
            await HandleSetupGuideAsync(turnContext, cancellationToken);
        }
        else if (command == "create-alias")
        {
            await HandleCreateAliasCardAsync(turnContext, cancellationToken);
        }
        else if (command == "queue-status")
        {
            await HandleQueueStatusAsync(turnContext, cancellationToken);
        }
        else if (command.StartsWith("queue-peek"))
        {
            await HandleQueuePeekAsync(turnContext, command, cancellationToken);
        }
        else if (command.StartsWith("queue-retry-all"))
        {
            await HandleQueueRetryAllAsync(turnContext, command, cancellationToken);
        }
        else if (command.StartsWith("queue-retry"))
        {
            await HandleQueueRetryAsync(turnContext, command, cancellationToken);
        }
        else if (command == "help" || command.StartsWith("help "))
        {
            await HandleHelpAsync(turnContext, command, cancellationToken);
            showNudge = true;
        }
        else
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(HelpTextBuilder.UnknownCommand(text)),
                cancellationToken);
        }

        // v1.5 §5: Append setup nudge if poison alert alias is missing
        if (showNudge)
        {
            try
            {
                var nudge = await GetPoisonAliasNudgeAsync();
                if (nudge != null)
                    await turnContext.SendActivityAsync(MessageFactory.Text(nudge), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check/send poison alias nudge");
            }
        }
    }

    // --- Help command ---

    private async Task HandleHelpAsync(
        ITurnContext turnContext, string command, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var topic = parts.Length > 1 ? parts[1].Trim().ToLower() : "";

        var text = topic switch
        {
            "aliases" or "alias" => HelpTextBuilder.Aliases(),
            "endpoints" or "endpoint" or "api" => HelpTextBuilder.Endpoints(GetHostname()),
            "queues" or "queue" => HelpTextBuilder.Queues(),
            "diagnostics" or "diagnostic" or "diag" => HelpTextBuilder.Diagnostics(),
            "" => HelpTextBuilder.Overview(),
            _ => $"Unknown help topic: `{topic}`\n\nAvailable topics: **aliases**, **endpoints**, **queues**, **diagnostics**"
        };

        await turnContext.SendActivityAsync(MessageFactory.Text(text), cancellationToken);
    }

    // --- Alias commands ---

    private async Task HandleSetAliasAsync(
        ITurnContext turnContext, string originalText, string command, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: **set-alias** `<name>` `[description]`"),
                cancellationToken);
            return;
        }

        var name = parts[1].ToLowerInvariant();
        // Extract description from original text to preserve casing
        var originalParts = originalText.TrimStart('/').Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var description = originalParts.Length > 2 ? originalParts[2] : string.Empty;

        if (!AliasNameRegex.IsMatch(name))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "Invalid alias name. Use 2-50 characters: lowercase letters, digits, hyphens. " +
                    "Must start and end with a letter or digit."),
                cancellationToken);
            return;
        }

        var (pk, rk, targetType) = await ExtractConversationKeysAsync(turnContext.Activity);
        if (pk == null || rk == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Could not determine conversation target from this context."),
                cancellationToken);
            return;
        }

        var entity = new AliasEntity
        {
            TargetType = targetType,
            TeamId = targetType == "channel" ? pk : null,
            ChannelId = targetType == "channel" ? rk : null,
            UserId = targetType == "personal" ? rk : null,
            ChatId = targetType == "groupChat" ? rk : null,
            Description = description,
            CreatedBy = turnContext.Activity.From?.AadObjectId ?? string.Empty,
            CreatedByName = turnContext.Activity.From?.Name ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _aliasService.SetAliasAsync(name, entity);
        InvalidatePoisonNudgeCacheIfMatch(name);
        _logger.LogInformation("Alias '{Alias}' set for {Type} {PK}/{RK} by {User}",
            name, targetType, pk, rk, entity.CreatedByName);

        var hostname = GetHostname();
        await turnContext.SendActivityAsync(
            MessageFactory.Text(
                $"Alias **{name}** set for this {targetType}.\n\n" +
                $"Notify: `https://{hostname}/api/v1/notify/{name}`"),
            cancellationToken);
    }

    private async Task HandleRemoveAliasAsync(
        ITurnContext turnContext, string command, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: **remove-alias** `<name>`"),
                cancellationToken);
            return;
        }

        var name = parts[1].ToLowerInvariant();
        var removed = await _aliasService.RemoveAliasAsync(name);
        InvalidatePoisonNudgeCacheIfMatch(name);

        if (removed)
        {
            _logger.LogInformation("Alias '{Alias}' removed by {User}",
                name, turnContext.Activity.From?.Name);
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Alias **{name}** removed."),
                cancellationToken);
        }
        else
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Alias **{name}** not found."),
                cancellationToken);
        }
    }

    private async Task HandleListAliasesAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var aliases = await _aliasService.GetAllAliasesAsync();
        if (aliases.Count == 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("No aliases configured."),
                cancellationToken);
            return;
        }

        // Pre-fetch channel names for teams that have aliases with missing channel names.
        // One API call per team, cached for the duration of this list operation.
        var channelNameCache = await BuildChannelNameCacheAsync(turnContext, aliases);

        var hostname = GetHostname();
        var displayInfos = new List<AliasDisplayInfo>();
        foreach (var a in aliases)
        {
            var target = await FormatAliasTargetAsync(a, channelNameCache);
            displayInfos.Add(new AliasDisplayInfo(
                Name: a.RowKey!,
                TargetType: a.TargetType ?? "unknown",
                Target: target,
                Description: a.Description,
                CreatedByName: a.CreatedByName,
                RelativeTime: FormatRelativeTime(a.CreatedAt)));
        }

        var cardJson = AliasListCardBuilder.Build(displayInfos, hostname);
        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<object>(cardJson)
        };
        await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);
    }

    private async Task<string> FormatAliasTargetAsync(
        AliasEntity alias, Dictionary<string, string> channelNameCache)
    {
        switch (alias.TargetType)
        {
            case "channel":
            {
                var entity = await _botService.GetConversationReferenceEntityAsync(alias.TeamId!, alias.ChannelId!);
                var channelName = entity?.ChannelName;
                var teamName = entity?.TeamName;

                // Fallback 1: extract from stored ConversationReference JSON
                if (string.IsNullOrEmpty(channelName) && entity != null)
                    channelName = ExtractFromConversationReference(entity.ConversationReference, "Conversation", "Name");

                // Fallback 2: use pre-fetched channel name from Teams API
                if (string.IsNullOrEmpty(channelName))
                    channelNameCache.TryGetValue(alias.ChannelId!, out channelName);

                if (!string.IsNullOrEmpty(channelName) || !string.IsNullOrEmpty(teamName))
                {
                    var channelDisplay = !string.IsNullOrEmpty(channelName) ? $"#{channelName}" : "channel";
                    var teamDisplay = teamName ?? alias.TeamId;
                    return $"{channelDisplay} in {teamDisplay}";
                }
                return $"channel `{alias.ChannelId}` in team `{alias.TeamId}`";
            }
            case "personal":
            {
                var entity = await _botService.GetConversationReferenceEntityAsync("user", alias.UserId!);
                var userName = entity?.UserName;

                // Fallback: extract user name from stored ConversationReference JSON
                if (string.IsNullOrEmpty(userName) && entity != null)
                    userName = ExtractFromConversationReference(entity.ConversationReference, "User", "Name");

                return !string.IsNullOrEmpty(userName) ? userName : $"user `{alias.UserId}`";
            }
            case "groupChat":
                return "group chat";
            default:
                return "unknown";
        }
    }

    private async Task<Dictionary<string, string>> BuildChannelNameCacheAsync(
        ITurnContext turnContext, IReadOnlyList<AliasEntity> aliases)
    {
        var cache = new Dictionary<string, string>();

        // GetTeamChannelsAsync requires the team thread ID (19:xxx@thread.tacv2), not the AAD group GUID.
        // We can only resolve this from the current turn context when the command is sent from a team channel.
        var channelData = turnContext.Activity.GetChannelData<TeamsChannelData>();
        var currentTeamThreadId = channelData?.Team?.Id;

        if (string.IsNullOrEmpty(currentTeamThreadId))
            return cache; // Not in a team context — can't resolve channel names via API

        // Resolve the AAD group GUID for the current team. In message activities, AadGroupId is
        // often null — ResolveTeamGuidAsync falls back to the teamlookup table.
        var currentTeamGroupId = await ResolveTeamGuidAsync(channelData?.Team);

        // Only resolve channels for the current team (we only have its thread ID)
        var hasAliasInCurrentTeam = aliases.Any(a =>
            a.TargetType == "channel" &&
            string.Equals(a.TeamId, currentTeamGroupId, StringComparison.OrdinalIgnoreCase));

        if (!hasAliasInCurrentTeam)
            return cache;

        try
        {
            var channels = await TeamsInfo.GetTeamChannelsAsync(turnContext, currentTeamThreadId);
            foreach (var ch in channels)
            {
                if (!string.IsNullOrEmpty(ch.Name))
                    cache[ch.Id] = ch.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch channels for team {TeamId}", currentTeamThreadId);
        }

        return cache;
    }

    private static string? ExtractFromConversationReference(string json, string objectKey, string propertyKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(objectKey, out var obj) &&
                obj.TryGetProperty(propertyKey, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private static string GetHostname()
    {
        return Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") ?? "localhost:7071";
    }

    private static string FormatRelativeTime(DateTimeOffset created)
    {
        var elapsed = DateTimeOffset.UtcNow - created;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d ago";
        return created.ToString("yyyy-MM-dd");
    }

    // --- Poison alias setup nudge (v1.5 §5) ---

    internal async Task<string?> GetPoisonAliasNudgeAsync()
    {
        if (DateTimeOffset.UtcNow < _poisonAliasNudgeCacheExpiry)
            return _poisonAliasNudgeCache;

        var aliasName = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        string? nudge;
        if (string.IsNullOrEmpty(aliasName))
        {
            nudge = "\u26a0\ufe0f Poison alert alias is not configured. Set the `PoisonAlertAlias` app setting.";
        }
        else
        {
            var alias = await _aliasService.GetAliasAsync(aliasName);
            nudge = alias == null
                ? $"\u26a0\ufe0f Setup incomplete: The poison alert alias **{aliasName}** does not exist.\n" +
                  $"Poison queue alerts will not be delivered until this alias is created.\n" +
                  $"Run `set-alias {aliasName}` in the channel where you want to receive operational alerts."
                : null;
        }

        _poisonAliasNudgeCache = nudge;
        _poisonAliasNudgeCacheExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        return nudge;
    }

    internal void InvalidatePoisonNudgeCacheIfMatch(string aliasName)
    {
        var configured = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        if (!string.IsNullOrEmpty(configured) &&
            string.Equals(aliasName, configured, StringComparison.OrdinalIgnoreCase))
        {
            _poisonAliasNudgeCacheExpiry = DateTimeOffset.MinValue;
        }
    }

    // --- Setup guide command ---

    private async Task HandleSetupGuideAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var apiAppId = Environment.GetEnvironmentVariable("ApiAppId") ?? "not-configured";
        var hostname = GetHostname();
        var cardJson = SetupGuideCardBuilder.Build(apiAppId, hostname);

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<object>(cardJson)
        };
        var activity = MessageFactory.Attachment(attachment);
        await turnContext.SendActivityAsync(activity, cancellationToken);
    }

    // --- Queue management commands ---

    private async Task HandleQueueStatusAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForQueueCommandsAsync(turnContext))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue commands require a valid Entra ID identity. Your account could not be verified."),
                cancellationToken);
            return;
        }

        if (_queueService == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue management is not available."),
                cancellationToken);
            return;
        }

        var status = await _queueService.GetQueueStatusAsync();
        var lines = status.Select(kv =>
        {
            var count = kv.Value >= 0 ? kv.Value.ToString() : "error";
            var indicator = kv.Key.EndsWith("-poison") && kv.Value > 0 ? " \u26a0\ufe0f" : "";
            return $"- **{kv.Key}**: {count}{indicator}";
        });

        await turnContext.SendActivityAsync(
            MessageFactory.Text($"**Queue Status:**\n\n{string.Join("\n", lines)}"),
            cancellationToken);
    }

    private async Task HandleQueuePeekAsync(
        ITurnContext turnContext, string command, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForQueueCommandsAsync(turnContext))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue commands require a valid Entra ID identity. Your account could not be verified."),
                cancellationToken);
            return;
        }

        if (_queueService == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue management is not available."),
                cancellationToken);
            return;
        }

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ValidPoisonQueues.Contains(parts[1]))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: **queue-peek** `<notifications-poison|botoperations-poison>` `[count]`"),
                cancellationToken);
            return;
        }

        var queueName = parts[1];
        var count = parts.Length > 2 && int.TryParse(parts[2], out var n) ? Math.Min(n, 32) : 5;

        var messages = await _queueService.PeekMessagesAsync(queueName, count);
        if (messages.Count == 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"**{queueName}** is empty."),
                cancellationToken);
            return;
        }

        var lines = messages.Select((m, i) =>
        {
            var excerpt = m.Body.ToString();
            if (excerpt.Length > 200) excerpt = excerpt[..200] + "...";
            return $"**{i + 1}.** `{excerpt}`";
        });

        await turnContext.SendActivityAsync(
            MessageFactory.Text($"**{queueName}** ({messages.Count} peeked):\n\n{string.Join("\n\n", lines)}"),
            cancellationToken);
    }

    private async Task HandleQueueRetryAsync(
        ITurnContext turnContext, string command, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForQueueCommandsAsync(turnContext))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue commands require a valid Entra ID identity. Your account could not be verified."),
                cancellationToken);
            return;
        }

        if (_queueService == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue management is not available."),
                cancellationToken);
            return;
        }

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ValidPoisonQueues.Contains(parts[1]))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: **queue-retry** `<notifications-poison|botoperations-poison>` `[count]`"),
                cancellationToken);
            return;
        }

        var queueName = parts[1];
        var count = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 1;

        var retried = await _queueService.RetryMessagesAsync(queueName, count);
        await turnContext.SendActivityAsync(
            MessageFactory.Text($"Retried **{retried}** message(s) from **{queueName}**."),
            cancellationToken);
    }

    private async Task HandleQueueRetryAllAsync(
        ITurnContext turnContext, string command, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForQueueCommandsAsync(turnContext))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue commands require a valid Entra ID identity. Your account could not be verified."),
                cancellationToken);
            return;
        }

        if (_queueService == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Queue management is not available."),
                cancellationToken);
            return;
        }

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ValidPoisonQueues.Contains(parts[1]))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: **queue-retry-all** `<notifications-poison|botoperations-poison>`"),
                cancellationToken);
            return;
        }

        var queueName = parts[1];
        var retried = await _queueService.RetryAllMessagesAsync(queueName);
        await turnContext.SendActivityAsync(
            MessageFactory.Text($"Retried all **{retried}** message(s) from **{queueName}**."),
            cancellationToken);
    }

    /// <summary>
    /// Checks whether the sender is authorized to run queue management commands.
    /// Any authenticated user (with a valid Entra ID) can run these commands in any scope.
    ///
    /// DEVIATION: The original spec (v1.4 §3.4) called for team-owner-only access in channels
    /// via TeamsInfo.GetTeamMemberAsync → UserRole == "owner". However, the Bot Framework
    /// Connector REST API never returns "owner" for userRole — it only distinguishes "user",
    /// "anonymous", and "guest" (despite the v4.9 SDK release notes documenting "owner" as
    /// a possible value). Implementing true owner checks requires Microsoft Graph API with
    /// GroupMember.Read.All permissions, which is out of scope for this POC.
    /// </summary>
    private Task<bool> IsAuthorizedForQueueCommandsAsync(ITurnContext turnContext)
    {
        var userId = turnContext.Activity.From?.AadObjectId;
        return Task.FromResult(!string.IsNullOrEmpty(userId));
    }

    // --- Create-alias card command ---

    private async Task HandleCreateAliasCardAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var conversationType = turnContext.Activity.Conversation?.ConversationType;
        var channelData = turnContext.Activity.GetChannelData<TeamsChannelData>();
        var channelName = channelData?.Channel?.Name;
        var userName = turnContext.Activity.From?.Name;

        // Channel name is often null in message activities — resolve from conversation reference or Teams API
        if (string.IsNullOrEmpty(channelName) && conversationType == "channel")
        {
            var channelId = channelData?.Channel?.Id ?? turnContext.Activity.Conversation?.Id?.Split(';')[0];
            var teamGroupId = await ResolveTeamGuidAsync(channelData?.Team);
            if (!string.IsNullOrEmpty(teamGroupId) && !string.IsNullOrEmpty(channelId))
            {
                var refEntity = await _botService.GetConversationReferenceEntityAsync(teamGroupId, channelId);
                channelName = refEntity?.ChannelName;
            }
        }

        var suggestedAlias = CreateAliasCardBuilder.DeriveAlias(conversationType, channelName, userName);
        var cardJson = CreateAliasCardBuilder.Build(suggestedAlias);

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<object>(cardJson)
        };
        var activity = MessageFactory.Attachment(attachment);
        await turnContext.SendActivityAsync(activity, cancellationToken);
    }

    /// <summary>
    /// Handles Adaptive Card Action.Submit payloads, which arrive as message activities
    /// with data in Activity.Value (not as adaptiveCard/action invokes).
    /// </summary>
    private async Task HandleCardSubmitAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var value = turnContext.Activity.Value;
        string? action = null;
        string? aliasName = null;
        string? aliasDescription = null;

        if (value is JsonElement jsonElement)
        {
            action = jsonElement.TryGetProperty("action", out var a) ? a.GetString() : null;
            aliasName = jsonElement.TryGetProperty("aliasName", out var n) ? n.GetString() : null;
            aliasDescription = jsonElement.TryGetProperty("aliasDescription", out var d) ? d.GetString() : null;
        }

        if (action != "createAlias")
        {
            _logger.LogWarning("Unknown card submit action: {Action}", action);
            return;
        }

        if (string.IsNullOrWhiteSpace(aliasName))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Alias name is required."), cancellationToken);
            return;
        }

        aliasName = aliasName.Trim().ToLowerInvariant();
        if (!AliasNameRegex.IsMatch(aliasName))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "Invalid alias name. Use 2-50 characters: lowercase letters, digits, hyphens. " +
                    "Must start and end with a letter or digit."),
                cancellationToken);
            return;
        }

        var (pk, rk, targetType) = await ExtractConversationKeysAsync(turnContext.Activity);
        if (pk == null || rk == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Could not determine conversation target."), cancellationToken);
            return;
        }

        var entity = new AliasEntity
        {
            TargetType = targetType,
            TeamId = targetType == "channel" ? pk : null,
            ChannelId = targetType == "channel" ? rk : null,
            UserId = targetType == "personal" ? rk : null,
            ChatId = targetType == "groupChat" ? rk : null,
            Description = aliasDescription?.Trim() ?? string.Empty,
            CreatedBy = turnContext.Activity.From?.AadObjectId ?? string.Empty,
            CreatedByName = turnContext.Activity.From?.Name ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _aliasService.SetAliasAsync(aliasName, entity);
        _logger.LogInformation("Alias '{Alias}' created via card by {User}", aliasName, entity.CreatedByName);

        var hostname = GetHostname();
        await turnContext.SendActivityAsync(
            MessageFactory.Text(
                $"Alias **{aliasName}** created for this {targetType}.\n\n" +
                $"Notify: `https://{hostname}/api/v1/notify/{aliasName}`"),
            cancellationToken);
    }

    protected override async Task<AdaptiveCardInvokeResponse> OnAdaptiveCardInvokeAsync(
        ITurnContext<IInvokeActivity> turnContext,
        AdaptiveCardInvokeValue invokeValue,
        CancellationToken cancellationToken)
    {
        var actionData = invokeValue?.Action?.Data;
        if (actionData == null)
        {
            return CreateAdaptiveCardResponse(400, "No action data provided.");
        }

        string? action = null;
        string? aliasName = null;
        string? aliasDescription = null;

        if (actionData is JsonElement jsonElement)
        {
            action = jsonElement.TryGetProperty("action", out var a) ? a.GetString() : null;
            aliasName = jsonElement.TryGetProperty("aliasName", out var n) ? n.GetString() : null;
            aliasDescription = jsonElement.TryGetProperty("aliasDescription", out var d) ? d.GetString() : null;
        }

        if (action != "createAlias")
        {
            return await base.OnAdaptiveCardInvokeAsync(turnContext, invokeValue!, cancellationToken);
        }

        // Validate alias name
        if (string.IsNullOrWhiteSpace(aliasName))
        {
            return CreateAdaptiveCardResponse(400, "Alias name is required.");
        }

        aliasName = aliasName.Trim().ToLowerInvariant();
        if (!AliasNameRegex.IsMatch(aliasName))
        {
            return CreateAdaptiveCardResponse(400,
                "Invalid alias name. Use 2-50 characters: lowercase letters, digits, hyphens. " +
                "Must start and end with a letter or digit.");
        }

        // Extract conversation keys
        var (pk, rk, targetType) = await ExtractConversationKeysAsync(turnContext.Activity);
        if (pk == null || rk == null)
        {
            return CreateAdaptiveCardResponse(400, "Could not determine conversation target.");
        }

        // Create the alias
        var entity = new AliasEntity
        {
            TargetType = targetType,
            TeamId = targetType == "channel" ? pk : null,
            ChannelId = targetType == "channel" ? rk : null,
            UserId = targetType == "personal" ? rk : null,
            ChatId = targetType == "groupChat" ? rk : null,
            Description = aliasDescription?.Trim() ?? string.Empty,
            CreatedBy = turnContext.Activity.From?.AadObjectId ?? string.Empty,
            CreatedByName = turnContext.Activity.From?.Name ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _aliasService.SetAliasAsync(aliasName, entity);
        _logger.LogInformation("Alias '{Alias}' created via card by {User}", aliasName, entity.CreatedByName);

        var hostname = GetHostname();
        return CreateAdaptiveCardResponse(200,
            $"Alias **{aliasName}** created for this {targetType}.\n\n" +
            $"Notify: `https://{hostname}/api/v1/notify/{aliasName}`");
    }

    private static AdaptiveCardInvokeResponse CreateAdaptiveCardResponse(int statusCode, string message)
    {
        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = new[]
            {
                new
                {
                    type = "TextBlock",
                    text = message,
                    wrap = true
                }
            }
        };

        return new AdaptiveCardInvokeResponse
        {
            StatusCode = statusCode,
            Type = "application/vnd.microsoft.card.adaptive",
            Value = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(card))
        };
    }

    // --- Delete post command ---

    private async Task HandleDeletePostAsync(
        ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var conversationType = turnContext.Activity.Conversation?.ConversationType;
        if (conversationType != "channel")
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("The **delete-post** command is only available in team channels."),
                cancellationToken);
            return;
        }

        var replyToId = turnContext.Activity.ReplyToId;

        // Fallback: extract root message ID from thread conversation ID.
        // Teams thread replies may not set ReplyToId, but the conversation ID
        // contains the root message ID as: 19:xxx@thread.tacv2;messageid=<id>
        if (string.IsNullOrEmpty(replyToId))
        {
            var conversationId = turnContext.Activity.Conversation?.Id;
            if (!string.IsNullOrEmpty(conversationId))
            {
                var match = Regex.Match(conversationId, @";messageid=(\d+)");
                if (match.Success)
                {
                    replyToId = match.Groups[1].Value;
                    _logger.LogDebug("Resolved replyToId from thread conversation ID: {ReplyToId}", replyToId);
                }
            }
        }

        if (string.IsNullOrEmpty(replyToId))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "Reply to a bot message and send **delete-post** to delete it."),
                cancellationToken);
            return;
        }

        try
        {
            await turnContext.DeleteActivityAsync(replyToId, cancellationToken);
            _logger.LogInformation("Deleted activity {ActivityId} requested by {UserId}",
                replyToId, turnContext.Activity.From?.AadObjectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete activity {ActivityId}", replyToId);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "Could not delete that message. I can only delete messages I sent."),
                cancellationToken);
        }
    }

    // --- Installation update (primary install/uninstall handler per MS docs) ---

    protected override async Task OnInstallationUpdateActivityAsync(
        ITurnContext<IInstallationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var action = turnContext.Activity.Action;
        var channelData = turnContext.Activity.GetChannelData<TeamsChannelData>();

        if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "add-upgrade", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBotInstalledAsync(turnContext, channelData, cancellationToken);
        }
        else if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, "remove-upgrade", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBotRemovedAsync(turnContext.Activity, channelData);
        }
    }

    // --- Conversation update: delegate to SDK overrides, suppress member dispatch ---

    // Install/uninstall handled via OnInstallationUpdateActivityAsync.
    // Suppress base member dispatch to prevent unnecessary HTTP calls for non-bot member events.
    protected override Task OnTeamsMembersAddedDispatchAsync(
        IList<ChannelAccount> membersAdded, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnTeamsMembersRemovedDispatchAsync(
        IList<ChannelAccount> membersRemoved, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        => Task.CompletedTask;

    // --- Channel event overrides (SDK dispatches from base OnConversationUpdateActivityAsync) ---

    protected override async Task OnTeamsChannelCreatedAsync(
        ChannelInfo channelInfo, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);
        var channelId = channelInfo?.Id;
        var channelName = channelInfo?.Name;

        if (string.IsNullOrEmpty(teamGuid) || string.IsNullOrEmpty(channelId)) return;

        _logger.LogInformation("Channel created: {ChannelName} ({ChannelId}) in team {TeamGuid}",
            channelName, channelId, teamGuid);

        var reference = turnContext.Activity.GetConversationReference();
        reference.Conversation = new ConversationAccount
        {
            Id = channelId,
            IsGroup = true,
            ConversationType = "channel",
            TenantId = reference.Conversation?.TenantId
        };

        await _botService.StoreConversationReferenceAsync(
            reference, teamGuid, channelId,
            "channel", teamInfo?.Name, channelName);
    }

    protected override async Task OnTeamsChannelDeletedAsync(
        ChannelInfo channelInfo, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);
        var channelId = channelInfo?.Id;

        if (string.IsNullOrEmpty(teamGuid) || string.IsNullOrEmpty(channelId)) return;

        _logger.LogInformation("Channel deleted: {ChannelName} ({ChannelId}) in team {TeamGuid}",
            channelInfo?.Name, channelId, teamGuid);

        await _botService.RemoveConversationReferenceAsync(teamGuid, channelId);
    }

    protected override async Task OnTeamsChannelRenamedAsync(
        ChannelInfo channelInfo, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);
        var channelId = channelInfo?.Id;
        var channelName = channelInfo?.Name;

        if (string.IsNullOrEmpty(teamGuid) || string.IsNullOrEmpty(channelId)) return;

        _logger.LogInformation("Channel renamed: {ChannelName} ({ChannelId}) in team {TeamGuid}",
            channelName, channelId, teamGuid);

        var reference = turnContext.Activity.GetConversationReference();
        reference.Conversation = new ConversationAccount
        {
            Id = channelId,
            IsGroup = true,
            ConversationType = "channel",
            TenantId = reference.Conversation?.TenantId
        };

        await _botService.StoreConversationReferenceAsync(
            reference, teamGuid, channelId,
            "channel", teamInfo?.Name, channelName);
    }

    protected override async Task OnTeamsChannelRestoredAsync(
        ChannelInfo channelInfo, TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);
        var channelId = channelInfo?.Id;
        var channelName = channelInfo?.Name;

        if (string.IsNullOrEmpty(teamGuid) || string.IsNullOrEmpty(channelId)) return;

        _logger.LogInformation("Channel restored: {ChannelName} ({ChannelId}) in team {TeamGuid}",
            channelName, channelId, teamGuid);

        var reference = turnContext.Activity.GetConversationReference();
        reference.Conversation = new ConversationAccount
        {
            Id = channelId,
            IsGroup = true,
            ConversationType = "channel",
            TenantId = reference.Conversation?.TenantId
        };

        await _botService.StoreConversationReferenceAsync(
            reference, teamGuid, channelId,
            "channel", teamInfo?.Name, channelName);
    }

    // --- Team event overrides ---

    protected override async Task OnTeamsTeamRenamedAsync(
        TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);
        var newTeamName = teamInfo?.Name;

        if (string.IsNullOrEmpty(teamGuid))
        {
            _logger.LogWarning("teamRenamed event but could not resolve team GUID");
            return;
        }

        _logger.LogInformation("Team renamed: {TeamGuid} \u2192 {NewName}", teamGuid, newTeamName);

        // Enqueue batch rename of conversation references (offloaded to avoid 15-second activity timeout)
        await EnqueueBotOperationAsync("rename_team", teamGuid, newTeamName);

        // Update teamlookup entry
        var teamThreadId = teamInfo?.Id;
        if (!string.IsNullOrEmpty(teamThreadId))
        {
            try
            {
                var response = await _teamLookupTable.GetEntityAsync<TeamLookupEntity>("teamlookup", teamThreadId);
                var lookup = response.Value;
                lookup.TeamName = newTeamName;
                await _teamLookupTable.UpdateEntityAsync(lookup, lookup.ETag);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("Team lookup not found for {ThreadId} during rename", teamThreadId);
            }
        }
    }

    protected override async Task OnTeamsTeamDeletedAsync(
        TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var teamGuid = await ResolveTeamGuidAsync(teamInfo);

        if (string.IsNullOrEmpty(teamGuid))
        {
            _logger.LogWarning("teamDeleted event but could not resolve team GUID");
            return;
        }

        _logger.LogInformation("Team deleted: {TeamGuid}", teamGuid);
        await EnqueueBotOperationAsync("remove_team_refs", teamGuid);
        await DeleteTeamLookupAsync(teamInfo?.Id);
    }

    // --- Private helpers: install/uninstall ---

    private async Task HandleBotInstalledAsync(
        ITurnContext turnContext, TeamsChannelData? channelData, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        var conversationType = activity.Conversation?.ConversationType ?? string.Empty;

        if (conversationType == "channel" || channelData?.Team != null)
        {
            // Bot added to a team
            var teamGuid = channelData?.Team?.AadGroupId;
            var teamName = channelData?.Team?.Name;
            var channelId = activity.Conversation?.Id;

            if (string.IsNullOrEmpty(teamGuid))
            {
                _logger.LogWarning("Bot installed in team but AadGroupId is null. Team.Id={TeamId}",
                    channelData?.Team?.Id);
                return;
            }

            _logger.LogInformation("Bot installed in team {TeamGuid} ({TeamName}), install channel {ChannelId}",
                teamGuid, teamName, channelId);

            // Store install channel reference (not necessarily General — user picks channel during install)
            var reference = activity.GetConversationReference();
            await _botService.StoreConversationReferenceAsync(
                reference, teamGuid, channelId!,
                "channel", teamName, channelData?.Channel?.Name);

            // Store team thread ID -> GUID lookup for channel events that lack aadGroupId
            var teamThreadId = channelData?.Team?.Id;
            if (!string.IsNullOrEmpty(teamThreadId))
            {
                await _teamLookupTable.UpsertEntityAsync(new TeamLookupEntity
                {
                    RowKey = teamThreadId,
                    TeamGuid = teamGuid,
                    TeamName = teamName
                });
                _logger.LogInformation("Stored team lookup: {ThreadId} -> {TeamGuid}", teamThreadId, teamGuid);
            }

            // Enqueue channel enumeration (offloaded to avoid 15-second activity timeout)
            var teamThreadId2 = channelData?.Team?.Id;
            await EnqueueBotOperationAsync("enumerate_channels", teamGuid, teamName, teamThreadId2,
                JsonSerializer.Serialize(reference));

            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"\ud83d\udc4b Hi! I'm the Teams Notification Bot (v{AppInfo.Version}).\n\n" +
                "I deliver notifications from external systems to Teams channels via named **aliases**.\n" +
                "Run **help** to get started."), cancellationToken);
        }
        else if (conversationType == "personal")
        {
            // Bot added to 1:1 chat
            var userId = activity.From?.AadObjectId;
            var userName = activity.From?.Name;

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("Bot installed in personal chat but From.AadObjectId is null");
                return;
            }

            _logger.LogInformation("Bot installed in personal chat with {UserName} ({UserId})", userName, userId);

            var reference = activity.GetConversationReference();
            await _botService.StoreConversationReferenceAsync(
                reference, "user", userId,
                "personal", userName: userName);

            var greeting = string.IsNullOrEmpty(userName) ? "Hi!" : $"Hi {userName}!";
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"\ud83d\udc4b {greeting} I'm the Teams Notification Bot (v{AppInfo.Version}).\n\n" +
                "I can send you direct notifications. Run **help** to learn more."), cancellationToken);
        }
        else if (conversationType == "groupChat")
        {
            // Bot added to group chat
            var chatId = activity.Conversation?.Id;

            if (string.IsNullOrEmpty(chatId))
            {
                _logger.LogWarning("Bot installed in group chat but Conversation.Id is null");
                return;
            }

            _logger.LogInformation("Bot installed in group chat {ChatId}", chatId);

            var reference = activity.GetConversationReference();
            await _botService.StoreConversationReferenceAsync(
                reference, "chat", chatId,
                "groupChat");

            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"\ud83d\udc4b Hi! I'm the Teams Notification Bot (v{AppInfo.Version}).\n\n" +
                "I can send notifications to this chat. Run **help** to learn more."), cancellationToken);
        }
    }

    private async Task HandleBotRemovedAsync(IActivity activity, TeamsChannelData? channelData)
    {
        var conversationType = activity.Conversation?.ConversationType ?? string.Empty;

        if (conversationType == "channel" || channelData?.Team != null)
        {
            var teamGuid = await ResolveTeamGuidAsync(channelData?.Team);
            if (!string.IsNullOrEmpty(teamGuid))
            {
                _logger.LogInformation("Bot removed from team {TeamGuid}", teamGuid);
                await EnqueueBotOperationAsync("remove_team_refs", teamGuid);
                await DeleteTeamLookupAsync(channelData?.Team?.Id);
            }
        }
        else if (conversationType == "personal")
        {
            var userId = activity.From?.AadObjectId;
            if (!string.IsNullOrEmpty(userId))
            {
                _logger.LogInformation("Bot removed from personal chat with {UserId}", userId);
                await _botService.RemoveConversationReferenceAsync("user", userId);
            }
        }
        else if (conversationType == "groupChat")
        {
            var chatId = activity.Conversation?.Id;
            if (!string.IsNullOrEmpty(chatId))
            {
                _logger.LogInformation("Bot removed from group chat {ChatId}", chatId);
                await _botService.RemoveConversationReferenceAsync("chat", chatId);
            }
        }
    }

    // --- Private helpers: queue operations ---

    private async Task EnqueueBotOperationAsync(
        string operation, string teamGuid, string? teamName = null,
        string? teamThreadId = null, string? serializedReference = null)
    {
        var message = new BotOperationMessage
        {
            Operation = operation,
            TeamGuid = teamGuid,
            TeamName = teamName,
            TeamThreadId = teamThreadId,
            SerializedReference = serializedReference,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        await _botOpsQueue.SendMessageAsync(JsonSerializer.Serialize(message));
        _logger.LogInformation("Enqueued bot operation: {Operation} for team {TeamGuid}", operation, teamGuid);
    }

    // --- Private helpers: conversation reference refresh ---

    private async Task AutoRefreshReferenceAsync(ITurnContext turnContext)
    {
        try
        {
            var activity = turnContext.Activity;
            var (pk, rk, targetType) = await ExtractConversationKeysAsync(activity);
            if (pk == null || rk == null) return;

            var reference = activity.GetConversationReference();

            // For channel messages in threads, strip ";messageid=..." from the conversation ID
            // so proactive notifications create new top-level posts instead of thread replies.
            // Clone the Conversation object — GetConversationReference() shares the same instance
            // as the activity, so mutating it would break downstream handlers (e.g. delete-post).
            if (targetType == "channel" && reference.Conversation?.Id != null)
            {
                var semicolonIndex = reference.Conversation.Id.IndexOf(';');
                if (semicolonIndex > 0)
                {
                    reference.Conversation = new ConversationAccount
                    {
                        Id = reference.Conversation.Id[..semicolonIndex],
                        IsGroup = reference.Conversation.IsGroup,
                        ConversationType = reference.Conversation.ConversationType,
                        TenantId = reference.Conversation.TenantId,
                    };
                }
            }

            var updated = await _botService.UpdateConversationReferenceAsync(reference, pk, rk);

            // For personal/groupChat, store if not found (first message before install event)
            if (!updated && targetType is "personal" or "groupChat")
            {
                await _botService.StoreConversationReferenceAsync(
                    reference, pk, rk, targetType,
                    userName: targetType == "personal" ? activity.From?.Name : null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-refresh conversation reference");
        }
    }

    // --- Private helpers: team GUID resolution ---

    private async Task<string?> ResolveTeamGuidAsync(TeamInfo? teamInfo)
    {
        // Prefer aadGroupId if present (reliable in membersAdded / bot install)
        var aadGroupId = teamInfo?.AadGroupId;
        if (!string.IsNullOrEmpty(aadGroupId))
            return aadGroupId;

        // Fall back to teamlookup table using team thread ID
        var teamThreadId = teamInfo?.Id;
        if (string.IsNullOrEmpty(teamThreadId))
        {
            _logger.LogWarning("Cannot resolve team GUID: both AadGroupId and Id are null");
            return null;
        }

        try
        {
            var response = await _teamLookupTable.GetEntityAsync<TeamLookupEntity>("teamlookup", teamThreadId);
            _logger.LogDebug("Resolved team thread ID {ThreadId} -> GUID {TeamGuid} via lookup table",
                teamThreadId, response.Value.TeamGuid);
            return response.Value.TeamGuid;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Team lookup not found for thread ID {ThreadId}. " +
                "Bot may need to be reinstalled in this team.", teamThreadId);
            return null;
        }
    }

    private async Task DeleteTeamLookupAsync(string? teamThreadId)
    {
        if (string.IsNullOrEmpty(teamThreadId)) return;

        try
        {
            await _teamLookupTable.DeleteEntityAsync("teamlookup", teamThreadId);
            _logger.LogInformation("Deleted team lookup for thread ID {ThreadId}", teamThreadId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Team lookup not found for thread ID {ThreadId} during deletion", teamThreadId);
        }
    }

    // --- Private helpers: conversation key extraction ---

    private async Task<(string? pk, string? rk, string targetType)> ExtractConversationKeysAsync(IActivity activity)
    {
        var channelData = activity.GetChannelData<TeamsChannelData>();
        var conversationType = activity.Conversation?.ConversationType ?? string.Empty;

        if (conversationType == "channel" || channelData?.Team != null)
        {
            var teamGuid = await ResolveTeamGuidAsync(channelData?.Team);
            // Use clean channel ID from channelData; fall back to conversation ID stripped of thread suffix
            var channelId = channelData?.Channel?.Id ?? activity.Conversation?.Id?.Split(';')[0];
            return (teamGuid, channelId, "channel");
        }
        else if (conversationType == "personal")
        {
            var userId = activity.From?.AadObjectId;
            return ("user", userId, "personal");
        }
        else if (conversationType == "groupChat")
        {
            var chatId = activity.Conversation?.Id;
            return ("chat", chatId, "groupChat");
        }

        return (null, null, string.Empty);
    }
}
