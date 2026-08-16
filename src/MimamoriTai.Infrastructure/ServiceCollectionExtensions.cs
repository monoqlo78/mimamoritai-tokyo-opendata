using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure.Line;
using MimamoriTai.Infrastructure.OpenData;
using MimamoriTai.Infrastructure.Security;

namespace MimamoriTai.Infrastructure;

/// <summary>Describes which real integrations are live, for display on the dashboard.</summary>
public sealed record IntegrationStatus(
    string DeviceProvider,
    bool SwitchBotConfigured,
    bool OrcaRouterConfigured,
    bool FabricConfigured,
    bool LineConfigured,
    bool EventhouseConfigured,
    string Database);

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Creates the <see cref="TokenCredential"/> used to authenticate Fabric Data Agent
    /// (and Eventhouse) calls: a normal Entra service principal (client credentials) when
    /// <see cref="FabricOptions.HasServicePrincipalCredentials"/> is true - required because
    /// Fabric Data Agent query auth does not support managed identities - otherwise
    /// <see cref="DefaultAzureCredential"/> for local dev (Azure CLI login) / Managed
    /// Identity fallback. Never logs or exposes the secret.
    /// </summary>
    public static TokenCredential CreateFabricTokenCredential(FabricOptions options) =>
        options.HasServicePrincipalCredentials
            ? new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret)
            : new DefaultAzureCredential();

    /// <summary>
    /// Registers every integration, always choosing a working mock when the real
    /// service is not configured, so the app runs end to end with zero secrets.
    /// </summary>
    public static IServiceCollection AddMimamoriTaiInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OrcaRouterOptions>(configuration.GetSection(OrcaRouterOptions.SectionName));
        services.Configure<SwitchBotOptions>(configuration.GetSection(SwitchBotOptions.SectionName));
        services.Configure<FabricOptions>(configuration.GetSection(FabricOptions.SectionName));
        services.Configure<LineOptions>(configuration.GetSection(LineOptions.SectionName));
        services.Configure<EventhouseOptions>(configuration.GetSection(EventhouseOptions.SectionName));
        services.Configure<EventStreamOptions>(configuration.GetSection(EventStreamOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<MimamoriDataProtectionOptions>(configuration.GetSection(MimamoriDataProtectionOptions.SectionName));
        services.Configure<FabricConsoleSyncOptions>(configuration.GetSection(FabricConsoleSyncOptions.SectionName));
        services.Configure<FabricPublishOptions>(configuration.GetSection(FabricPublishOptions.SectionName));
        services.Configure<OpenDataOptions>(configuration.GetSection(OpenDataOptions.SectionName));

        // Public open data: 環境省 WBGT + 気象庁 AMeDAS, used by the heatstroke rule.
        // Registered unconditionally because it needs no credentials at all -- the
        // provider itself returns null out of season or when the source is down, so the
        // rest of the app never has to know whether the figure was available.
        services.AddHttpClient<IHeatAdvisoryProvider, TokyoHeatAdvisoryProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        var connectionString = configuration.GetConnectionString("AppDb");

        services.AddDbContext<AppDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseSqlServer(connectionString, sql =>
                    sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.DefaultSchema));
            }
            else
            {
                // No connection string: fall back to a local SQLite file so the
                // hackathon demo runs with zero infrastructure setup.
                options.UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "mimamoritai-demo.db")}");
            }
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton(TimeProvider.System);

        // --- Data Protection / per-household credential encryption -----------
        // The key ring itself (used to encrypt/decrypt SwitchBotConnection rows)
        // is registered here; whether a *durable* key path is required is decided
        // and enforced (fail-fast) by the hosting Web project at startup, since
        // only it knows the current IWebHostEnvironment. Local dev with no
        // DataProtection:KeyDirectory configured keeps ASP.NET Core's own default
        // local key ring, which is fine for a single-machine demo box.
        var dataProtection = configuration.GetSection(MimamoriDataProtectionOptions.SectionName).Get<MimamoriDataProtectionOptions>()
            ?? new MimamoriDataProtectionOptions();

        var dataProtectionBuilder = services.AddDataProtection().SetApplicationName("MimamoriTai");
        if (dataProtection.IsDurablePathConfigured)
        {
            dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtection.KeyDirectory!));
        }

        services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();

        // --- Device provider -------------------------------------------------
        // Both providers are always registered; selection between them happens per
        // household at runtime via IDeviceProviderFactory + IDataSourceContext, not
        // once at startup, so a single running app can serve Sample (mock) households
        // and a user's Production (SwitchBot-backed, when configured) household side
        // by side.
        var switchBot = configuration.GetSection(SwitchBotOptions.SectionName).Get<SwitchBotOptions>() ?? new SwitchBotOptions();

        services.AddHttpClient<ISwitchBotClient, SwitchBotClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Named client reused by the household-scoped factory below (per-household
        // credentials, resolved at request time -- see HouseholdSwitchBotClientFactory).
        services.AddHttpClient(HouseholdSwitchBotClientFactory.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IHouseholdSwitchBotClientFactory, HouseholdSwitchBotClientFactory>();
        services.AddScoped<SwitchBotConnectionService>();

        services.AddSingleton<MockDeviceProvider>();
        services.AddScoped<SwitchBotDeviceProvider>();
        services.AddScoped<IDeviceProviderFactory, DeviceProviderFactory>();
        services.AddScoped<IDataSourceContext, DataSourceContext>();
        services.AddScoped<IDeviceProvider, DataSourceAwareDeviceProvider>();

        // --- AI router -------------------------------------------------------
        var orca = configuration.GetSection(OrcaRouterOptions.SectionName).Get<OrcaRouterOptions>() ?? new OrcaRouterOptions();

        if (orca.IsConfigured)
        {
            services.AddHttpClient<IAiRouterClient, OrcaRouterClient>(client =>
            {
                client.BaseAddress = new Uri(orca.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(orca.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IAiRouterClient, MockAiRouterClient>();
        }

        // --- Fabric Data Agent -----------------------------------------------
        // The MCP-backed client is registered only once Fabric is configured; until
        // then the mock reports IsConfigured = false and the orchestrator uses local data.
        var fabric = configuration.GetSection(FabricOptions.SectionName).Get<FabricOptions>() ?? new FabricOptions();

        if (fabric.IsConfigured)
        {
            // TryAddSingleton means this becomes the app-wide TokenCredential the first
            // time it runs; if Eventhouse direct ingestion is also configured (see below)
            // it reuses this same credential rather than registering its own. That is
            // intentional here: Fabric Data Agent query auth requires a normal service
            // principal (managed identities are unsupported), so when Fabric is enabled
            // with SP credentials configured, Eventhouse REST ingestion authenticates with
            // that same service principal instead of DefaultAzureCredential.
            services.TryAddSingleton<TokenCredential>(CreateFabricTokenCredential(fabric));
            services.AddHttpClient<IFabricDataAgentClient, FabricDataAgentMcpClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            });
        }
        else
        {
            services.AddSingleton<IFabricDataAgentClient, MockFabricDataAgentClient>();
        }

        // --- LINE ------------------------------------------------------------
        var line = configuration.GetSection(LineOptions.SectionName).Get<LineOptions>() ?? new LineOptions();

        if (line.IsConfigured)
        {
            services.AddHttpClient<ILineMessagingClient, LineMessagingClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddSingleton<ILineMessagingClient, MockLineMessagingClient>();
        }

        // Registered unconditionally: the verifier is what makes a LIFF view refuse to
        // show data, so it must exist even when LINE sending is not configured. With no
        // LiffChannelId it simply reports CanVerify=false and every token fails closed.
        services.AddHttpClient<ILineIdTokenVerifier, LineIdTokenVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Also unconditional: with the mock client it reports NotConfigured, which is
        // what lets the dashboard say "この画面の中だけのやり取りです" instead of
        // pretending a message left the building.
        services.AddScoped<LineConversationRelay>();

        // --- Fabric Eventstream / Eventhouse (real-time streaming ingestion) --
        // Preference order: EventStream (Event Hubs-protocol custom endpoint,
        // the primary ingestion path) > Eventhouse (direct KQL REST ingestion)
        // > Mock, so the app runs end to end with zero secrets either way.
        //
        // When both are configured the direct Eventhouse path is kept wired as a
        // fallback rather than discarded: the Eventstream is an extra hop that can
        // pause or throttle on its own, and both paths land in the same table.
        var eventStream = configuration.GetSection(EventStreamOptions.SectionName).Get<EventStreamOptions>() ?? new EventStreamOptions();
        var eventhouse = configuration.GetSection(EventhouseOptions.SectionName).Get<EventhouseOptions>() ?? new EventhouseOptions();

        if (eventStream.IsConfigured && eventhouse.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());

            // The Event Hubs producer is expensive to build and thread-safe, so it
            // stays a singleton; the typed HttpClient below is transient by design.
            services.AddSingleton<EventHubEventStreamPublisher>();
            services.AddHttpClient<EventhouseStreamPublisher>(client =>
            {
                client.BaseAddress = new Uri(eventhouse.ClusterUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(eventhouse.TimeoutSeconds);
            });

            services.AddTransient<IEventStreamPublisher>(sp => new FallbackEventStreamPublisher(
                sp.GetRequiredService<EventHubEventStreamPublisher>(),
                sp.GetRequiredService<EventhouseStreamPublisher>(),
                sp.GetRequiredService<ILogger<FallbackEventStreamPublisher>>()));
        }
        else if (eventStream.IsConfigured)
        {
            services.AddSingleton<IEventStreamPublisher, EventHubEventStreamPublisher>();
        }
        else if (eventhouse.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());
            services.AddHttpClient<IEventStreamPublisher, EventhouseStreamPublisher>(client =>
            {
                client.BaseAddress = new Uri(eventhouse.ClusterUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(eventhouse.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IEventStreamPublisher, MockEventStreamPublisher>();
        }

        // Plug Mini readings only ever go through Eventhouse (no EventHub-protocol
        // path for this stream, unlike DeviceEvents above) since the JSON REST
        // ingestion path is all that's needed for a low-volume per-poll-cycle
        // table; keeps this secondary stream simple and independently failable.
        if (eventhouse.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());
            services.AddHttpClient<IPlugMiniReadingStreamPublisher, EventhousePlugMiniReadingStreamPublisher>(client =>
            {
                client.BaseAddress = new Uri(eventhouse.ClusterUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(eventhouse.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IPlugMiniReadingStreamPublisher, MockPlugMiniReadingStreamPublisher>();
        }

        // Same Eventhouse, separate table and separate client: outdoor open data and
        // device telemetry arrive on independent cadences and must fail independently.
        if (eventhouse.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());
            services.AddHttpClient<IHeatReadingStreamPublisher, EventhouseHeatReadingStreamPublisher>(client =>
            {
                client.BaseAddress = new Uri(eventhouse.ClusterUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(eventhouse.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IHeatReadingStreamPublisher, MockHeatReadingStreamPublisher>();
        }

        services.AddScoped<HeatReadingService>();

        // --- Application services --------------------------------------------

        // Scoped, unlike the other publishers here, because it reads the app database
        // through IAppDbContext; the background service resolves it per cycle.
        var fabricConsoleSync = configuration
            .GetSection(FabricConsoleSyncOptions.SectionName)
            .Get<FabricConsoleSyncOptions>() ?? new FabricConsoleSyncOptions();

        if (fabricConsoleSync.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());

            // Deliberately not the shared TokenCredential: see FabricConsoleSyncCredential.
            services.TryAddSingleton(new FabricConsoleSyncCredential(new DefaultAzureCredential()));
            services.AddScoped<IFabricConsoleSync, FabricSqlConsoleSync>();
        }
        else
        {
            services.AddScoped<IFabricConsoleSync, MockFabricConsoleSync>();
        }

        services.AddScoped<ILocalDataQuestionService>(sp =>
            new LocalDataQuestionService(sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<ActivityService>();
        services.AddScoped<PowerUsageService>();
        services.AddScoped<RiskAssessmentService>();
        services.AddScoped<DeviceControlService>();

        // Singleton on purpose: a confirmation is proposed on one HTTP request and
        // answered on the next, so the pending action has to outlive the scope.
        services.AddSingleton<IPendingActionStore, InMemoryPendingActionStore>();

        // Built by hand so the Fabric budget stays configurable: the container cannot
        // supply a bare TimeSpan, and callers such as the LINE webhook enforce their own
        // deadline that a slow data agent must not be allowed to consume.
        services.AddScoped(sp => new AssistantOrchestrator(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IAiRouterClient>(),
            sp.GetRequiredService<IDeviceProvider>(),
            sp.GetRequiredService<IFabricDataAgentClient>(),
            sp.GetRequiredService<ILocalDataQuestionService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IPendingActionStore>(),
            TimeSpan.FromSeconds(Math.Max(1, fabric.QueryTimeoutSeconds)),
            sp.GetRequiredService<IGuardedActionNotifier>()));

        services.AddScoped<DeviceSyncService>();
        services.AddScoped<EventStreamPublishService>();
        services.AddScoped<PlugMiniReadingPublishService>();
        services.AddScoped<SwitchBotPollingCycleService>();
        services.AddScoped<SwitchBotWebhookIngestService>();
        services.AddScoped<DeviceInsightService>();
        services.AddScoped<DeviceInsightQuestionService>();

        // --- Multi-user / household access -------------------------------------
        // DevCurrentUserAccessor is the zero-configuration fallback: a single fixed
        // demo user, no login required. A later task swaps this registration for a
        // claims-based implementation (Entra External ID / LINE OIDC); nothing else
        // in the app needs to change, since every caller depends only on
        // ICurrentUserAccessor.
        services.AddScoped<ICurrentUserAccessor, DevCurrentUserAccessor>();
        services.AddScoped<HouseholdAccessService>();
        services.AddScoped<AdminAccessService>();

        // --- Watch/risk alert (LINE push) -------------------------------------
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LineOptions>>().Value;
            var threshold = Enum.TryParse<RiskLevel>(options.AlertRiskThreshold, ignoreCase: true, out var parsed)
                ? parsed
                : RiskLevel.Medium;

            return new WatchAlertSettings
            {
                ToId = options.AlertToId,
                Threshold = threshold,
                Cooldown = TimeSpan.FromHours(Math.Max(options.AlertCooldownHours, 0)),
                PublicBaseUrl = options.PublicBaseUrl
            };
        });
        services.AddScoped<ILineRecipientResolver, LineRecipientResolver>();
        services.AddScoped<IGuardedActionNotifier, LineGuardedActionNotifier>();
        services.AddScoped<WatchAlertService>();
        services.AddScoped<LinePostbackActionService>();
        services.AddScoped<LineLinkCodeService>();

        services.AddScoped(sp => new IntegrationStatus(
            sp.GetRequiredService<IDeviceProvider>().Kind.ToString(),
            sp.GetRequiredService<IOptions<SwitchBotOptions>>().Value.IsConfigured,
            sp.GetRequiredService<IAiRouterClient>().IsConfigured,
            sp.GetRequiredService<IFabricDataAgentClient>().IsConfigured,
            sp.GetRequiredService<ILineMessagingClient>().IsConfigured,
            sp.GetRequiredService<IEventStreamPublisher>().IsConfigured,
            string.IsNullOrWhiteSpace(connectionString) ? "SQLite (demo fallback)" : "SQL Server"));

        return services;
    }
}
