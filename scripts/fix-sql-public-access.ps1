<#
.SYNOPSIS
  サイトが 500 を返すようになったとき、まずこれを実行する。

.DESCRIPTION
  Azure SQL Server `sqldb-mngenv` の公衆ネットワークアクセスが
  ガバナンスポリシー `AzureSQL_PublicNetwork_Modify`
  （割り当て `MCAPSGovDeployPolicies`）によって自動的に無効化され、
  App Service から DB につながらなくなることがある。

  症状:
    サイトが HTTP 500
    ログに Error Number:47073
    "Connection was denied because Deny Public Network Access is set to Yes."

  この状態は自分たちの操作ミスではなく、ポリシーの再評価のたびに
  再発しうる。恒久対策は次のいずれかで、どれも判断が要る:

    1. `sql-group` にポリシー免除を申請する（ガバナンス側の承認が必要）
    2. App Service を B1 以上へ上げ、VNet 統合＋プライベートエンドポイントにする
       （現在は F1 で、無料プランでは VNet 統合が使えない）
    3. 発生したらこのスクリプトで戻す（ハッカソン期間中の現実解）

  このスクリプトは 3 のためのもの。読み取りだけして状態を報告し、
  無効化されていたときだけ有効に戻す。

  注意: `sqldb-mngenv` は見守り隊だけのサーバではない。
  同じサーバに他案件が同居しているので、ここで戻す以外の
  サーバ単位の設定変更を勝手に行わないこと。

.EXAMPLE
  pwsh ./scripts/fix-sql-public-access.ps1
  pwsh ./scripts/fix-sql-public-access.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'sql-group',
    [string]$ServerName = 'sqldb-mngenv',
    [string]$WebAppResourceGroup = $env:MIMAMORI_RG,
    [string]$WebAppName = $env:MIMAMORI_WEBAPP,

    # 状態を見るだけで、書き込みは一切しない。
    [switch]$CheckOnly,

    # 戻したあとにアプリを再起動しない（接続プールが掴んだままになるので通常は再起動する）。
    [switch]$SkipRestart
)

$ErrorActionPreference = 'Stop'

function Write-Step($message) { Write-Host "==> $message" }

$state = az sql server show -g $ResourceGroup -n $ServerName --query 'publicNetworkAccess' -o tsv
if (-not $state) {
    throw "サーバの状態を取得できなかった。az にログインしているか、サブスクリプションが合っているか確認する。"
}

Write-Step "$ServerName の公衆ネットワークアクセス: $state"

if ($state -eq 'Enabled') {
    Write-Host "問題なし。500 が続くなら原因は別にある（アプリのログを見ること）。"
    return
}

if ($CheckOnly) {
    Write-Warning "無効化されている。戻すには -CheckOnly を外して実行する。"
    return
}

Write-Step "有効に戻す"
az sql server update -g $ResourceGroup -n $ServerName --enable-public-network true --query 'publicNetworkAccess' -o tsv | Out-Null

$after = az sql server show -g $ResourceGroup -n $ServerName --query 'publicNetworkAccess' -o tsv
if ($after -ne 'Enabled') {
    throw "戻せなかった（現在: $after）。権限か、ポリシーの deny 効果を確認する。"
}
Write-Step "戻した: $after"

if (-not $SkipRestart) {
    # 接続プールが「つながらない」を掴んだままになるので、再起動して掴み直させる。
    Write-Step "$WebAppName を再起動"
    az webapp restart -g $WebAppResourceGroup -n $WebAppName | Out-Null
    Start-Sleep -Seconds 45
}

try {
    $response = Invoke-WebRequest "https://$WebAppName.azurewebsites.net" -TimeoutSec 120 -UseBasicParsing
    Write-Step "サイト: HTTP $($response.StatusCode)"
}
catch {
    Write-Warning "サイトがまだ応答しない: $($_.Exception.Message)"
    Write-Warning "起動に時間がかかっているだけのこともある。1 分ほど置いて再確認する。"
}
