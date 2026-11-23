param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][string]$SourceDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Destination = [IO.Path]::GetFullPath($Destination)
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
New-Item -ItemType Directory -Path $Destination, $SourceDirectory -Force | Out-Null
$box = Join-Path $SourceDirectory 'sing-box'
$tun = Join-Path $SourceDirectory 'sing-tun'
function Get-Source($Name, $Url, $Commit) {
    git init -q $Name
    git -C $Name config core.autocrlf false
    git -C $Name fetch --depth=1 $Url $Commit
    git -C $Name checkout --detach FETCH_HEAD
    if ((git -C $Name rev-parse HEAD).Trim() -ne $Commit) { throw 'Unexpected source revision.' }
}
Get-Source $box 'https://github.com/SagerNet/sing-box.git' 'c005f35300ce76427dc285481ec5695f5b179cd2'
Get-Source $tun 'https://github.com/SagerNet/sing-tun.git' '040db71fcd33196f1d15708b91eb61e9dc6ab8dd'
$patchDirectory = Join-Path $SourceDirectory 'patches'
New-Item -ItemType Directory -Path $patchDirectory -Force | Out-Null
function Apply-Patch($Repository, $Name) {
    $patch = Join-Path $patchDirectory $Name
    $content = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "patches/$Name"))
    [IO.File]::WriteAllText($patch, $content.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    git -C $Repository apply $patch
}
Apply-Patch $box 'reality.patch'
Apply-Patch $tun 'windows-tun.patch'
Copy-Item $PSCommandPath (Join-Path $SourceDirectory 'Build.ps1')
Copy-Item (Join-Path $PSScriptRoot 'LICENSES.md') $SourceDirectory
Push-Location $box
try {
    go mod edit -replace 'github.com/sagernet/sing-tun=../sing-tun'
    go mod vendor
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    go build -mod=vendor -trimpath -tags 'with_utls,with_gvisor' -ldflags '-s -w -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=1.14.4-pomogator' -o (Join-Path $Destination 'PomogatorTunCore.exe') ./cmd/sing-box
} finally { Pop-Location }
Copy-Item (Join-Path $box 'LICENSE') (Join-Path $Destination 'SING-BOX-LICENSE.txt')
Copy-Item (Join-Path $tun 'LICENSE') (Join-Path $Destination 'SING-TUN-LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot 'LICENSES.md') $Destination
if ($env:GITHUB_RUN_ID) {
    "Corresponding Source for this binary: https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID (artifact Pomogator-sing-box-corresponding-source)." |
        Set-Content (Join-Path $Destination 'SING-BOX-SOURCE.txt')
}
