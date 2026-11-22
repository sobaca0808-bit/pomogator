$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) 'probes'
function Assert($condition, $message) {
    if (-not $condition) { throw $message }
}

foreach ($script in (Get-ChildItem $root -Filter '*.ps1')) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) ("Syntax error: " + $script.Name)
}
'PASS Parse all embedded PowerShell scripts'

function Invoke-Probe($name) {
    $common = Get-Content (Join-Path $root 'common.ps1') -Raw -Encoding UTF8
    $source = Get-Content (Join-Path $root ($name + '.ps1')) -Raw -Encoding UTF8
    $previous = [Console]::Out
    $writer = [System.IO.StringWriter]::new()
    try {
        [Console]::SetOut($writer)
        & ([scriptblock]::Create($common + [Environment]::NewLine + $source))
        return (ConvertFrom-Json -InputObject ($writer.ToString()))
    } finally { [Console]::SetOut($previous); $writer.Dispose() }
}

function Get-NetAdapter {
    param([switch]$IncludeHidden)
    [pscustomobject]@{ ifIndex=4; Name='Адаптер'; InterfaceDescription='Ralink 802.11n'; DriverVersion='1.2'; DriverDate='2020-01-01'; Status='Up'; HardwareInterface=$true; LinkSpeed='150 Mbps' }
    [pscustomobject]@{ ifIndex=8; Name='Tunnel'; InterfaceDescription='Wintun'; Status='Up'; HardwareInterface=$false }
}
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($ClassName -eq 'Win32_NetworkAdapter') {
        [pscustomobject]@{ Name='USB Wi-Fi'; ConfigManagerErrorCode=28 }
    } else {
        foreach ($name in @('SharedAccess','WlanSvc','RasMan','Dnscache','Dhcp','NlaSvc','WinHttpAutoProxySvc','BFE')) {
            [pscustomobject]@{ Name=$name; State='Running'; StartMode='Manual' }
        }
    }
}
function Get-NetIPAddress { [pscustomobject]@{ InterfaceIndex=4; IPAddress='192.0.2.10'; PrefixLength=24; AddressFamily='IPv4'; AddressState='Preferred' } }
function Get-DnsClientServerAddress { [pscustomobject]@{ InterfaceIndex=4; ServerAddresses=@('192.0.2.53') } }
function Get-NetIPInterface { [pscustomobject]@{ InterfaceIndex=4; AddressFamily='IPv4'; ConnectionState='Connected'; InterfaceMetric=20 } }
function Get-NetConnectionProfile { [pscustomobject]@{ InterfaceIndex=4; NetworkCategory='Public'; IPv4Connectivity='NoTraffic'; IPv6Connectivity='NoTraffic' } }
function Get-NetRoute {
    param($PolicyStore)
    [pscustomobject]@{ DestinationPrefix='0.0.0.0/0'; NextHop='192.0.2.1'; InterfaceIndex=4; RouteMetric=20 }
}
function Find-NetRoute {
    param($RemoteIPAddress)
    Get-NetRoute
}
function Get-WinEvent {
    param($FilterHashtable, $MaxEvents)
    [pscustomobject]@{ TimeCreated=[datetime]'2026-01-01'; ProviderName='Microsoft-Windows-DNS-Client';
        Id=1014; Level=3; Message='sensitive event payload' }
}

$adapters = Invoke-Probe adapters
Assert ($adapters.Status -eq 1 -and $adapters.Codes -contains 'PNP=28') 'Device code was not reported.'
Assert (($adapters.Details -join ' ') -match 'ifIndex 8') 'VPN adapter observation missing.'
'PASS Report driver fault and virtual VPN candidate'

$addresses = Invoke-Probe addresses
Assert ($addresses.Status -eq 0) 'Recorded default route was not recognized.'
Assert (($addresses.Details -join ' ') -match '192\.0\.2\.53.+1\.1\.1\.1') 'DNS and route evidence missing.'
'PASS Collect IP, DNS, routes and NLA without treating NLA as an outage'

$services = Invoke-Probe services
Assert ($services.Status -eq 0) 'Manual service start was incorrectly flagged.'
Assert (($services.Details -join ' ') -match 'SharedAccess:.*вручную') 'SharedAccess start mode is missing.'
'PASS Treat manual SharedAccess startup as valid'

$events = Invoke-Probe events
Assert ($events.Status -eq 2 -and $events.Codes -contains 'EVENT=1014') 'Historical event not found.'
Assert (($events.Details -join ' ') -notlike '*sensitive event payload*') 'Raw event message was exported.'
'PASS Retain event metadata without reading the message'

function Get-NetAdapter { throw [UnauthorizedAccessException]::new('sensitive OS error message') }
$errorResult = Invoke-Probe adapters
Assert ($errorResult.Status -eq 2 -and $errorResult.Codes.Count -eq 1) 'Failed system read was not contained.'
Assert (($errorResult | ConvertTo-Json -Compress) -notlike '*sensitive OS error message*') 'Raw exception was exported.'
'PASS Isolate Windows command failure and omit raw exception text'
