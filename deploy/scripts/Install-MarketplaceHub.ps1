[CmdletBinding()]
param(
    [string] $ApplicationImage,
    [string] $EdgeImage,
    [string] $SiteAddress,
    [string] $OwnerEmail,
    [string] $OwnerPasswordFile,
    [string] $DockerExecutable,
    [switch] $Deploy,
    [switch] $Bootstrap
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Bootstrap -and -not $Deploy) { throw '-Bootstrap can only be used together with -Deploy.' }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$secretsRoot = Join-Path $repositoryRoot 'deploy\secrets'
$environmentFile = Join-Path $secretsRoot 'production.env'
$composeExecutable = Join-Path $env:LOCALAPPDATA 'Ravencia\tools\docker-compose-v2.40.2.exe'
$composeDownload = 'https://github.com/docker/compose/releases/download/v2.40.2/docker-compose-windows-x86_64.exe'
$composeSha256 = '1f7f20b91e0564147dc58b3a58a22a8f64a787e060ce3c25789f408beacc0c4d'

function Read-Required([string] $Prompt, [string] $CurrentValue) {
    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) { return $CurrentValue.Trim() }
    $value = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($value)) { throw "$Prompt is required." }
    return $value.Trim()
}

function Install-ExactCompose {
    if (Test-Path -LiteralPath $composeExecutable -PathType Leaf) {
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $composeExecutable).Hash.ToLowerInvariant()
        if ($actual -ne $composeSha256) {
            throw 'The existing Compose v2.40.2 path has an unexpected checksum; it was not overwritten.'
        }
        return
    }

    $toolsRoot = Split-Path -Parent $composeExecutable
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    $downloadTarget = "$composeExecutable.download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $composeDownload -OutFile $downloadTarget
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $downloadTarget).Hash.ToLowerInvariant()
        if ($actual -ne $composeSha256) { throw 'Downloaded Compose checksum does not match the pinned official checksum.' }
        Move-Item -LiteralPath $downloadTarget -Destination $composeExecutable
    }
    finally {
        if (Test-Path -LiteralPath $downloadTarget) { Remove-Item -LiteralPath $downloadTarget -Force }
    }
}

if ([string]::IsNullOrWhiteSpace($DockerExecutable)) {
    $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -ne $dockerCommand) { $DockerExecutable = $dockerCommand.Source }
}
if ([string]::IsNullOrWhiteSpace($DockerExecutable)) {
    $standardDocker = Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $standardDocker -PathType Leaf) { $DockerExecutable = $standardDocker }
}
if ([string]::IsNullOrWhiteSpace($DockerExecutable) -or -not (Test-Path -LiteralPath $DockerExecutable -PathType Leaf)) {
    throw 'Docker CLI was not found. Complete the Windows VPS Linux-container runtime runbook first, or pass -DockerExecutable.'
}
$dockerTarget = (& $DockerExecutable info --format '{{.OSType}}/{{.Architecture}}' 2>$null).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Docker Engine is not reachable.' }
if ($dockerTarget -ne 'linux/x86_64' -and $dockerTarget -ne 'linux/amd64') {
    throw "This release requires a Linux/amd64 Docker Engine; detected $dockerTarget."
}

Install-ExactCompose

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    $ApplicationImage = Read-Required 'Immutable application image (name@sha256:...)' $ApplicationImage
    $EdgeImage = Read-Required 'Immutable edge image (name@sha256:...)' $EdgeImage
    $SiteAddress = Read-Required 'Panel HTTPS address (https://panel.example.com)' $SiteAddress
    $OwnerEmail = Read-Required 'Initial Owner email' $OwnerEmail

    & (Join-Path $PSScriptRoot 'Initialize-VpsDeployment.ps1') `
        -ApplicationImage $ApplicationImage `
        -EdgeImage $EdgeImage `
        -SiteAddress $SiteAddress `
        -OwnerEmail $OwnerEmail `
        -GenerateDataProtectionCertificate `
        -OwnerPasswordFile $OwnerPasswordFile
    if ($LASTEXITCODE -ne 0) { throw 'Secure deployment initialization failed.' }
}
else {
    Write-Host 'Existing deploy/secrets/production.env was preserved; initialization was skipped.'
}

$deploymentArguments = @('-ComposeExecutable', $composeExecutable)
if (-not $Deploy) { $deploymentArguments += '-ValidateOnly' }
if ($Bootstrap) { $deploymentArguments += '-Bootstrap' }
& (Join-Path $PSScriptRoot 'Invoke-VpsDeployment.ps1') @deploymentArguments
if ($LASTEXITCODE -ne 0) { throw 'MarketplaceHub deployment command failed.' }

if (-not $Deploy) {
    Write-Host 'Preparation is complete. Re-run this installer with -Deploy -Bootstrap for the first empty installation.'
}
