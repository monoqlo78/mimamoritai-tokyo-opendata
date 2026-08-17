using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Security;
using MimamoriTai.Web.Components;
using MimamoriTai.Web.Endpoints;
using MimamoriTai.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Pull every secret from Key Vault before anything reads configuration. No-op when
// KeyVault:Uri is unset, so `dotnet run` with zero configuration still works.
builder.AddMimamoriTaiKeyVault();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMimamoriTaiInfrastructure(builder.Configuration);
builder.Services.AddMimamoriTaiAuthentication(builder.Configuration);
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DeviceDetailService>();
builder.Services.AddScoped<DeviceSettingsService>();
builder.Services.AddScoped<AdminConsoleService>();
builder.Services.AddScoped<LiffSessionService>();
builder.Services.AddOpenApi();
builder.Services.AddConsoleQuestionSupport(builder.Configuration);
builder.Services.AddHostedService<WatchAlertBackgroundService>();
builder.Services.AddHostedService<SwitchBotPollingBackgroundService>();
builder.Services.AddHostedService<DemoDataTopUpBackgroundService>();
builder.Services.AddHostedService<EventStreamPublishBackgroundService>();
builder.Services.AddHostedService<PlugMiniReadingPublishBackgroundService>();
builder.Services.AddHostedService<FabricConsoleSyncBackgroundService>();
builder.Services.AddHostedService<HeatReadingCaptureBackgroundService>();

var app = builder.Build();

// Fail fast rather than silently running with an ephemeral Data Protection key
// ring: per-household SwitchBot credentials are encrypted with this key ring, so
// losing it on every restart/redeploy in a real (non-Development) environment
// would make every already-saved SwitchBotConnection permanently undecryptable.
// Local dev may rely on ASP.NET Core's own default local key ring.
if (!app.Environment.IsDevelopment())
{
    var dataProtectionOptions = app.Services.GetRequiredService<IOptions<MimamoriDataProtectionOptions>>().Value;
    if (!dataProtectionOptions.IsDurablePathConfigured)
    {
        throw new InvalidOperationException(
            "DataProtection:KeyDirectory must be configured with a durable, persistent path in non-Development " +
            "environments (see docs/SECURITY.md). Without it, the Data Protection key ring is ephemeral and every " +
            "per-household SwitchBot credential encrypted under it becomes unreadable after the next restart or deploy.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
else
{
    app.MapOpenApi();
}

app.UseMimamoriTaiForwardedHeaders();

// Status code pages re-execute the pipeline, which would turn API/webhook error
// codes into HTML responses. Restrict the friendly pages to browser navigation.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api")
        && !ctx.Request.Path.StartsWithSegments("/webhooks")
        && !ctx.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApiEndpoints();
app.MapWebhookEndpoints();
app.MapSimulatorEndpoints();
app.MapAlertEndpoints();
app.MapDeviceSyncEndpoints();
app.MapSwitchBotConnectionEndpoints();
app.MapAuthEndpoints();
app.MapFabricSyncEndpoints();
app.MapConsoleQuestionEndpoints();

await InitializeDatabaseAsync(app);

app.Run();

/// <summary>
/// Applies migrations when running against SQL Server, or creates the SQLite demo
/// database, and seeds demo data so the app is immediately usable.
/// </summary>
static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();

            // EnsureCreated never upgrades an existing file, so a demo database
            // created before a model change is missing the new tables or columns and
            // every query against them throws at runtime. The SQLite database is a
            // disposable demo artifact, so recreate it when it is out of date.
            var missing = await GetMissingSqliteObjectsAsync(db);
            if (missing.Count > 0)
            {
                logger.LogWarning(
                    "Demo database is out of date (missing: {Missing}). Recreating it.",
                    string.Join(", ", missing));
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }
        }

        await DemoDataSeeder.SeedAsync(db, clock);
        await DemoDataSeeder.TopUpAsync(db, clock);
        logger.LogInformation("Database ready. Provider: {Provider}", db.Database.ProviderName);
    }
    catch (Exception ex)
    {
        logger.LogError("Database initialization failed: {Type}. The app starts but data features are unavailable.", ex.GetType().Name);
    }
}

/// <summary>
/// Tables and columns the model expects but the SQLite demo file does not contain.
///
/// Checking tables alone is not enough: adding a property to an existing entity
/// leaves the table present but the column absent, and the failure then surfaces
/// only when a query touches it ("no such column: d.DisplayNameOverride"), long
/// after startup reported success.
/// </summary>
static async Task<List<string>> GetMissingSqliteObjectsAsync(AppDbContext db)
{
    var expected = db.Model.GetEntityTypes()
        .Select(t => (Table: t.GetTableName(), Type: t))
        .Where(x => !string.IsNullOrEmpty(x.Table))
        .ToList();

    var actual = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    await db.Database.OpenConnectionAsync();
    try
    {
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual[reader.GetString(0)] = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        foreach (var table in actual.Keys.ToList())
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();

            // pragma_table_info takes the name as a value, so it can be parameterised
            // instead of concatenated.
            command.CommandText = "SELECT name FROM pragma_table_info($table)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual[table].Add(reader.GetString(0));
            }
        }
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }

    var missing = new List<string>();

    foreach (var (table, entity) in expected)
    {
        if (!actual.TryGetValue(table!, out var columns))
        {
            missing.Add(table!);
            continue;
        }

        // Owned/split entities can share a table, so report per column rather than
        // assuming one entity owns every column of its table.
        missing.AddRange(entity.GetProperties()
            .Select(p => p.GetColumnName())
            .Where(c => !string.IsNullOrEmpty(c) && !columns.Contains(c))
            .Select(c => $"{table}.{c}"));
    }

    return missing.Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Exposed so tests can reference the generated entry point assembly.</summary>
public partial class Program;