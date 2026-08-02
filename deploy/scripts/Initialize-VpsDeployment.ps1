[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ApplicationImage,

    [Parameter(Mandatory)]
    [string] $EdgeImage,

    [Parameter(Mandatory)]
    [string] $SiteAddress,

    [Parameter(Mandatory)]
    [string] $OwnerEmail,

    [Parameter(Mandatory)]
    [string] $DataProtectionCertificatePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SingleLine([string] $Name, [string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Contains("`r") -or $Value.Contains("`n")) {
        throw "$Name must be a non-empty single-line value."
    }
}

function Assert-ImmutableImage([string] $Name, [string] $Value) {
    Assert-SingleLine $Name $Value
    if ($Value -notmatch '^[^\s@]+@sha256:[0-9a-f]{64}$') {
        throw "$Name must use an immutable name@sha256:<64 lowercase hex> reference."
    }
}

function New-RandomBase64([int] $ByteCount) {
    $bytes = [byte[]]::new($ByteCount)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function ConvertTo-PlainText([Security.SecureString] $Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Write-Secret([string] $Path, [string] $Value) {
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

Assert-ImmutableImage 'ApplicationImage' $ApplicationImage
Assert-ImmutableImage 'EdgeImage' $EdgeImage
Assert-SingleLine 'OwnerEmail' $OwnerEmail

$siteUri = $null
if (-not [Uri]::TryCreate($SiteAddress, [UriKind]::Absolute, [ref] $siteUri) -or
    $siteUri.Scheme -ne 'https' -or
    [string]::IsNullOrWhiteSpace($siteUri.Host) -or
    $siteUri.PathAndQuery -ne '/') {
    throw 'SiteAddress must be an HTTPS origin without a path or query, for example https://panel.example.com.'
}

$certificate = (Resolve-Path -LiteralPath $DataProtectionCertificatePath).Path
if ([IO.Path]::GetExtension($certificate) -ne '.pfx') {
    throw 'DataProtectionCertificatePath must point to a .pfx file.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$secretsRoot = Join-Path $repositoryRoot 'deploy\secrets'
$targets = @(
    'postgres_password.txt',
    'app_db_connection.txt',
    'credential_key.txt',
    'bootstrap_owner_password.txt',
    'dp_certificate.pfx',
    'dp_certificate_password.txt',
    'production.env'
) | ForEach-Object { Join-Path $secretsRoot $_ }

$existing = @($targets | Where-Object { Test-Path -LiteralPath $_ })
if ($existing.Count -gt 0) {
    throw 'Deployment secrets already exist. This initializer never overwrites or rotates existing secrets.'
}

$ownerPasswordSecure = Read-Host 'Temporary Owner password (15-64 characters; upper/lower/digit/symbol)' -AsSecureString
$ownerPasswordConfirm = Read-Host 'Repeat temporary Owner password' -AsSecureString
$certificatePasswordSecure = Read-Host 'Data Protection PFX password' -AsSecureString
$ownerPassword = ConvertTo-PlainText $ownerPasswordSecure
$ownerPasswordAgain = ConvertTo-PlainText $ownerPasswordConfirm
$certificatePassword = ConvertTo-PlainText $certificatePasswordSecure

try {
    if ($ownerPassword -cne $ownerPasswordAgain) { throw 'Owner passwords do not match.' }
    if ($ownerPassword.Length -lt 15 -or $ownerPassword.Length -gt 64 -or
        $ownerPassword -cnotmatch '[A-Z]' -or $ownerPassword -cnotmatch '[a-z]' -or
        $ownerPassword -notmatch '[0-9]' -or $ownerPassword -notmatch '[^A-Za-z0-9]') {
        throw 'Owner password does not satisfy the documented bootstrap policy.'
    }
    if ([string]::IsNullOrEmpty($certificatePassword)) { throw 'PFX password cannot be empty.' }

    $testCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificate,
        $certificatePassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if (-not $testCertificate.HasPrivateKey) { throw 'The Data Protection PFX does not contain a private key.' }
        if ($testCertificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'The Data Protection PFX is expired.' }
    }
    finally { $testCertificate.Dispose() }

    New-Item -ItemType Directory -Path $secretsRoot | Out-Null
    $postgresPassword = New-RandomBase64 32
    $credentialKey = New-RandomBase64 32
    $connection = "Host=postgres;Port=5432;Database=marketplacehub;Username=marketplacehub;Password=$postgresPassword"

    Write-Secret (Join-Path $secretsRoot 'postgres_password.txt') $postgresPassword
    Write-Secret (Join-Path $secretsRoot 'app_db_connection.txt') $connection
    Write-Secret (Join-Path $secretsRoot 'credential_key.txt') $credentialKey
    Write-Secret (Join-Path $secretsRoot 'bootstrap_owner_password.txt') $ownerPassword
    Copy-Item -LiteralPath $certificate -Destination (Join-Path $secretsRoot 'dp_certificate.pfx')
    Write-Secret (Join-Path $secretsRoot 'dp_certificate_password.txt') $certificatePassword

    $environment = @(
        "MARKETPLACEHUB_APP_IMAGE=$ApplicationImage",
        "MARKETPLACEHUB_EDGE_IMAGE=$EdgeImage",
        "MARKETPLACEHUB_SITE_ADDRESS=$($siteUri.GetLeftPart([UriPartial]::Authority))",
        'MARKETPLACEHUB_BOOTSTRAP_TENANT_CODE=ravencia',
        'MARKETPLACEHUB_BOOTSTRAP_TENANT_NAME=Ravencia',
        "MARKETPLACEHUB_BOOTSTRAP_OWNER_EMAIL=$OwnerEmail",
        'MARKETPLACEHUB_BOOTSTRAP_OWNER_NAME=Ravencia Admin'
    ) -join "`n"
    Write-Secret (Join-Path $secretsRoot 'production.env') ($environment + "`n")
}
catch {
    if (Test-Path -LiteralPath $secretsRoot) {
        foreach ($target in $targets) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        }
    }
    throw
}
finally {
    $ownerPassword = $null
    $ownerPasswordAgain = $null
    $certificatePassword = $null
}

Write-Host 'VPS deployment files were created under deploy/secrets without printing secret values.'
Write-Host 'Run Invoke-VpsDeployment.ps1 -ValidateOnly before the first deployment.'
