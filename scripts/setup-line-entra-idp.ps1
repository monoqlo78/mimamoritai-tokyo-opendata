#Requires -Version 7
<#
.SYNOPSIS
    Wires up LINE Login as an OIDC identity provider in an Entra External ID (CIAM) tenant.

.DESCRIPTION
    Creating the LINE Login channel itself must be done manually in the LINE Developers
    Console (see docs/line-entra-setup.md). Once you have a Channel ID and Channel secret,
    this script automates everything else:

      1. Acquires a Microsoft Graph token scoped to the CIAM tenant via az cli.
      2. Checks whether an OIDC identity provider named "LINE" already exists.
      3. Creates it (or patches the existing one) with the correct LINE OIDC settings.
      4. Links the identity provider to the specified user flow.
      5. Verifies the user flow now lists LINE among its identity providers.
      6. Prints the redirect/callback URI candidates to paste into the LINE console.

    LINE's OIDC implementation does not support the `offline_access` scope, so the
    scope requested is deliberately `openid profile email`.

.PARAMETER LineChannelId
    The Channel ID of the LINE Login channel created in the LINE Developers Console.

.PARAMETER LineChannelSecret
    The Channel secret of the LINE Login channel. Never printed or logged.

.PARAMETER TenantId
    The Entra External ID (CIAM) tenant id.

.PARAMETER AppId
    The application (client) id of the web app registration that signs users in.

.PARAMETER UserFlowId
    The id of the existing sign-up/sign-in user flow to attach LINE to.

.EXAMPLE
    ./scripts/setup-line-entra-idp.ps1 -LineChannelId 1234567890 -LineChannelSecret 'xxxxxxxx'

.EXAMPLE
    ./scripts/setup-line-entra-idp.ps1 `
        -LineChannelId 1234567890 `
        -LineChannelSecret 'xxxxxxxx' `
        -TenantId '5ff64b34-cc0e-4813-9911-92968b7ff975' `
        -AppId 'dcc221af-ceb0-47fe-baac-837e8853423c' `
        -UserFlowId 'd06ea237-ed42-4f1c-8526-9d766b66d8f4'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LineChannelId,

    [Parameter(Mandatory = $true)]
    [string]$LineChannelSecret,

    [string]$TenantId = '5ff64b34-cc0e-4813-9911-92968b7ff975',

    [string]$AppId = 'dcc221af-ceb0-47fe-baac-837e8853423c',

    [string]$UserFlowId = 'd06ea237-ed42-4f1c-8526-9d766b66d8f4',

    # Optional pre-acquired Microsoft Graph access token. Supply this when the Azure CLI
    # cannot mint a token that carries IdentityProvider.ReadWrite.All (the CLI's first-party
    # client does not request that scope, so /identity/identityProviders returns HTTP 403).
    # Acquire one with the device code flow against a client that has the scope consented.
    [string]$AccessToken
)

$ErrorActionPreference = 'Stop'

$script:GraphBaseUrl = 'https://graph.microsoft.com/beta'
$script:LineIdpDisplayName = 'LINE'
$script:LineWellKnownEndpoint = 'https://access.line.me/.well-known/openid-configuration'
$script:LineIssuer = 'https://access.line.me'
$script:LineScope = 'openid profile email'

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Fail {
    param([string]$Message)
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function ConvertTo-Utf8String {
    <#
        Invoke-WebRequest's .Content can come back as byte[] depending on the response
        content type / PowerShell version. Normalize to a string before parsing JSON.
    #>
    param($Content)

    if ($Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString($Content)
    }
    return [string]$Content
}

function Get-GraphToken {
    param(
        [string]$TenantId,
        [string]$PreAcquiredToken
    )

    if (-not [string]::IsNullOrWhiteSpace($PreAcquiredToken)) {
        Write-Step "Using pre-acquired Microsoft Graph access token"
        return $PreAcquiredToken.Trim()
    }

    Write-Step "Acquiring Microsoft Graph access token for tenant $TenantId"

    $token = $null
    try {
        $token = az account get-access-token --resource https://graph.microsoft.com --tenant $TenantId -o tsv --query accessToken 2>$null
    }
    catch {
        $token = $null
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Host ""
        Write-Host "Could not obtain a Graph token for tenant $TenantId via az cli." -ForegroundColor Red
        Write-Host "Run the following, then re-run this script:" -ForegroundColor Yellow
        Write-Host "    az login --tenant $TenantId --allow-no-subscriptions" -ForegroundColor Yellow
        exit 1
    }

    Write-Info "Token acquired."
    return $token.Trim()
}

function Invoke-GraphRequest {
    <#
        Thin wrapper around Invoke-WebRequest that:
          - always sets the Authorization header
          - always uses -SkipHttpErrorCheck so we can inspect non-2xx responses ourselves
          - normalizes the response body to a UTF-8 string
          - returns a pscustomobject with StatusCode, Body (raw string), and Json (parsed, or $null)
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$Token,
        $Body
    )

    $headers = @{
        Authorization = "Bearer $Token"
    }

    $params = @{
        Method             = $Method
        Uri                = $Uri
        Headers            = $headers
        SkipHttpErrorCheck = $true
    }

    if ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 10)
        $params['ContentType'] = 'application/json'
    }

    $response = Invoke-WebRequest @params

    $bodyString = ConvertTo-Utf8String -Content $response.Content

    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($bodyString)) {
        try {
            $json = $bodyString | ConvertFrom-Json
        }
        catch {
            $json = $null
        }
    }

    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Body       = $bodyString
        Json       = $json
    }
}

function Test-SuccessStatus {
    param([int]$StatusCode)
    return ($StatusCode -ge 200 -and $StatusCode -lt 300)
}

function Get-GraphErrorMessage {
    param($Response)

    if ($Response.Json -and $Response.Json.error) {
        $code = $Response.Json.error.code
        $message = $Response.Json.error.message
        return "$code : $message"
    }
    return $Response.Body
}

function Find-LineIdentityProvider {
    param([string]$Token)

    Write-Step "Checking whether an identity provider named '$script:LineIdpDisplayName' already exists"

    $response = Invoke-GraphRequest -Method GET -Uri "$script:GraphBaseUrl/identity/identityProviders" -Token $Token

    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to list identity providers (HTTP $($response.StatusCode)): $(Get-GraphErrorMessage $response)"
    }

    $existing = $response.Json.value | Where-Object { $_.displayName -eq $script:LineIdpDisplayName }

    if ($existing) {
        Write-Info "Found existing identity provider '$script:LineIdpDisplayName' (id: $($existing.id))."
        return $existing
    }

    Write-Info "No existing '$script:LineIdpDisplayName' identity provider found."
    return $null
}

function New-LineIdentityProviderBody {
    param(
        [string]$ClientId,
        [string]$ClientSecret,
        [switch]$UseIssuer
    )

    $body = [ordered]@{
        '@odata.type'  = '#microsoft.graph.oidcIdentityProvider'
        displayName    = $script:LineIdpDisplayName
        clientId       = $ClientId
        clientSecret   = $ClientSecret
        scope          = $script:LineScope
        responseType   = 'code'
        wellKnownEndpoint = $script:LineWellKnownEndpoint
        inboundClaimMapping = [ordered]@{
            subject       = 'sub'
            displayName   = 'name'
            email         = 'email'
        }
    }

    if ($UseIssuer) {
        $body['issuer'] = $script:LineIssuer
    }

    return $body
}

function New-LineIdentityProvider {
    param(
        [string]$Token,
        [string]$ClientId,
        [string]$ClientSecret
    )

    Write-Step "Creating '$script:LineIdpDisplayName' OIDC identity provider"

    $primaryBody = New-LineIdentityProviderBody -ClientId $ClientId -ClientSecret $ClientSecret
    $response = Invoke-GraphRequest -Method POST -Uri "$script:GraphBaseUrl/identity/identityProviders" -Token $Token -Body $primaryBody

    if (Test-SuccessStatus -StatusCode $response.StatusCode) {
        Write-Info "Created using wellKnownEndpoint-only shape."
        return $response.Json
    }

    if ($response.StatusCode -eq 400) {
        Write-Info "wellKnownEndpoint-only shape failed with HTTP 400: $(Get-GraphErrorMessage $response)"
        Write-Info "Retrying with an additional 'issuer' property..."

        $fallbackBody = New-LineIdentityProviderBody -ClientId $ClientId -ClientSecret $ClientSecret -UseIssuer
        $fallbackResponse = Invoke-GraphRequest -Method POST -Uri "$script:GraphBaseUrl/identity/identityProviders" -Token $Token -Body $fallbackBody

        if (Test-SuccessStatus -StatusCode $fallbackResponse.StatusCode) {
            Write-Info "Created using wellKnownEndpoint + issuer shape."
            return $fallbackResponse.Json
        }

        Fail "Failed to create identity provider with both shapes. Last error (HTTP $($fallbackResponse.StatusCode)): $(Get-GraphErrorMessage $fallbackResponse)"
    }

    Fail "Failed to create identity provider (HTTP $($response.StatusCode)): $(Get-GraphErrorMessage $response)"
}

function Update-LineIdentityProvider {
    param(
        [string]$Token,
        [string]$IdpId,
        [string]$ClientId,
        [string]$ClientSecret
    )

    Write-Step "Updating existing '$script:LineIdpDisplayName' identity provider (id: $IdpId) with new client id/secret"

    $patchBody = [ordered]@{
        clientId     = $ClientId
        clientSecret = $ClientSecret
        scope        = $script:LineScope
        responseType = 'code'
    }

    $response = Invoke-GraphRequest -Method PATCH -Uri "$script:GraphBaseUrl/identity/identityProviders/$IdpId" -Token $Token -Body $patchBody

    # PATCH on identityProviders typically returns 204 No Content on success.
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to update identity provider (HTTP $($response.StatusCode)): $(Get-GraphErrorMessage $response)"
    }

    Write-Info "Identity provider updated."
}

function Add-IdentityProviderToUserFlow {
    param(
        [string]$Token,
        [string]$UserFlowId,
        [string]$IdpId
    )

    Write-Step "Linking identity provider to user flow $UserFlowId"

    $uri = "$script:GraphBaseUrl/identity/authenticationEventsFlows/$UserFlowId/microsoft.graph.externalUsersSelfServiceSignUpEventsFlow/onAuthenticationMethodLoadStart/microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp/identityProviders/`$ref"

    $body = [ordered]@{
        '@odata.id' = "$script:GraphBaseUrl/identity/identityProviders/$IdpId"
    }

    $response = Invoke-GraphRequest -Method POST -Uri $uri -Token $Token -Body $body

    if (Test-SuccessStatus -StatusCode $response.StatusCode) {
        Write-Info "Identity provider linked to user flow."
        return
    }

    if ($response.StatusCode -eq 400 -or $response.StatusCode -eq 409) {
        $errorText = Get-GraphErrorMessage $response
        if ($errorText -match 'already' -or $errorText -match 'exist') {
            Write-Info "Identity provider already linked to user flow (HTTP $($response.StatusCode)): $errorText"
            return
        }
    }

    Fail "Failed to link identity provider to user flow (HTTP $($response.StatusCode)): $(Get-GraphErrorMessage $response)"
}

function Confirm-UserFlowIdentityProviders {
    param(
        [string]$Token,
        [string]$UserFlowId
    )

    Write-Step "Verifying user flow's linked identity providers"

    $uri = "$script:GraphBaseUrl/identity/authenticationEventsFlows/$UserFlowId/microsoft.graph.externalUsersSelfServiceSignUpEventsFlow/onAuthenticationMethodLoadStart/microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp/identityProviders"

    $response = Invoke-GraphRequest -Method GET -Uri $uri -Token $Token

    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Write-Info "Could not re-read user flow identity providers directly (HTTP $($response.StatusCode)): $(Get-GraphErrorMessage $response)"
        Write-Info "Falling back to reading the full user flow with `$expand..."

        $fallbackUri = "$script:GraphBaseUrl/identity/authenticationEventsFlows/$UserFlowId`?`$expand=onAuthenticationMethodLoadStart"
        $fallbackResponse = Invoke-GraphRequest -Method GET -Uri $fallbackUri -Token $Token

        if (-not (Test-SuccessStatus -StatusCode $fallbackResponse.StatusCode)) {
            Fail "Failed to verify user flow identity providers (HTTP $($fallbackResponse.StatusCode)): $(Get-GraphErrorMessage $fallbackResponse)"
        }

        $names = @()
        $step = $fallbackResponse.Json.onAuthenticationMethodLoadStart
        if ($step -and $step.identityProviders) {
            $names = $step.identityProviders | ForEach-Object { $_.displayName }
        }
        return $names
    }

    $names = @()
    if ($response.Json -and $response.Json.value) {
        $names = $response.Json.value | ForEach-Object { $_.displayName }
    }
    return $names
}

# ----------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------

Write-Host "LINE Login -> Entra External ID identity provider setup" -ForegroundColor White
Write-Host "  Tenant id:     $TenantId"
Write-Host "  App id:        $AppId"
Write-Host "  User flow id:  $UserFlowId"
Write-Host "  LINE channel:  $LineChannelId"
Write-Host ""

$token = Get-GraphToken -TenantId $TenantId -PreAcquiredToken $AccessToken

$existingIdp = Find-LineIdentityProvider -Token $token

if ($existingIdp) {
    Update-LineIdentityProvider -Token $token -IdpId $existingIdp.id -ClientId $LineChannelId -ClientSecret $LineChannelSecret
    $idpId = $existingIdp.id
}
else {
    $created = New-LineIdentityProvider -Token $token -ClientId $LineChannelId -ClientSecret $LineChannelSecret
    if (-not $created -or -not $created.id) {
        Fail "Identity provider creation did not return an id. Response did not include an 'id' field."
    }
    $idpId = $created.id
    Write-Info "Created identity provider id: $idpId"
}

Add-IdentityProviderToUserFlow -Token $token -UserFlowId $UserFlowId -IdpId $idpId

$linkedNames = Confirm-UserFlowIdentityProviders -Token $token -UserFlowId $UserFlowId

Write-Step "Verification complete"
if ($linkedNames -and $linkedNames.Count -gt 0) {
    Write-Info "Identity providers currently enabled on this user flow:"
    foreach ($name in $linkedNames) {
        Write-Info "  - $name"
    }
    if ($linkedNames -notcontains $script:LineIdpDisplayName) {
        Write-Host "WARNING: '$script:LineIdpDisplayName' was not found in the verified list. Check the Entra portal manually." -ForegroundColor Yellow
    }
}
else {
    Write-Host "WARNING: Could not determine the linked identity providers list. Check the Entra portal manually." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Green
Write-Host " LINE identity provider setup finished." -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next: register the LINE callback URL in the LINE Developers Console." -ForegroundColor Yellow
Write-Host "Microsoft varies the exact callback host/path shown in the Entra portal per tenant." -ForegroundColor Yellow
Write-Host "Open Entra portal > External Identities > All identity providers > LINE, and copy the" -ForegroundColor Yellow
Write-Host "'Callback URL' shown there. Two commonly seen shapes are:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  1) Tenant-subdomain federation form:" -ForegroundColor Gray
Write-Host "     https://<tenant-subdomain>.ciamlogin.com/$TenantId/federation/oidc/access.line.me" -ForegroundColor White
Write-Host ""
Write-Host "  2) Generic External ID OAuth2 callback form:" -ForegroundColor Gray
Write-Host "     https://contsoexternal.ciamlogin.com/$TenantId/federation/oauth2" -ForegroundColor White
Write-Host ""
Write-Host "Use whichever one the Entra portal actually displays for the LINE provider." -ForegroundColor Yellow
Write-Host ""
if ($env:MIMAMORI_WEBAPP) {
    Write-Host "App URL for reference: https://$($env:MIMAMORI_WEBAPP).azurewebsites.net" -ForegroundColor Gray
}
