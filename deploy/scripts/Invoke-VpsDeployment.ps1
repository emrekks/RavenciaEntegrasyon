[CmdletBinding()]
param(
    [string] $ComposeExecutable = "$env:LOCALAPPDATA\Ravencia\tools\docker-compose-v2.40.2.exe",
    [switch] $ValidateOnly,
    [switch] $Bootstrap
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$baseCompose = Join-Path $repositoryRoot 'deploy\compose\compose.yaml'
$productionCompose = Join-Path $repositoryRoot 'deploy\compose\compose.production.yaml'
$secretsRoot = Join-Path $repositoryRoot 'deploy\secrets'
$environmentFile = Join-Path $secretsRoot 'production.env'

if (-not (Test-Path -LiteralPath $ComposeExecutable -PathType Leaf)) {
    throw "Exact Compose executable was not found: $ComposeExecutable"
}

$requiredFiles = @(
    'postgres_password.txt',
    'app_db_connection.txt',
    'credential_key.txt',
    'bootstrap_owner_password.txt',
    'dp_certificate.pfx',
    'dp_certificate_password.txt',
    'production.env'
)
foreach ($name in $requiredFiles) {
    $path = Join-Path $secretsRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Required deployment file is missing or empty: deploy/secrets/$name"
    }
}

$environment = @{}
foreach ($line in Get-Content -LiteralPath $environmentFile) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -lt 1) { throw 'production.env contains an invalid line.' }
    $environment[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
}

foreach ($imageKey in @('MARKETPLACEHUB_APP_IMAGE', 'MARKETPLACEHUB_EDGE_IMAGE')) {
    if (-not $environment.ContainsKey($imageKey) -or $environment[$imageKey] -notmatch '^[^\s@]+@sha256:[0-9a-f]{64}$') {
        throw "$imageKey must be an immutable name@sha256:<64 lowercase hex> reference."
    }
}

$siteUri = $null
if (-not $environment.ContainsKey('MARKETPLACEHUB_SITE_ADDRESS') -or
    -not [Uri]::TryCreate($environment['MARKETPLACEHUB_SITE_ADDRESS'], [UriKind]::Absolute, [ref] $siteUri) -or
    $siteUri.Scheme -ne 'https' -or $siteUri.PathAndQuery -ne '/') {
    throw 'MARKETPLACEHUB_SITE_ADDRESS must be an HTTPS origin without a path or query.'
}

$credentialKey = [Convert]::FromBase64String((Get-Content -Raw -LiteralPath (Join-Path $secretsRoot 'credential_key.txt')).Trim())
if ($credentialKey.Length -ne 32) { throw 'credential_key.txt must contain exactly 32 random bytes encoded as Base64.' }

$connection = (Get-Content -Raw -LiteralPath (Join-Path $secretsRoot 'app_db_connection.txt')).Trim()
foreach ($requiredPart in @('Host=postgres', 'Database=marketplacehub', 'Username=marketplacehub')) {
    if ($connection -notlike "*$requiredPart*") { throw "app_db_connection.txt is missing required internal connection part: $requiredPart" }
}

$composeArguments = @('--env-file', $environmentFile, '-f', $baseCompose, '-f', $productionCompose)
$composeVersion = (& $ComposeExecutable @composeArguments version --short).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Compose version check failed.' }
if ($composeVersion -ne '2.40.2') { throw "Exact Compose 2.40.2 is required; detected $composeVersion." }
& $ComposeExecutable @composeArguments config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Production Compose validation failed.' }

Write-Host 'Production configuration passed local fail-closed validation.'
if ($ValidateOnly) { return }

& $ComposeExecutable @composeArguments pull postgres migrate api worker caddy
if ($LASTEXITCODE -ne 0) { throw 'Image pull failed; no services were changed.' }

& $ComposeExecutable @composeArguments up -d postgres migrate api worker caddy
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed. Inspect Compose service status and logs without printing secrets.' }

if ($Bootstrap) {
    & $ComposeExecutable @composeArguments run --rm -e Bootstrap__Enabled=true migrate api/MarketplaceHub.Api.dll bootstrap
    if ($LASTEXITCODE -ne 0) { throw 'Explicit initial Owner bootstrap failed.' }
}

$readyUri = [Uri]::new($siteUri, '/health/ready')
$response = Invoke-WebRequest -UseBasicParsing -Uri $readyUri -TimeoutSec 30
if ($response.StatusCode -ne 200) { throw "Readiness returned HTTP $($response.StatusCode)." }

& $ComposeExecutable @composeArguments ps
Write-Host "Deployment completed and readiness returned HTTP 200 at $readyUri"
