<#
.SYNOPSIS
    Builds the release package of PEAKapi.

.EXAMPLE
    ./scripts/package.ps1
#>
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Version = (Select-String -Path 'src/PeakRestApi/PeakRestApi.csproj' -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$Stage = "artifacts/package/PEAKapi-$Version"
$Dist = "artifacts/dist"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "Building PEAKapi $Version"
dotnet build -c Release -v minimal

$Bin = 'src/PeakRestApi/bin/Release/netstandard2.1'
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path "$Stage/plugins" | Out-Null

Copy-Item "$Bin/com.github.nglmercer.PEAKapi.dll" "$Stage/plugins/"
Copy-Item "$Bin/PeakRestApi.Core.dll" "$Stage/plugins/"
Copy-Item 'README.md' $Stage
Copy-Item 'LICENSE' $Stage
Copy-Item 'CHANGELOG.md' $Stage
Copy-Item 'docs/INSTALLATION.md' $Stage

@"
{
  "name": "PEAKapi",
  "version_number": "$Version",
  "website_url": "https://github.com/nglmercer/PEAKapi",
  "description": "A local, authenticated REST API for PEAK. Lets a live stream spawn items, entities and hazards in a running game.",
  "dependencies": [
    "BepInEx-BepInExPack_PEAK-5.4.75301"
  ]
}
"@ | Set-Content -Path "$Stage/manifest.json" -Encoding utf8

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$zip = Join-Path $Root "$Dist/PEAKapi-$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "artifacts/package/PEAKapi-$Version" -DestinationPath $zip
Copy-Item "$Stage/manifest.json" "$Dist/manifest.json"

Write-Host "Package: $Dist/PEAKapi-$Version.zip"
Write-Host 'Install: unzip it into the PEAK folder and copy plugins/ into BepInEx/plugins/.'
