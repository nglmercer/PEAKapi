<#
.SYNOPSIS
    Talks to a running PEAKapi.

.EXAMPLE
    $env:PEAK_API_TOKEN = "..."
    ./peakapi.ps1 health
    ./peakapi.ps1 items
    ./peakapi.ps1 spawn

.DESCRIPTION
    The token can also be read from the PEAKapi configuration with the
    PEAK_CONFIG environment variable.
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('health', 'capabilities', 'items', 'spawn')]
    [string]$Command = 'health'
)

$ErrorActionPreference = 'Stop'

$baseUrl = if ($env:PEAK_API_URL) { $env:PEAK_API_URL } else { 'http://127.0.0.1:17454' }

function Get-PeakApiToken {
    if ($env:PEAK_API_TOKEN) { return $env:PEAK_API_TOKEN.Trim() }

    $candidates = @(
        $env:PEAK_CONFIG,
        (Join-Path $env:LOCALAPPDATA '..\..\..\..\Steam\steamapps\common\PEAK\BepInEx\config\org.peak.rest-api.cfg')
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $line = Select-String -Path $candidate -Pattern '^AuthenticationToken\s*=\s*(.+)$' | Select-Object -First 1
            if ($line) { return $line.Matches[0].Groups[1].Value.Trim() }
        }
    }

    throw 'Set PEAK_API_TOKEN or PEAK_CONFIG.'
}

$token = Get-PeakApiToken
$headers = @{ Authorization = "Bearer $token" }

function Invoke-PeakApi {
    param(
        [string]$Method = 'GET',
        [string]$Path,
        [object]$Body
    )

    $parameters = @{
        Uri        = "$baseUrl$Path"
        Method     = $Method
        Headers    = $headers
        TimeoutSec = 10
    }

    if ($Body) {
        $parameters['ContentType'] = 'application/json'
        $parameters['Headers'] = $headers.Clone()
        $parameters['Headers']['Idempotency-Key'] = [guid]::NewGuid().ToString()
        $parameters['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress)
    }

    try {
        $response = Invoke-RestMethod @parameters
        return [pscustomobject]@{ Status = 200; Body = $response }
    }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $payload = $reader.ReadToEnd() | ConvertFrom-Json
        return [pscustomobject]@{ Status = $status; Body = $payload }
    }
}

switch ($Command) {
    'health'       { $result = Invoke-PeakApi -Path '/health' }
    'capabilities' { $result = Invoke-PeakApi -Path '/api/capabilities' }
    'items'        { $result = Invoke-PeakApi -Path '/api/catalog/items' }
    'spawn' {
        # Take an id that the running game really has.
        $catalog = Invoke-PeakApi -Path '/api/catalog/items'
        $item = $catalog.Body.items | Where-Object { $_.spawnable } | Select-Object -First 1
        if (-not $item) {
            Write-Error 'No spawnable item. Is a level loaded?'
            exit 1
        }

        $result = Invoke-PeakApi -Method POST -Path '/api/spawn/item' -Body @{
            itemId   = $item.id
            quantity = 1
            position = @{ mode = 'random_player' }
        }
    }
}

$result.Body | ConvertTo-Json -Depth 8
