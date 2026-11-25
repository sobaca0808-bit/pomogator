#Requires -Version 7.3
param(
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Destination = [IO.Path]::GetFullPath($Destination)
$source = Join-Path $WorkDirectory 'xray-source'
$commit = '676eaf66f8f05d4d558ee7a74b4736623d3feeb9'
$patchName = 'xray-tun-peer.patch'

git init -q $source
git -C $source config core.autocrlf false
git -C $source fetch --depth=1 https://github.com/XTLS/Xray-core.git refs/tags/v26.11.9
git -C $source checkout -q --detach FETCH_HEAD
if ((git -C $source rev-parse HEAD) -ne $commit) { throw 'Unexpected Xray source revision.' }

# Windows checkouts of this repository may have converted the patch to CRLF.
$patch = Join-Path $WorkDirectory $patchName
$content = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $patchName))
[IO.File]::WriteAllText($patch, $content.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
git -C $source apply --index $patch
$tree = git -C $source write-tree

$buildEnv = @{
    GOOS = 'windows'; GOARCH = 'amd64'; GOAMD64 = 'v1'; CGO_ENABLED = '0'
    GOWORK = 'off'; GOTOOLCHAIN = 'local'
}
$previous = @{}
Push-Location $source
try {
    foreach ($key in $buildEnv.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $buildEnv[$key])
    }
    if ((go env GOVERSION) -ne 'go1.27.3') { throw 'Build xray-tun.exe with Go 1.27.3.' }
    go mod download
    go mod verify
    $testNames = go test -mod=readonly -list '^TestPomogator' ./proxy/tun
    if (!($testNames -match '^TestPomogator')) { throw 'Patched TUN tests not found.' }
    go test -mod=readonly -v -count=1 -run '^TestPomogator' ./proxy/tun
    $label = 'pomogator-tun-' + $tree.Substring(0, 12)
    go build -mod=readonly -trimpath -buildvcs=false -ldflags "-s -w -X github.com/xtls/xray-core/core.build=$label" -o (Join-Path $Destination 'xray-tun.exe') ./main

    # The archive contains the exact patched sources, including go.mod, go.sum
    # and the upstream license. The tree does not contain build products.
    git archive --format=zip --prefix=xray-tun/ "--output=$(Join-Path $Destination 'XRAY-TUN-SOURCE.zip')" $tree
    Copy-Item -LiteralPath $patch -Destination $Destination
    @(
        'xray-tun.exe: modified Xray-core under the Mozilla Public License 2.0.'
        "Upstream: https://github.com/XTLS/Xray-core/tree/$commit"
        "Patched Git tree: $tree"
        'Corresponding source: XRAY-TUN-SOURCE.zip. Changes: xray-tun-peer.patch.'
        'License: XRAY-LICENSE.txt. The stock proxy binary xray.exe is from the upstream release.'
        "Toolchain: Go 1.27.3, GOOS=windows, GOARCH=amd64, GOAMD64=v1, CGO_ENABLED=0."
    ) | Set-Content -Encoding utf8 (Join-Path $Destination 'XRAY-TUN-NOTICE.txt')
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    Pop-Location
}
