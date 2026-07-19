param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("stable", "testing")]
    [string] $Channel,

    [Parameter(Mandatory = $true)]
    [string] $PluginManifestPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [Parameter(Mandatory = $true)]
    [string] $RepositoryUrl,

    [AllowEmptyString()]
    [string] $ReleaseNotes = "",

    [string] $ReleaseUpdatedAt,

    [string] $ExistingRepoJsonPath
)

$ErrorActionPreference = "Stop"

function Read-Manifest([string] $Path)
{
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path))
    {
        return $null
    }

    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($value -is [array])
    {
        return $value | Select-Object -First 1
    }

    return $value
}

function Set-Property($Object, [string] $Name, $Value)
{
    if ($Object.PSObject.Properties[$Name])
    {
        $Object.$Name = $Value
    }
    else
    {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Copy-Property($Target, $Source, [string] $Name)
{
    if ($null -ne $Source -and $Source.PSObject.Properties[$Name])
    {
        Set-Property $Target $Name $Source.$Name
        return $true
    }

    return $false
}

function ConvertTo-NormalizedVersion([string] $Value)
{
    $parsed = [Version]::Parse($Value)
    $revision = if ($parsed.Revision -lt 0) { 0 } else { $parsed.Revision }
    return [Version]::new($parsed.Major, $parsed.Minor, $parsed.Build, $revision)
}

$pluginManifest = Read-Manifest $PluginManifestPath
if ($null -eq $pluginManifest)
{
    throw "Could not read the built plugin manifest at '$PluginManifestPath'."
}

$existingManifest = Read-Manifest $ExistingRepoJsonPath
$releaseVersion = ConvertTo-NormalizedVersion $Version
$builtVersion = ConvertTo-NormalizedVersion $pluginManifest.AssemblyVersion
if ($builtVersion -ne $releaseVersion)
{
    throw "Built manifest version '$($pluginManifest.AssemblyVersion)' does not match release version '$Version'."
}

$normalizedVersion = $releaseVersion.ToString()

$downloadUrl = "$RepositoryUrl/releases/download/$Tag/Pulsar.zip"
$lastUpdate = if ([string]::IsNullOrWhiteSpace($ReleaseUpdatedAt))
{
    [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}
else
{
    [DateTimeOffset]::Parse($ReleaseUpdatedAt).ToUnixTimeSeconds()
}

if ($Channel -eq "testing")
{
    if ($null -eq $existingManifest)
    {
        # Bootstrap a testing-only repository. Dalamud requires a normal version
        # even for this case, and IsTestingExclusive keeps it hidden from users
        # who have not enabled testing plugins.
        $manifest = $pluginManifest
        Set-Property $manifest "Changelog" $ReleaseNotes
        Set-Property $manifest "DownloadLinkInstall" $downloadUrl
        Set-Property $manifest "DownloadLinkUpdate" $downloadUrl
        Set-Property $manifest "IsTestingExclusive" $true
    }
    else
    {
        $stableVersion = ConvertTo-NormalizedVersion $existingManifest.AssemblyVersion
        if ($existingManifest.IsTestingExclusive)
        {
            $currentTestingVersion = ConvertTo-NormalizedVersion $existingManifest.TestingAssemblyVersion
            if ($releaseVersion -lt $currentTestingVersion)
            {
                throw "Testing version '$releaseVersion' must not be older than current testing version '$currentTestingVersion'."
            }
        }
        elseif ($releaseVersion -le $stableVersion)
        {
            throw "Testing version '$releaseVersion' must be newer than stable version '$stableVersion'."
        }

        $manifest = $existingManifest
    }

    Set-Property $manifest "TestingAssemblyVersion" $normalizedVersion
    Set-Property $manifest "TestingDalamudApiLevel" $pluginManifest.DalamudApiLevel
    Set-Property $manifest "TestingChangelog" $ReleaseNotes
    Set-Property $manifest "DownloadLinkTesting" $downloadUrl
}
else
{
    if ($null -ne $existingManifest -and -not $existingManifest.IsTestingExclusive)
    {
        $currentStableVersion = ConvertTo-NormalizedVersion $existingManifest.AssemblyVersion
        if ($releaseVersion -lt $currentStableVersion)
        {
            throw "Stable version '$releaseVersion' must not be older than current stable version '$currentStableVersion'."
        }
    }

    $manifest = $pluginManifest
    Set-Property $manifest "Changelog" $ReleaseNotes
    Set-Property $manifest "DownloadLinkInstall" $downloadUrl
    Set-Property $manifest "DownloadLinkUpdate" $downloadUrl

    # Stable and testing are independent channels. Keep an existing testing
    # release even when the new stable version has caught up with it; Dalamud
    # will choose the greater applicable version itself.
    $hasTestingVersion = Copy-Property $manifest $existingManifest "TestingAssemblyVersion"
    [void](Copy-Property $manifest $existingManifest "TestingDalamudApiLevel")
    [void](Copy-Property $manifest $existingManifest "TestingChangelog")
    [void](Copy-Property $manifest $existingManifest "DownloadLinkTesting")

    if (-not $hasTestingVersion)
    {
        Set-Property $manifest "TestingAssemblyVersion" $normalizedVersion
        Set-Property $manifest "TestingDalamudApiLevel" $pluginManifest.DalamudApiLevel
        Set-Property $manifest "TestingChangelog" $ReleaseNotes
        Set-Property $manifest "DownloadLinkTesting" $downloadUrl
    }
}

Set-Property $manifest "RepoUrl" $RepositoryUrl
Set-Property $manifest "DownloadCount" 0
Set-Property $manifest "LastUpdate" $lastUpdate

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory))
{
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$json = ConvertTo-Json -InputObject @($manifest) -Depth 16
[IO.File]::WriteAllText($OutputPath, "$json`n", [Text.UTF8Encoding]::new($false))
