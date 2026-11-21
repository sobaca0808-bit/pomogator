try {
    $adapters = @(Get-NetAdapter -IncludeHidden | Sort-Object ifIndex | Select-Object -First 128)
    $detail = [System.Collections.Generic.List[string]]::new()
    $codes = [System.Collections.Generic.List[string]]::new()
    $needsReview = $false
    $deviceProblem = $false
    foreach ($adapter in $adapters) {
        $kind = if ($adapter.HardwareInterface) { 'физический' } else { 'виртуальный/программный' }
        $detail.Add(('ifIndex {0}: {1} | {2} | {3} | {4} | скорость: {5} | драйвер: {6} ({7})' -f
            $adapter.ifIndex, (Safe-Value $adapter.Name), (Safe-Value $adapter.InterfaceDescription), $kind,
            $adapter.Status, $adapter.LinkSpeed, (Safe-Value $adapter.DriverVersion), (Safe-Value $adapter.DriverDate)))
    }
    if ($adapters.Count -eq 128) { $detail.Add('Показано до 128 интерфейсов.') }

    try {
        $devices = @(Get-CimInstance Win32_NetworkAdapter -Filter 'ConfigManagerErrorCode <> 0' |
            Select-Object -First 64)
        foreach ($device in $devices) {
            $code = [int]$device.ConfigManagerErrorCode
            # Code 45 can describe a previously plugged device; 22 means administratively disabled.
            $description = switch ($code) {
                22 { 'отключено' }
                45 { 'сейчас отсутствует' }
                28 { 'драйвер не установлен'; $deviceProblem = $true }
                10 { 'устройство не запустилось'; $deviceProblem = $true }
                31 { 'Windows не загрузила драйвер'; $deviceProblem = $true }
                43 { 'устройство сообщило о проблеме'; $deviceProblem = $true }
                default { 'проверьте устройство в диспетчере устройств'; $needsReview = $true }
            }
            $codes.Add("PNP=$code")
            $detail.Add(('Диспетчер устройств: {0} | код {1}, {2}' -f (Safe-Value $device.Name), $code, $description))
        }
    }
    catch {
        $needsReview = $true
        $codes.Add((Error-Code $_))
        $detail.Add('Не удалось прочитать коды состояния устройств через CIM.')
    }

    $vpn = @($adapters | Where-Object { $_.InterfaceDescription -match 'VPN|WireGuard|Wintun|OpenVPN|TAP-Windows|Tailscale|ZeroTier|AnyConnect|Fortinet|GlobalProtect|WAN Miniport \((IKEv2|L2TP|PPTP|SSTP)\)' })
    foreach ($adapter in $vpn) {
        $detail.Add(('Возможный VPN-интерфейс по описанию: ifIndex {0}; состояние {1}.' -f $adapter.ifIndex, $adapter.Status))
    }
    $detail.Add('Наличие виртуального интерфейса не доказывает использование VPN. Расширение браузера может не создавать системный интерфейс.')

    if ($deviceProblem) {
        Send-Result 1 'Диспетчер устройств сообщил об ошибке одного или нескольких сетевых устройств. Влияние на текущий интернет проверяется отдельно.' $detail $codes
    } elseif ($adapters.Count -eq 0 -or $needsReview) {
        Send-Result 2 'Список получен, но состояние некоторых устройств требует проверки.' $detail $codes
    } else {
        Send-Result 0 'Адаптеры и версии драйверов прочитаны.' $detail $codes
    }
}
catch { Send-Result 2 'Не удалось получить список адаптеров Windows.' @() @((Error-Code $_)) }
