[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Send-Result {
    param([int]$Status, [string]$Summary, [string[]]$Details = @(), [string[]]$Codes = @())
    $json = ConvertTo-Json -Compress -Depth 4 -InputObject @{
        Status = $Status; Summary = $Summary; Details = @($Details | Where-Object { $_ })
        Codes = @($Codes | Where-Object { $_ } | Select-Object -Unique)
    }
    [Console]::WriteLine($json)
}

function Error-Code($record) {
    return '0x{0:X8}' -f $record.Exception.HResult
}

function Safe-Value($value) {
    return ([string]$value -replace '[\u0000-\u001F\u007F]', ' ')
}
