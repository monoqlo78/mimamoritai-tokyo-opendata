#Requires -Version 7
<#
.SYNOPSIS
    Creates (or replaces) the MimamoriTai "one-touch" LINE rich menu and sets it as the
    channel-wide default, so an elderly resident can reach family with a single tap.

.DESCRIPTION
    Idempotent end-to-end setup against the LINE Messaging API:

      1. Produces a 2500x1686 PNG with 6 large tap areas — either generated locally with
         System.Drawing (Windows only, no NuGet/package dependency added to the app) or
         supplied via -ImagePath (validated for exact dimensions/type from the raw PNG
         header, which works on every OS with zero extra dependencies).
      2. Creates the rich menu object (POST /v2/bot/richmenu) with 5 postback actions
         and one URI action
         actions matching the webhook's LinePostbackActionService.
      3. Uploads the PNG as the rich menu's image (POST .../richmenu/{id}/content).
      4. Sets it as the default rich menu for every user (POST /v2/bot/user/all/richmenu/{id}).
      5. Verifies the default rich menu id matches what was just created.
      6. Only after verification, removes older MimamoriTai menus. A failed replacement
         therefore leaves the current working menu untouched.

    The 6 tap areas (2 rows x 3 columns) map to the exact postback.data values handled by
    LinePostbackActionService in src/MimamoriTai.Core/Application/LinePostbackActionService.cs:

      top-left      "助けて"       -> postback action=emergency
      top-middle    "体調が悪い"   -> postback action=unwell
      top-right     "大丈夫"       -> postback action=okay
      bottom-left   "今日の様子"   -> postback action=status
      bottom-middle "家族に連絡"   -> postback action=contact_family
      bottom-right  "Web版"        -> URI action opening the animated one-touch page

    The channel access token is never printed, logged, or written to disk by this script.

.PARAMETER ChannelAccessToken
    The LINE Messaging API channel access token (long-lived or short-lived). Mandatory.
    Pass it as a SecureString-friendly plain string from your shell; never hard-code it in
    a checked-in file. e.g.:
        $token = Read-Host -AsSecureString "Channel access token" | ConvertFrom-SecureString -AsPlainText
        ./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken $token

.PARAMETER ImagePath
    Optional path to an existing 2500x1686 PNG or JPEG to use instead of generating one.
    Required on non-Windows hosts (System.Drawing image generation is Windows-only).

.PARAMETER OutputImagePath
    Where a locally generated PNG is written. Defaults to scripts/generated/line-rich-menu.png
    (git-ignored). Ignored when -ImagePath is supplied.

.PARAMETER MenuName
    Rich menu name prefix used for both creation and idempotent cleanup of old menus.
    Must start with "MimamoriTai-".

.PARAMETER ApiBaseUrl
    LINE Messaging API base URL (management endpoints). Default https://api.line.me.

.PARAMETER ApiDataBaseUrl
    LINE Messaging API data endpoint (image upload). Default https://api-data.line.me.

.PARAMETER WebAppUrl
    Public HTTPS URL opened by the Web版 tile.

.EXAMPLE
    $token = Read-Host -AsSecureString "Channel access token" | ConvertFrom-SecureString -AsPlainText
    ./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken $token

.EXAMPLE
    ./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken $token -ImagePath ./assets/rich-menu.png
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ChannelAccessToken,

    [string]$ImagePath = (Join-Path $PSScriptRoot '../assets/line-rich-menu.png'),

    [string]$OutputImagePath = (Join-Path $PSScriptRoot 'generated/line-rich-menu.png'),

    [ValidatePattern('^MimamoriTai-')]
    [string]$MenuName = 'MimamoriTai-FriendlyGuardian',

    [string]$ApiBaseUrl = 'https://api.line.me',

    [string]$ApiDataBaseUrl = 'https://api-data.line.me',

    # 公開リポジトリなので本番URLは埋め込まない。
    # 環境変数 MIMAMORI_WEBAPP（App Service 名）か MIMAMORI_WEBAPP_URL（完全なURL）で渡す。
    [string]$WebAppUrl = $(
        if ($env:MIMAMORI_WEBAPP_URL) { "$($env:MIMAMORI_WEBAPP_URL.TrimEnd('/'))/one-touch" }
        elseif ($env:MIMAMORI_WEBAPP) { "https://$($env:MIMAMORI_WEBAPP).azurewebsites.net/one-touch" }
        else { '' }
    )
)

$ErrorActionPreference = 'Stop'

if ($WebAppUrl -notlike 'https://*') {
    Write-Host "ERROR: -WebAppUrl が https:// で始まっていません。環境変数 MIMAMORI_WEBAPP か MIMAMORI_WEBAPP_URL を設定するか、-WebAppUrl を渡してください。" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ChannelAccessToken)) {
    Write-Host "ERROR: -ChannelAccessToken must not be empty." -ForegroundColor Red
    exit 1
}

# ----------------------------------------------------------------------------
# Constants: rich menu geometry and the 6 one-touch actions.
# ----------------------------------------------------------------------------

$script:MenuWidth = 2500
$script:MenuHeight = 1686
$script:ColWidths = @(834, 833, 833)   # sums to 2500
$script:RowHeights = @(843, 843)        # sums to 1686

$script:Buttons = @(
    [ordered]@{ Label = '助けて';       Row = 0; Col = 0; Color = '#C62828'; Kind = 'postback'; Data = 'action=emergency' }
    [ordered]@{ Label = '体調が悪い';   Row = 0; Col = 1; Color = '#EF6C00'; Kind = 'postback'; Data = 'action=unwell' }
    [ordered]@{ Label = '大丈夫';       Row = 0; Col = 2; Color = '#2E7D32'; Kind = 'postback'; Data = 'action=okay' }
    [ordered]@{ Label = '今日の様子';   Row = 1; Col = 0; Color = '#1565C0'; Kind = 'postback'; Data = 'action=status' }
    [ordered]@{ Label = '家族に連絡';   Row = 1; Col = 1; Color = '#6A1B9A'; Kind = 'postback'; Data = 'action=contact_family' }
    [ordered]@{ Label = 'Web版';        Row = 1; Col = 2; Color = '#455A64'; Kind = 'uri';      Data = $WebAppUrl }
)

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------

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

    if ($null -eq $Content) {
        return ''
    }
    if ($Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString($Content)
    }
    return [string]$Content
}

function Test-SuccessStatus {
    param([int]$StatusCode)
    return ($StatusCode -ge 200 -and $StatusCode -lt 300)
}

function Invoke-LineRequest {
    <#
        Thin wrapper around Invoke-WebRequest for the LINE Messaging API that:
          - always sets the Authorization header (never echoed anywhere)
          - always uses -SkipHttpErrorCheck so non-2xx responses can be inspected/failed fast
          - normalizes the response body to a UTF-8 string
          - returns a pscustomobject with StatusCode, Body (raw string), and Json (parsed, or $null)
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$ContentType,
        $Body,
        [byte[]]$BinaryBody
    )

    $headers = @{
        Authorization = "Bearer $ChannelAccessToken"
    }

    $params = @{
        Method             = $Method
        Uri                = $Uri
        Headers            = $headers
        SkipHttpErrorCheck = $true
    }

    if ($null -ne $BinaryBody) {
        $params['Body'] = $BinaryBody
        $params['ContentType'] = $ContentType
    }
    elseif ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 10)
        $params['ContentType'] = if ($ContentType) { $ContentType } else { 'application/json' }
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

function Get-LineErrorMessage {
    param($Response)

    if ($Response.Json -and $Response.Json.message) {
        return $Response.Json.message
    }
    if (-not [string]::IsNullOrWhiteSpace($Response.Body)) {
        return $Response.Body
    }
    return "(no response body)"
}

# ----------------------------------------------------------------------------
# PNG validation — pure byte parsing of the IHDR chunk, no dependency needed.
# PNG layout: 8-byte signature, then a 4-byte length + 4-byte type ("IHDR") + data,
# where the first 4 bytes of IHDR data are width and the next 4 are height (big-endian).
# ----------------------------------------------------------------------------

function Get-PngDimensions {
    param([byte[]]$Bytes)

    $pngSignature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($Bytes.Length -lt 24) {
        return $null
    }
    for ($i = 0; $i -lt 8; $i++) {
        if ($Bytes[$i] -ne $pngSignature[$i]) {
            return $null
        }
    }

    $ihdrType = [Text.Encoding]::ASCII.GetString($Bytes, 12, 4)
    if ($ihdrType -ne 'IHDR') {
        return $null
    }

    $widthBytes = $Bytes[16..19]
    $heightBytes = $Bytes[20..23]
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($widthBytes)
        [Array]::Reverse($heightBytes)
    }

    return [pscustomobject]@{
        Width  = [BitConverter]::ToUInt32($widthBytes, 0)
        Height = [BitConverter]::ToUInt32($heightBytes, 0)
    }
}

function Confirm-RichMenuImage {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "Image file not found: $Path"
    }

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -notin @('.png', '.jpg', '.jpeg')) {
        Fail "Image must be a .png or .jpg/.jpeg file (got '$extension')."
    }

    $bytes = [IO.File]::ReadAllBytes($Path)

    if ($extension -eq '.png') {
        $dimensions = Get-PngDimensions -Bytes $bytes
        if ($null -eq $dimensions) {
            Fail "'$Path' does not look like a valid PNG file (bad signature/IHDR)."
        }
        if ($dimensions.Width -ne $script:MenuWidth -or $dimensions.Height -ne $script:MenuHeight) {
            Fail "'$Path' is $($dimensions.Width)x$($dimensions.Height); LINE rich menus require exactly ${script:MenuWidth}x${script:MenuHeight}."
        }
        Write-Info "Validated PNG: $($dimensions.Width)x$($dimensions.Height)."
    }
    else {
        # JPEG dimension parsing needs a full SOFx scanner; not worth the complexity here.
        # LINE itself will reject a wrongly-sized JPEG, so this is a size sanity check only.
        Write-Info "JPEG supplied — trusting caller for exact ${script:MenuWidth}x${script:MenuHeight} dimensions (LINE will reject a bad size)."
    }

    return $bytes
}

function New-RichMenuImageLocally {
    param([string]$OutputPath)

    Write-Step "Generating a ${script:MenuWidth}x${script:MenuHeight} rich menu PNG locally (System.Drawing)"

    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    }
    catch {
        Fail "System.Drawing is unavailable on this platform (Windows-only). Re-run with -ImagePath pointing to a pre-made ${script:MenuWidth}x${script:MenuHeight} PNG/JPEG."
    }

    $outputDir = Split-Path -Parent $OutputPath
    if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $bitmap = New-Object System.Drawing.Bitmap($script:MenuWidth, $script:MenuHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $fontFamily = 'Yu Gothic UI'
        try {
            $probe = New-Object System.Drawing.Font($fontFamily, 10)
            $probe.Dispose()
        }
        catch {
            $fontFamily = 'MS Gothic'
        }

        $font = New-Object System.Drawing.Font($fontFamily, 92, [System.Drawing.FontStyle]::Bold)
        $whiteBrush = [System.Drawing.Brushes]::White
        $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 4)
        $stringFormat = New-Object System.Drawing.StringFormat
        $stringFormat.Alignment = [System.Drawing.StringAlignment]::Center
        $stringFormat.LineAlignment = [System.Drawing.StringAlignment]::Center

        $colOffsets = @(0, $script:ColWidths[0], ($script:ColWidths[0] + $script:ColWidths[1]))
        $rowOffsets = @(0, $script:RowHeights[0])

        foreach ($button in $script:Buttons) {
            $x = $colOffsets[$button.Col]
            $y = $rowOffsets[$button.Row]
            $w = $script:ColWidths[$button.Col]
            $h = $script:RowHeights[$button.Row]

            $color = [System.Drawing.ColorTranslator]::FromHtml($button.Color)
            $brush = New-Object System.Drawing.SolidBrush($color)
            try {
                $rect = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
                $graphics.FillRectangle($brush, $rect)
                $graphics.DrawRectangle($borderPen, $rect)

                $textRect = New-Object System.Drawing.RectangleF($x, $y, $w, $h)
                $graphics.DrawString($button.Label, $font, $whiteBrush, $textRect, $stringFormat)
            }
            finally {
                $brush.Dispose()
            }
        }

        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    Write-Info "Saved: $OutputPath"
    return $OutputPath
}

# ----------------------------------------------------------------------------
# LINE Messaging API steps
# ----------------------------------------------------------------------------

function Remove-ExistingMimamoriTaiRichMenus {
    param(
        [string]$NamePrefix,
        [string]$KeepRichMenuId
    )

    Write-Step "Removing superseded rich menus named '$NamePrefix*'"

    $response = Invoke-LineRequest -Method GET -Uri "$ApiBaseUrl/v2/bot/richmenu/list"
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to list rich menus (HTTP $($response.StatusCode)): $(Get-LineErrorMessage $response)"
    }

    $existing = @($response.Json.richmenus | Where-Object {
        $_.name -like "$NamePrefix*" -and $_.richMenuId -ne $KeepRichMenuId
    })

    if ($existing.Count -eq 0) {
        Write-Info "No superseded '$NamePrefix*' rich menus found."
        return
    }

    foreach ($menu in $existing) {
        Write-Info "Deleting rich menu '$($menu.name)' (id: $($menu.richMenuId))"
        $deleteResponse = Invoke-LineRequest -Method DELETE -Uri "$ApiBaseUrl/v2/bot/richmenu/$($menu.richMenuId)"
        if (-not (Test-SuccessStatus -StatusCode $deleteResponse.StatusCode)) {
            Fail "Failed to delete rich menu $($menu.richMenuId) (HTTP $($deleteResponse.StatusCode)): $(Get-LineErrorMessage $deleteResponse)"
        }
    }

    Write-Info "Deleted $($existing.Count) old rich menu(s)."
}

function New-RichMenuAreas {
    $colOffsets = @(0, $script:ColWidths[0], ($script:ColWidths[0] + $script:ColWidths[1]))
    $rowOffsets = @(0, $script:RowHeights[0])

    $areas = @()
    foreach ($button in $script:Buttons) {
        $action = switch ($button.Kind) {
            'postback' {
                [ordered]@{
                    type        = 'postback'
                    data        = $button.Data
                    displayText = $button.Label
                }
            }
            'uri' {
                [ordered]@{
                    type = 'uri'
                    uri  = $button.Data
                }
            }
            default {
                [ordered]@{
                    type = 'message'
                    text = $button.Data
                }
            }
        }

        $areas += [ordered]@{
            bounds = [ordered]@{
                x      = $colOffsets[$button.Col]
                y      = $rowOffsets[$button.Row]
                width  = $script:ColWidths[$button.Col]
                height = $script:RowHeights[$button.Row]
            }
            action = $action
        }
    }

    return $areas
}

function New-RichMenu {
    param([string]$Name)

    Write-Step "Creating rich menu '$Name'"

    $body = [ordered]@{
        size     = [ordered]@{ width = $script:MenuWidth; height = $script:MenuHeight }
        selected = $true
        name     = $Name
        chatBarText = 'メニュー'
        areas    = New-RichMenuAreas
    }

    $response = Invoke-LineRequest -Method POST -Uri "$ApiBaseUrl/v2/bot/richmenu" -Body $body
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to create rich menu (HTTP $($response.StatusCode)): $(Get-LineErrorMessage $response)"
    }

    $richMenuId = $response.Json.richMenuId
    if ([string]::IsNullOrWhiteSpace($richMenuId)) {
        Fail "Rich menu creation succeeded but no richMenuId was returned. Body: $($response.Body)"
    }

    Write-Info "Created rich menu id: $richMenuId"
    return $richMenuId
}

function Set-RichMenuImage {
    param(
        [string]$RichMenuId,
        [byte[]]$ImageBytes,
        [string]$ImageContentType
    )

    Write-Step "Uploading rich menu image ($($ImageBytes.Length) bytes)"

    $response = Invoke-LineRequest -Method POST -Uri "$ApiDataBaseUrl/v2/bot/richmenu/$RichMenuId/content" -ContentType $ImageContentType -BinaryBody $ImageBytes
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to upload rich menu image (HTTP $($response.StatusCode)): $(Get-LineErrorMessage $response)"
    }

    Write-Info "Image uploaded."
}

function Set-DefaultRichMenu {
    param([string]$RichMenuId)

    Write-Step "Setting '$RichMenuId' as the default rich menu for all users"

    $response = Invoke-LineRequest -Method POST -Uri "$ApiBaseUrl/v2/bot/user/all/richmenu/$RichMenuId"
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to set default rich menu (HTTP $($response.StatusCode)): $(Get-LineErrorMessage $response)"
    }

    Write-Info "Default rich menu set."
}

function Confirm-DefaultRichMenu {
    param([string]$ExpectedRichMenuId)

    Write-Step "Verifying the default rich menu"

    $response = Invoke-LineRequest -Method GET -Uri "$ApiBaseUrl/v2/bot/user/all/richmenu"
    if (-not (Test-SuccessStatus -StatusCode $response.StatusCode)) {
        Fail "Failed to read the default rich menu (HTTP $($response.StatusCode)): $(Get-LineErrorMessage $response)"
    }

    $actual = $response.Json.richMenuId
    if ($actual -ne $ExpectedRichMenuId) {
        Fail "Default rich menu verification failed: expected '$ExpectedRichMenuId' but LINE reports '$actual'."
    }

    Write-Info "Confirmed: default rich menu id is '$actual'."
}

# ----------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------

Write-Host "MimamoriTai LINE rich menu setup" -ForegroundColor White
Write-Host "  Menu name:   $MenuName"
Write-Host "  API base:    $ApiBaseUrl"
Write-Host "  Data base:   $ApiDataBaseUrl"
Write-Host ""

if ($ImagePath) {
    Write-Step "Validating supplied image: $ImagePath"
    $imageBytes = Confirm-RichMenuImage -Path $ImagePath
    $imageContentType = if ([IO.Path]::GetExtension($ImagePath).ToLowerInvariant() -eq '.png') {
        'image/png'
    }
    else {
        'image/jpeg'
    }
}
else {
    $generatedPath = New-RichMenuImageLocally -OutputPath $OutputImagePath
    $imageBytes = Confirm-RichMenuImage -Path $generatedPath
    $imageContentType = 'image/png'
}

$richMenuId = New-RichMenu -Name $MenuName
Set-RichMenuImage -RichMenuId $richMenuId -ImageBytes $imageBytes -ImageContentType $imageContentType
Set-DefaultRichMenu -RichMenuId $richMenuId
Confirm-DefaultRichMenu -ExpectedRichMenuId $richMenuId
Remove-ExistingMimamoriTaiRichMenus -NamePrefix 'MimamoriTai-' -KeepRichMenuId $richMenuId

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Green
Write-Host " Rich menu setup finished: $richMenuId" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
