$since = (Get-Date).AddHours(-72)
$detail = [System.Collections.Generic.List[string]]::new()
$codes = [System.Collections.Generic.List[string]]::new()
$incomplete = $false
$query = @(
    @{ LogName = 'System'; ProviderName = @('Microsoft-Windows-DNS-Client', 'Microsoft-Windows-Dhcp-Client', 'Microsoft-Windows-TCPIP', 'Microsoft-Windows-NDIS'); Label = 'System' },
    @{ LogName = 'Application'; ProviderName = @('RasClient'); Label = 'Application/RasClient' },
    @{ LogName = 'Microsoft-Windows-WLAN-AutoConfig/Operational'; Label = 'WLAN-AutoConfig/Operational' }
)
$total = 0
foreach ($check in $query) {
    $label = $check.Label
    $check.Remove('Label')
    $check['StartTime'] = $since
    $check['Level'] = @(1, 2, 3)
    try {
        $events = @(Get-WinEvent -FilterHashtable $check -MaxEvents 20)
        foreach ($entry in $events) {
            $explanation = switch ($entry.Id) {
                1014 { if ($entry.ProviderName -match 'DNS-Client') { 'в прошлом был тайм-аут разрешения имени' } }
                20227 { if ($entry.ProviderName -match 'RasClient') { 'ошибка подключения RAS; сопоставьте время с обрывом PPPoE или VPN' } }
            }
            if (-not $explanation) { $explanation = 'историческое событие; сопоставьте время с симптомом' }
            $detail.Add(('{0:o} | {1} | {2} | ID {3}, уровень {4} | {5}' -f
                $entry.TimeCreated, $label, (Safe-Value $entry.ProviderName), $entry.Id, $entry.Level, $explanation))
            $codes.Add(('EVENT={0}' -f $entry.Id))
        }
        $total += $events.Count
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
            $detail.Add(('{0}: подходящих событий за 72 часа нет.' -f $label))
        } else {
            $incomplete = $true
            $codes.Add((Error-Code $_))
            $detail.Add(('{0}: не удалось прочитать журнал или провайдер недоступен.' -f $label))
        }
    }
}
$detail.Add('До 20 предупреждений и ошибок на канал за 72 часа. Полные тексты и данные событий не собираются.')
if ($total -gt 0 -or $incomplete) {
    Send-Result 2 'Есть исторические события или часть журналов недоступна. Текущее состояние сети проверяется отдельно.' $detail $codes
} else { Send-Result 0 'В проверенных каналах сетевые предупреждения и ошибки за 72 часа не найдены.' $detail }
