<#
.SYNOPSIS
    fabric-app/semantic-model/ の TMDL を Fabric のセマンティックモデルとして配置する。

.DESCRIPTION
    TMDL は scripts/gen-semantic-model.py が生成する。手で書かない。
    同名のモデルが既にあれば定義を更新し、無ければ新規作成する。

    前提:
      * az login 済みで、Fabric のテナントを向いていること
        （az account set --subscription <Fabric のサブスクリプション>）
      * fabric-app/scripts/semantic-model-views.sql を SQL 分析エンドポイントに
        適用済みであること。ビューが無いとモデルの検証で失敗する

.EXAMPLE
    pwsh ./scripts/deploy-semantic-model.ps1
#>
param(
    [string]$WorkspaceId = 'e2a48a60-0b5f-421f-91bb-51a33fe528bc',
    [string]$DisplayName = 'MimamoriTai',
    [string]$ModelPath
)

$ErrorActionPreference = 'Stop'

if (-not $ModelPath) {
    $ModelPath = Join-Path $PSScriptRoot '..\fabric-app\semantic-model'
}
$ModelPath = (Resolve-Path $ModelPath).Path
if (-not (Test-Path $ModelPath)) { throw "semantic-model folder not found: $ModelPath" }

$token = az account get-access-token --resource https://api.fabric.microsoft.com --query accessToken -o tsv
if (-not $token) { throw 'Could not acquire a Fabric token. Run `az login` first.' }
$headers = @{ Authorization = "Bearer $($token.Trim())"; 'Content-Type' = 'application/json' }

# TMDL のツリーをそのまま parts に写す。パス区切りは Fabric 側が / を要求する。
$parts = @()
Get-ChildItem $ModelPath -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($ModelPath.Length + 1).Replace('\', '/')
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $parts += @{
        path        = $rel
        payload     = [Convert]::ToBase64String($bytes)
        payloadType = 'InlineBase64'
    }
}
Write-Host "packed $($parts.Count) parts from $ModelPath"

function Invoke-FabricLro {
    param($Response, $Headers)
    # 作成・更新は長時間操作になることがある。202 が返ったら完了まで待つ。
    $op = $Response.Headers['x-ms-operation-id']
    if (-not $op) { return }
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        $s = Invoke-RestMethod -Headers $Headers -Uri "https://api.fabric.microsoft.com/v1/operations/$op"
        if ($s.status -in @('Succeeded', 'Completed')) { Write-Host "  operation $($s.status)"; return }
        if ($s.status -eq 'Failed') { throw "operation failed: $($s.error | ConvertTo-Json -Depth 5)" }
    }
    throw 'operation timed out'
}

$existing = Invoke-RestMethod -Headers $headers `
    -Uri "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels"
$model = $existing.value | Where-Object { $_.displayName -eq $DisplayName } | Select-Object -First 1

if ($model) {
    Write-Host "updating existing semantic model $($model.id)"
    $body = @{ definition = @{ parts = $parts } } | ConvertTo-Json -Depth 6 -Compress
    $r = Invoke-WebRequest -Method Post -Headers $headers `
        -Uri "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels/$($model.id)/updateDefinition" `
        -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Invoke-FabricLro -Response $r -Headers $headers
    $id = $model.id
} else {
    Write-Host "creating semantic model $DisplayName"
    $body = @{
        displayName = $DisplayName
        definition  = @{ parts = $parts }
    } | ConvertTo-Json -Depth 6 -Compress
    $r = Invoke-WebRequest -Method Post -Headers $headers `
        -Uri "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels" `
        -Body ([Text.Encoding]::UTF8.GetBytes($body))
    Invoke-FabricLro -Response $r -Headers $headers
    $after = Invoke-RestMethod -Headers $headers `
        -Uri "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels"
    $id = ($after.value | Where-Object { $_.displayName -eq $DisplayName } | Select-Object -First 1).id
}

Write-Host "semantic model ready: $DisplayName ($id)"
Write-Host "接続情報の入力が必要な場合は Fabric ポータルでモデルを開いて資格情報を設定してください。"
