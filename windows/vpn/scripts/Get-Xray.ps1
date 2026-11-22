param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$version = '26.11.9'
$sha256 = '1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('pomogator-xray-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $archive = Join-Path $temp 'xray.zip'
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/XTLS/Xray-core/releases/download/v$version/Xray-windows-64.zip" -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $sha256) {
        throw 'Xray archive SHA-256 mismatch.'
    }
    $expanded = Join-Path $temp 'expanded'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item (Join-Path $expanded 'xray.exe') -Destination $Destination
    Copy-Item (Join-Path $expanded 'LICENSE') -Destination (Join-Path $Destination 'XRAY-LICENSE.txt')
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
