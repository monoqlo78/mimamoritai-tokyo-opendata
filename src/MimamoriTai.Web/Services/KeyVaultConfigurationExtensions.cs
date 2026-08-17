using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Loads every secret out of Azure Key Vault into the configuration at startup, so
/// no API key, connection string or client secret has to exist as a plain value in
/// App Service settings, in appsettings.json, or anywhere in the repository.
///
/// Authentication is passwordless: <see cref="DefaultAzureCredential"/> resolves to the
/// Web App's system-assigned managed identity in Azure (granted "Key Vault Secrets User")
/// and to the developer's own az login / Visual Studio account locally, so the same code
/// path works in both places without a bootstrap secret.
///
/// Secret names use the standard double-dash convention that the Key Vault configuration
/// provider maps onto the configuration hierarchy: <c>AzureModelRouter--ApiKey</c> becomes
/// <c>AzureModelRouter:ApiKey</c>. Because this provider is added last it wins over
/// appsettings.json, and because it is registered only when <c>KeyVault:Uri</c> is set,
/// the app still starts with zero configuration (all-mock demo mode) when it is absent.
///
/// The very first load happens synchronously inside <c>AddAzureKeyVault</c>, so a vault that
/// is temporarily unreachable (network policy flipping <c>publicNetworkAccess</c> to disabled,
/// a private endpoint whose DNS has not propagated yet, a managed identity that lost its role
/// assignment) used to abort the process before it could serve a single request, which App
/// Service then turns into a hard 503 after a few consecutive cold-start failures. That is a
/// worse outcome than starting without the vault: the app already fails fast, per feature, on
/// any secret it genuinely needs. So the load is best-effort and a failure is only reported.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    public const string UriKey = "KeyVault:Uri";

    public static WebApplicationBuilder AddMimamoriTaiKeyVault(this WebApplicationBuilder builder)
        => builder.AddMimamoriTaiKeyVault(AddAzureKeyVaultProvider, WriteStartupWarning);

    internal static WebApplicationBuilder AddMimamoriTaiKeyVault(
        this WebApplicationBuilder builder,
        Action<WebApplicationBuilder, Uri> addProvider,
        Action<string> reportFailure)
    {
        var uri = builder.Configuration[UriKey];

        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var vaultUri))
        {
            return builder;
        }

        try
        {
            addProvider(builder, vaultUri);
        }
        catch (Exception ex)
        {
            reportFailure(
                $"Key Vault '{vaultUri}' could not be read at startup ({ex.GetType().Name}: {ex.Message}). " +
                "Continuing without it: configuration falls back to app settings, and any feature whose " +
                "secret is missing stays disabled instead of taking the whole site down.");
        }

        return builder;
    }

    private static void AddAzureKeyVaultProvider(WebApplicationBuilder builder, Uri vaultUri)
    {
        var client = new SecretClient(vaultUri, new DefaultAzureCredential());

        builder.Configuration.AddAzureKeyVault(
            client,
            new AzureKeyVaultConfigurationOptions
            {
                // Picks up rotated values without a redeploy. Any failure to reload is
                // non-fatal: the previously loaded values stay in effect.
                ReloadInterval = TimeSpan.FromMinutes(30)
            });
    }

    private static void WriteStartupWarning(string message)
        => Console.Error.WriteLine($"warn: {typeof(KeyVaultConfigurationExtensions).FullName}[0]{Environment.NewLine}      {message}");
}
