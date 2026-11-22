$detail = [System.Collections.Generic.List[string]]::new()
$codes = [System.Collections.Generic.List[string]]::new()
$incomplete = $false
try {
    Get-NetIPAddress | Sort-Object InterfaceIndex, AddressFamily | Select-Object -First 128 | ForEach-Object {
        $detail.Add(('IP ifIndex {0}: {1}/{2} | {3}, {4}' -f
            $_.InterfaceIndex, $_.IPAddress, $_.PrefixLength, $_.AddressFamily, $_.AddressState))
    }
} catch { $detail.Add('Не удалось прочитать IP-адреса.'); $codes.Add((Error-Code $_)); $incomplete = $true }

try {
    Get-DnsClientServerAddress | Where-Object { $_.ServerAddresses } | Select-Object -First 96 | ForEach-Object {
        $detail.Add(('DNS ifIndex {0}: {1}' -f $_.InterfaceIndex, ($_.ServerAddresses -join ', ')))
    }
} catch { $detail.Add('Не удалось прочитать DNS интерфейсов.'); $codes.Add((Error-Code $_)); $incomplete = $true }

try {
    Get-NetIPInterface | Sort-Object InterfaceIndex, AddressFamily | Select-Object -First 96 | ForEach-Object {
        $detail.Add(('Интерфейс ifIndex {0}: {1} | {2} | метрика {3}' -f
            $_.InterfaceIndex, $_.AddressFamily, $_.ConnectionState, $_.InterfaceMetric))
    }
} catch { $detail.Add('Не удалось прочитать метрики интерфейсов.'); $codes.Add((Error-Code $_)); $incomplete = $true }

try {
    Get-NetConnectionProfile | Select-Object -First 32 | ForEach-Object {
        $detail.Add(('Профиль NLA ifIndex {0}: {1} | IPv4 {2} | IPv6 {3}' -f
            $_.InterfaceIndex, $_.NetworkCategory, $_.IPv4Connectivity, $_.IPv6Connectivity))
    }
    $detail.Add('Категория и состояние профиля NLA сами по себе не определяют доступность интернета.')
} catch { $detail.Add('Не удалось прочитать профиль NLA.'); $codes.Add((Error-Code $_)); $incomplete = $true }

$hasDefault = $false
try {
    $routes = @(Get-NetRoute -PolicyStore ActiveStore)
    $defaults = @($routes | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0', '0.0.0.0/1', '128.0.0.0/1', '::/1', '8000::/1') })
    $hasDefault = @($defaults | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') }).Count -gt 0
    $other = @($routes | Where-Object { $_.DestinationPrefix -notin @('0.0.0.0/0', '::/0', '0.0.0.0/1', '128.0.0.0/1', '::/1', '8000::/1') } |
        Sort-Object RouteMetric | Select-Object -First 64)
    @($defaults | Select-Object -First 48) + $other | ForEach-Object {
        $detail.Add(('Маршрут {0} через {1} | ifIndex {2} | метрика {3}' -f
            $_.DestinationPrefix, $_.NextHop, $_.InterfaceIndex, $_.RouteMetric))
    }
    $detail.Add('Показано до 48 маршрутов по умолчанию или /1 и до 64 других маршрутов. 0.0.0.0/:: в поле шлюза может означать маршрут по каналу.')
} catch { $detail.Add('Не удалось прочитать таблицу маршрутов.'); $codes.Add((Error-Code $_)); $incomplete = $true }

foreach ($destination in @('1.1.1.1', '8.8.8.8')) {
    try {
        $best = @(Find-NetRoute -RemoteIPAddress $destination | Where-Object { $_.PSObject.Properties['DestinationPrefix'] })
        foreach ($route in $best) {
            $detail.Add(('Выбранный Windows маршрут к {0}: ifIndex {1}, префикс {2}, шлюз {3}.' -f
                $destination, $route.InterfaceIndex, $route.DestinationPrefix, $route.NextHop))
        }
    } catch { $detail.Add(('Не удалось определить маршрут к {0}.' -f $destination)); $codes.Add((Error-Code $_)); $incomplete = $true }
}
$detail.Add('В VPN возможны туннели, фильтрация и раздельная маршрутизация. Снимок маршрутов сам по себе не подтверждает выход без VPN.')
if ($incomplete -or -not $hasDefault) {
    Send-Result 2 'Снимок частичный или маршрут по умолчанию не найден. Сравните с результатами DNS и внешних запросов.' $detail $codes
} else {
    Send-Result 0 'Прочитаны IP, DNS и маршруты. Найден маршрут по умолчанию.' $detail $codes
}
