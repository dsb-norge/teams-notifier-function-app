using System.Text.Json;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Middleware;
using TeamsNotificationBot.Services;
using ThrottlingTroll;
using ThrottlingTroll.CounterStores.AzureTable;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(worker =>
    {
        worker.UseMiddleware<AuthMiddleware>();

        // Rate limiting (v1.4 §2) — keyed by EasyAuth principal ID, applied to API endpoints only
        var connStr = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        ICounterStore counterStore;
        if (!string.IsNullOrEmpty(connStr))
        {
            // Local development: Azurite
            counterStore = new AzureTableCounterStore(connStr, "ThrottlingTrollCounters");
        }
        else
        {
            // Azure: Managed Identity
            var acctName = Environment.GetEnvironmentVariable("StorageAccountName");
            var clientId = Environment.GetEnvironmentVariable("AzureWebJobsStorage__clientId");
            var cred = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = clientId
            });
            var tableServiceClient = new TableServiceClient(
                new Uri($"https://{acctName}.table.core.windows.net"), cred);
            counterStore = new AzureTableCounterStore(tableServiceClient, "ThrottlingTrollCounters");
        }

        var rateLimitPermit = int.TryParse(Environment.GetEnvironmentVariable("RateLimit__PermitLimit"), out var p) ? p : 60;
        var rateLimitInterval = int.TryParse(Environment.GetEnvironmentVariable("RateLimit__IntervalInSeconds"), out var i) ? i : 60;

        worker.UseThrottlingTroll(options =>
        {
            options.CounterStore = counterStore;
            options.Config = new ThrottlingTrollConfig
            {
                Rules =
                [
                    new ThrottlingTrollRule
                    {
                        LimitMethod = new FixedWindowRateLimitMethod
                        {
                            PermitLimit = rateLimitPermit,
                            IntervalInSeconds = rateLimitInterval
                        },
                        IdentityIdExtractor = request =>
                        {
                            if (request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-ID", out var principalId))
                            {
                                var value = principalId.ToString();
                                return !string.IsNullOrEmpty(value) ? value : null;
                            }
                            return null;
                        },
                        UriPattern = "/api/v1/.*"
                    }
                ]
            };

            options.ResponseFabric = async (checkResults, requestProxy, responseProxy, requestAborted) =>
            {
                var limitExceeded = checkResults
                    .OrderByDescending(r => r.RetryAfterInSeconds)
                    .FirstOrDefault(r => r.RequestsRemaining < 0);
                if (limitExceeded == null) return;

                responseProxy.StatusCode = 429;
                responseProxy.SetHttpHeader("Retry-After", limitExceeded.RetryAfterHeaderValue);
                responseProxy.SetHttpHeader("Content-Type", "application/problem+json");

                var problem = new
                {
                    type = "https://httpstatuses.io/429",
                    title = "Too Many Requests",
                    status = 429,
                    detail = $"Rate limit exceeded. Try again in {(int)limitExceeded.RetryAfterInSeconds} seconds.",
                    instance = requestProxy.UriWithoutQueryString
                };
                await responseProxy.WriteAsync(JsonSerializer.Serialize(problem));
            };
        });
    })
    .ConfigureAppConfiguration(config =>
    {
        // appsettings.json provides defaults: Logging, ConnectionsMap, Scopes, AuthType.
        // Hardcoded identity values are placeholders — overridden by env vars below.
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        // Environment variables override appsettings.json (last-registered source wins).
        config.AddEnvironmentVariables();

        // Map simple env vars to M365 Agents SDK config paths.
        var botAppId = Environment.GetEnvironmentVariable("BotAppId");
        var tenantId = Environment.GetEnvironmentVariable("TenantId");
        var uamiClientId = Environment.GetEnvironmentVariable("AzureWebJobsStorage__clientId");

        var overrides = new Dictionary<string, string?>();

        if (!string.IsNullOrEmpty(botAppId))
        {
            overrides["TokenValidation:Audiences:0"] = botAppId;
            overrides["Connections:ServiceConnection:Settings:ClientId"] = botAppId;
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            overrides["TokenValidation:TenantId"] = tenantId;
            overrides["Connections:ServiceConnection:Settings:AuthorityEndpoint"] =
                $"https://login.microsoftonline.com/{tenantId}";
        }

        if (!string.IsNullOrEmpty(uamiClientId))
        {
            overrides["Connections:ServiceConnection:Settings:FederatedClientId"] = uamiClientId;
        }

        if (overrides.Count > 0)
        {
            config.AddInMemoryCollection(overrides);
        }
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Override the Application Insights default logging filter that suppresses Info-level logs
        services.Configure<LoggerFilterOptions>(options =>
        {
            LoggerFilterRule? toRemove = options.Rules.FirstOrDefault(rule => rule.ProviderName
                == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (toRemove is not null)
            {
                options.Rules.Remove(toRemove);
            }
        });

        // M365 Agents SDK: HTTP clients (needed by MSAL and RestChannelServiceClientFactory)
        services.AddHttpClient();

        // M365 Agents SDK: outbound token acquisition (reads Connections/ConnectionsMap from config)
        services.AddDefaultMsalAuth(context.Configuration);

        // M365 Agents SDK: IConnections reads Connections/ConnectionsMap from config
        // (normally registered by AddAgentCore which requires IHostApplicationBuilder)
        services.AddSingleton<IConnections, ConfigurationConnections>();

        // M365 Agents SDK: IChannelServiceClientFactory creates ConnectorClient/UserTokenClient
        // (needed by CloudAdapter to send responses back through Bot Framework)
        services.AddSingleton<IChannelServiceClientFactory, RestChannelServiceClientFactory>();

        // M365 Agents SDK: CloudAdapter as IAgentHttpAdapter + dependencies
        services.AddCloudAdapter();

        // M365 Agents SDK: register the bot handler as the agent
        services.AddTransient<IAgent, TeamsBotHandler>();

        // M365 Agents SDK: inbound JWT validation for Bot Framework and Entra ID tokens
        services.AddAgentAspNetAuthentication(context.Configuration);

        // Storage clients (Table Storage + Queue)
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        var useConnectionString = !string.IsNullOrEmpty(connectionString);

        if (useConnectionString)
        {
            // Local development with Azurite
            var convRefClient = new TableClient(connectionString, "conversationreferences");
            var aliasClient = new TableClient(connectionString, "aliases");
            var teamLookupClient = new TableClient(connectionString, "teamlookup");
            convRefClient.CreateIfNotExists();
            aliasClient.CreateIfNotExists();
            teamLookupClient.CreateIfNotExists();
            services.AddSingleton(convRefClient);
            services.AddKeyedSingleton("teamlookup", teamLookupClient);
            services.AddSingleton<IAliasService>(new AliasService(aliasClient));
            services.AddSingleton(new QueueClient(connectionString, "notifications",
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("botoperations",
                new QueueClient(connectionString, "botoperations",
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("notifications-poison",
                new QueueClient(connectionString, "notifications-poison",
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("botoperations-poison",
                new QueueClient(connectionString, "botoperations-poison",
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            var idempotencyClient = new TableClient(connectionString, "idempotencykeys");
            idempotencyClient.CreateIfNotExists();
            services.AddSingleton<IIdempotencyService>(new IdempotencyService(idempotencyClient));
        }
        else
        {
            // Azure with Managed Identity
            var accountName = Environment.GetEnvironmentVariable("StorageAccountName");
            var managedIdentityClientId = Environment.GetEnvironmentVariable("AzureWebJobsStorage__clientId");

            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId
            });

            var tableUri = new Uri($"https://{accountName}.table.core.windows.net");
            var queueBaseUri = $"https://{accountName}.queue.core.windows.net";

            var convRefClient = new TableClient(tableUri, "conversationreferences", credential);
            var aliasClient = new TableClient(tableUri, "aliases", credential);
            var teamLookupClient = new TableClient(tableUri, "teamlookup", credential);
            convRefClient.CreateIfNotExists();
            aliasClient.CreateIfNotExists();
            teamLookupClient.CreateIfNotExists();
            services.AddSingleton(convRefClient);
            services.AddKeyedSingleton("teamlookup", teamLookupClient);
            services.AddSingleton<IAliasService>(new AliasService(aliasClient));

            services.AddSingleton(new QueueClient(
                new Uri($"{queueBaseUri}/notifications"),
                credential,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("botoperations",
                new QueueClient(
                    new Uri($"{queueBaseUri}/botoperations"),
                    credential,
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("notifications-poison",
                new QueueClient(
                    new Uri($"{queueBaseUri}/notifications-poison"),
                    credential,
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
            services.AddKeyedSingleton("botoperations-poison",
                new QueueClient(
                    new Uri($"{queueBaseUri}/botoperations-poison"),
                    credential,
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));

            var idempotencyClient = new TableClient(tableUri, "idempotencykeys", credential);
            idempotencyClient.CreateIfNotExists();
            services.AddSingleton<IIdempotencyService>(new IdempotencyService(idempotencyClient));
        }

        // Queue management service (for queue commands + poison queue monitoring)
        services.AddSingleton<IQueueManagementService, QueueManagementService>();

        // Bot service (uses CloudAdapter + TableClient for proactive messaging)
        services.AddSingleton<IBotService, BotService>();
    })
    .Build();

host.Run();
