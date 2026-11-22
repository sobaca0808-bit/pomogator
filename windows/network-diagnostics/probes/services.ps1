try {
    $names = @('SharedAccess', 'WlanSvc', 'RasMan', 'Dnscache', 'Dhcp', 'NlaSvc', 'WinHttpAutoProxySvc', 'BFE')
    $filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
    $services = @(Get-CimInstance Win32_Service -Filter $filter)
    $detail = [System.Collections.Generic.List[string]]::new()
    $needsReview = $false
    foreach ($name in $names) {
        $service = $services | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if ($null -eq $service) {
            $needsReview = $true
            $detail.Add(('Служба {0}: отсутствует в ответе Windows.' -f $name))
            continue
        }
        $state = switch ($service.State) { 'Running' { 'запущена' }; 'Stopped' { 'остановлена' }; default { Safe-Value $service.State } }
        $mode = switch ($service.StartMode) { 'Manual' { 'вручную' }; 'Auto' { 'автоматически' }; 'Disabled' { 'отключена'; $needsReview = $true }; default { Safe-Value $service.StartMode } }
        $detail.Add(('{0}: {1}, запуск {2}.' -f $name, $state, $mode))
    }
    $detail.Add('Ручной запуск SharedAccess допустим. Остановленная служба с запуском по запросу сама по себе не означает неисправность.')
    if ($needsReview) { Send-Result 2 'Некоторые службы отключены или не найдены. Сопоставьте их с нужной функцией.' $detail }
    else { Send-Result 0 'Прочитаны состояния восьми сетевых служб.' $detail }
} catch { Send-Result 2 'Не удалось прочитать службы через CIM.' @() @((Error-Code $_)) }
