#Requires -Version 5.1
# pckit engine | tools: ремонт Windows (DISM + SFC, сброс сети), быстрый DNS с замером, удаление встроенных приложений
# Всё, что можно вернуть, пишется в журнал отката (DNS). Ремонт и удаление приложений вернуть кнопкой нельзя — окно предупреждает.

# ---------------------------------------------------------------- ремонт

function Invoke-PckitRepairSystem {
    # DISM чинит хранилище компонентов, потом SFC сверяет системные файлы. 10–30 минут.
    param([scriptblock]$OnStep)
    $sys = [Environment]::SystemDirectory
    if ($OnStep) { & $OnStep 'DISM: проверяю и чиню хранилище компонентов Windows (самая долгая часть)' }
    & (Join-Path $sys 'Dism.exe') /Online /Cleanup-Image /RestoreHealth /NoRestart 2>&1 | Out-Null
    $dism = $LASTEXITCODE

    if ($OnStep) { & $OnStep 'SFC: проверяю системные файлы' }
    # sfc пишет в консоль UTF-16: читаем через файл
    $log = Join-Path $env:TEMP ('nolagi-sfc-{0}.txt' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    & cmd.exe /c ('"{0}" /scannow > "{1}" 2>&1' -f (Join-Path $sys 'sfc.exe'), $log) | Out-Null
    $sfc = $LASTEXITCODE
    $text = ''
    try { $text = ([IO.File]::ReadAllText($log, [Text.Encoding]::Unicode) -replace "`0", '') } catch { }
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue

    # итог по смыслу, а не по тексту (он на языке Windows): 0 = нарушений нет; иначе смотрим ключевые слова
    $sfcState = if ($sfc -eq 0) { 'clean' }
    elseif ($text -match 'successfully repaired|успешно восстановил') { 'repaired' }
    elseif ($text -match 'unable to fix|не удается восстановить|не удалось восстановить') { 'broken' }
    else { 'unknown' }
    return [pscustomobject]@{ DismCode = $dism; SfcCode = $sfc; Sfc = $sfcState }
}

function Invoke-PckitRepairNetwork {
    # сброс Winsock и TCP/IP к заводским + очистка кэша DNS; вступает в силу после перезагрузки
    $steps = @(
        @{ Text = 'Сброс Winsock'; Cmd = 'netsh winsock reset' },
        @{ Text = 'Сброс TCP/IP'; Cmd = 'netsh int ip reset' },
        @{ Text = 'Очистка кэша DNS'; Cmd = 'ipconfig /flushdns' }
    )
    $out = New-Object System.Collections.ArrayList
    foreach ($s in $steps) {
        & cmd.exe /c $s.Cmd 2>&1 | Out-Null
        [void]$out.Add([pscustomobject]@{ Text = $s.Text; Ok = ($LASTEXITCODE -eq 0) })
    }
    return $out.ToArray()
}

# ---------------------------------------------------------------- DNS

$script:PckitDnsServers = @(
    [pscustomobject]@{ Key = 'cloudflare'; Name = 'Cloudflare'; Primary = '1.1.1.1'; Secondary = '1.0.0.1'; Doh = $true; Note = 'самый быстрый в большинстве стран, не продаёт данные' },
    [pscustomobject]@{ Key = 'google'; Name = 'Google'; Primary = '8.8.8.8'; Secondary = '8.8.4.4'; Doh = $true; Note = 'надёжный, работает везде' },
    [pscustomobject]@{ Key = 'quad9'; Name = 'Quad9'; Primary = '9.9.9.9'; Secondary = '149.112.112.112'; Doh = $true; Note = 'блокирует сайты с вирусами' },
    [pscustomobject]@{ Key = 'yandex'; Name = 'Яндекс'; Primary = '77.88.8.8'; Secondary = '77.88.8.1'; Doh = $false; Note = 'быстрый в СНГ' }
)
$script:PckitDnsTestNames = @('steampowered.com', 'faceit.com', 'discord.com', 'youtube.com', 'google.com', 'microsoft.com')

function Get-PckitDnsAdapters {
    # подключённые адаптеры с выходом в интернет (есть шлюз по умолчанию)
    $list = New-Object System.Collections.ArrayList
    $cfgs = @()
    try { $cfgs = @(Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' }) } catch { return @() }
    foreach ($c in $cfgs) {
        $guid = [string]$c.NetAdapter.InterfaceGuid
        $static = ''
        try { $static = [string](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{0}' -f $guid) -Name NameServer -ErrorAction Stop).NameServer } catch { }
        $servers = @()
        try { $servers = @((Get-DnsClientServerAddress -InterfaceIndex $c.InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop).ServerAddresses) } catch { }
        [void]$list.Add([pscustomobject]@{
                Index   = [int]$c.InterfaceIndex
                Alias   = [string]$c.InterfaceAlias
                Dhcp    = (-not $static.Trim())
                Static  = @($static -split '[,\s]+' | Where-Object { $_ })
                Servers = $servers
            })
    }
    return $list.ToArray()
}

function Measure-PckitDnsServer {
    # медиана времени ответа (мс) по нескольким сайтам; $null = сервер не отвечает
    param([string]$Server)
    $times = New-Object System.Collections.ArrayList
    foreach ($n in $script:PckitDnsTestNames) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            Resolve-DnsName -Name $n -Server $Server -Type A -DnsOnly -NoHostsFile -QuickTimeout -ErrorAction Stop | Out-Null
            $sw.Stop(); [void]$times.Add($sw.Elapsed.TotalMilliseconds)
        } catch { $sw.Stop() }
    }
    if ($times.Count -lt [math]::Ceiling($script:PckitDnsTestNames.Count / 2)) { return $null }
    $sorted = @($times | Sort-Object)
    return [math]::Round($sorted[[int][math]::Floor($sorted.Count / 2)], 0)
}

function Get-PckitDnsBenchmark {
    param([scriptblock]$OnStep)
    $adapters = @(Get-PckitDnsAdapters)
    $current = @($adapters | ForEach-Object { $_.Servers } | Where-Object { $_ -and $_ -notmatch ':' } | Select-Object -Unique)
    $rows = New-Object System.Collections.ArrayList
    if ($current.Count) {
        if ($OnStep) { & $OnStep 'Текущий DNS' }
        $known = $script:PckitDnsServers | Where-Object { $current -contains $_.Primary } | Select-Object -First 1
        [void]$rows.Add([pscustomobject]@{
                Key = 'current'; Name = $(if ($known) { '{0} (сейчас)' -f $known.Name } else { 'Сейчас (провайдер или роутер)' })
                Servers = ($current -join ', '); Ms = (Measure-PckitDnsServer -Server $current[0]); Current = $true; Doh = $false; Note = ''
            })
    }
    foreach ($s in $script:PckitDnsServers) {
        if ($current.Count -and $current[0] -eq $s.Primary) { continue }
        if ($OnStep) { & $OnStep $s.Name }
        [void]$rows.Add([pscustomobject]@{
                Key = $s.Key; Name = $s.Name; Servers = ('{0}, {1}' -f $s.Primary, $s.Secondary)
                Ms = (Measure-PckitDnsServer -Server $s.Primary); Current = $false; Doh = $s.Doh; Note = $s.Note
            })
    }
    return [pscustomobject]@{ Rows = $rows.ToArray(); Adapters = @($adapters | ForEach-Object { $_.Alias }); Dhcp = [bool](@($adapters | Where-Object { $_.Dhcp }).Count) }
}

function Set-PckitDns {
    # меняет DNS на всех подключённых адаптерах; старые адреса — в журнал (netsh, чтобы откат работал через cmd)
    param([string]$Key, [bool]$Doh, $Journal)
    $s = $script:PckitDnsServers | Where-Object { $_.Key -eq $Key } | Select-Object -First 1
    if (-not $s) { throw ('неизвестный DNS: {0}' -f $Key) }
    $done = New-Object System.Collections.ArrayList
    foreach ($a in @(Get-PckitDnsAdapters)) {
        if ($a.Alias -match '["%^&|<>]') { [void]$done.Add([pscustomobject]@{ Alias = $a.Alias; Ok = $false; Message = 'необычное имя адаптера — пропущен' }); continue }
        try {
            $undo = if ($a.Dhcp) {
                'netsh interface ipv4 set dnsservers name="{0}" source=dhcp' -f $a.Alias
            } else {
                $u = 'netsh interface ipv4 set dnsservers name="{0}" static {1} primary validate=no' -f $a.Alias, $a.Static[0]
                for ($i = 1; $i -lt $a.Static.Count; $i++) { $u += (' && netsh interface ipv4 add dnsservers name="{0}" {1} index={2} validate=no' -f $a.Alias, $a.Static[$i], ($i + 1)) }
                $u
            }
            Set-DnsClientServerAddress -InterfaceIndex $a.Index -ServerAddresses @($s.Primary, $s.Secondary) -ErrorAction Stop
            if ($Journal) { Add-PckitCommandTracked -Journal $Journal -Undo $undo -Note ('DNS адаптера {0}' -f $a.Alias) }
            [void]$done.Add([pscustomobject]@{ Alias = $a.Alias; Ok = $true; Message = '' })
        } catch {
            [void]$done.Add([pscustomobject]@{ Alias = $a.Alias; Ok = $false; Message = $_.Exception.Message })
        }
    }
    # DNS-over-HTTPS: Windows 11 шифрует запросы к известным ей серверам (Cloudflare, Google, Quad9)
    if ($Doh -and $s.Doh -and [Environment]::OSVersion.Version.Build -ge 22000) {
        try {
            foreach ($ip in @($s.Primary, $s.Secondary)) {
                $cur = Get-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction Stop
                if (-not $cur.AutoUpgrade) {
                    Set-DnsClientDohServerAddress -ServerAddress $ip -AutoUpgrade $true -AllowFallbackToUdp $true -ErrorAction Stop | Out-Null
                    if ($Journal) {
                        Add-PckitCommandTracked -Journal $Journal -Note ('DNS-over-HTTPS для {0}' -f $ip) `
                            -Undo ('powershell -NoProfile -Command "Set-DnsClientDohServerAddress -ServerAddress {0} -AutoUpgrade ${1} -AllowFallbackToUdp ${2}"' -f $ip, ([string]$cur.AutoUpgrade).ToLower(), ([string]$cur.AllowFallbackToUdp).ToLower())
                    }
                }
            }
            [void]$done.Add([pscustomobject]@{ Alias = 'DNS-over-HTTPS'; Ok = $true; Message = '' })
        } catch {
            [void]$done.Add([pscustomobject]@{ Alias = 'DNS-over-HTTPS'; Ok = $false; Message = $_.Exception.Message })
        }
    }
    & ipconfig.exe /flushdns 2>&1 | Out-Null
    return $done.ToArray()
}

# ---------------------------------------------------------------- встроенные приложения

# только известный «мусор» с понятными названиями; системное, магазин, драйверы, Xbox для Game Pass сюда не входят
$script:PckitBloatApps = @(
    @('Microsoft.BingNews', 'Новости (MSN)', 'safe'),
    @('Microsoft.BingWeather', 'Погода (MSN)', 'safe'),
    @('Microsoft.BingSearch', 'Поиск Bing', 'safe'),
    @('Microsoft.MicrosoftSolitaireCollection', 'Microsoft Solitaire Collection', 'safe'),
    @('Microsoft.GetHelp', 'Техническая поддержка', 'safe'),
    @('Microsoft.Getstarted', 'Советы', 'safe'),
    @('Microsoft.WindowsFeedbackHub', 'Центр отзывов', 'safe'),
    @('Microsoft.WindowsMaps', 'Карты', 'safe'),
    @('Microsoft.People', 'Люди', 'safe'),
    @('Microsoft.MicrosoftOfficeHub', 'Microsoft 365 (Office) — реклама подписки', 'safe'),
    @('Microsoft.Todos', 'Microsoft To Do', 'safe'),
    @('Microsoft.PowerAutomateDesktop', 'Power Automate', 'safe'),
    @('Microsoft.Microsoft3DViewer', 'Средство просмотра 3D', 'safe'),
    @('Microsoft.MixedReality.Portal', 'Портал смешанной реальности', 'safe'),
    @('Microsoft.SkypeApp', 'Skype', 'safe'),
    @('MicrosoftTeams', 'Teams (личный)', 'safe'),
    @('MSTeams', 'Teams (личный)', 'safe'),
    @('Clipchamp.Clipchamp', 'Clipchamp — видеоредактор', 'safe'),
    @('MicrosoftCorporationII.MicrosoftFamily', 'Семейная безопасность', 'safe'),
    @('Microsoft.Windows.DevHome', 'Dev Home', 'safe'),
    @('Microsoft.549981C3F5F10', 'Cortana', 'safe'),
    @('Microsoft.Copilot', 'Copilot', 'safe'),
    @('Microsoft.OutlookForWindows', 'Outlook (новый)', 'caution'),
    @('Microsoft.WindowsCommunicationsApps', 'Почта и Календарь', 'caution'),
    @('Microsoft.ZuneVideo', 'Кино и ТВ', 'caution'),
    @('Microsoft.ZuneMusic', 'Медиаплеер', 'caution'),
    @('Microsoft.WindowsSoundRecorder', 'Звукозапись', 'caution'),
    @('Microsoft.WindowsAlarms', 'Часы и будильник', 'caution'),
    @('Microsoft.MicrosoftStickyNotes', 'Записки', 'caution'),
    @('Microsoft.YourPhone', 'Связь с телефоном', 'caution'),
    @('MicrosoftCorporationII.QuickAssist', 'Быстрая помощь', 'caution'),
    @('SpotifyAB.SpotifyMusic', 'Spotify (предустановлен)', 'safe'),
    @('Disney.37853FC22B2CE', 'Disney+ (предустановлен)', 'safe'),
    @('BytedancePte.Ltd.TikTok', 'TikTok (предустановлен)', 'safe'),
    @('king.com.CandyCrushSaga', 'Candy Crush Saga', 'safe'),
    @('king.com.CandyCrushSodaSaga', 'Candy Crush Soda Saga', 'safe'),
    @('Facebook.Facebook', 'Facebook (предустановлен)', 'safe'),
    @('AmazonVideo.PrimeVideo', 'Prime Video (предустановлен)', 'safe')
)

function Get-PckitBloatApps {
    $installed = @{}
    try { foreach ($p in @(Get-AppxPackage -ErrorAction Stop)) { if (-not $installed.ContainsKey([string]$p.Name)) { $installed[[string]$p.Name] = $p } } } catch { }
    $rows = New-Object System.Collections.ArrayList
    $seen = @{}
    foreach ($b in $script:PckitBloatApps) {
        $p = $installed[$b[0]]
        if (-not $p -or $p.NonRemovable -or $seen.ContainsKey($b[1])) { continue }
        $seen[$b[1]] = $true
        [void]$rows.Add([pscustomobject]@{
                Id   = ('APPX|{0}' -f $b[0]); Name = $b[1]; Package = [string]$p.Name; Level = $b[2]
                Note = $(if ($b[2] -eq 'caution') { 'пользуетесь — оставьте' } else { 'можно удалить' })
            })
    }
    # OneDrive — обычная программа, удаляется своим деинсталлятором (файлы в облаке и в папке OneDrive остаются)
    $od = $null
    try { $od = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe' -ErrorAction Stop } catch { }
    if ($od -and $od.UninstallString) {
        [void]$rows.Add([pscustomobject]@{ Id = 'ONEDRIVE'; Name = 'OneDrive'; Package = 'OneDrive'; Level = 'caution'; Note = 'если храните файлы в OneDrive — оставьте; файлы в облаке и в папке OneDrive не удаляются' })
    }
    return $rows.ToArray()
}

function Remove-PckitBloatApp {
    param([string]$Id)
    try {
        if ($Id -eq 'ONEDRIVE') {
            $od = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe' -ErrorAction Stop
            Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            & cmd.exe /c ([string]$od.UninstallString) 2>&1 | Out-Null
            return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Message = $(if ($LASTEXITCODE -eq 0) { '' } else { 'код {0}' -f $LASTEXITCODE }) }
        }
        if ($Id -notmatch '^APPX\|(.+)$') { throw 'неизвестное приложение' }
        $name = $matches[1]
        if (-not ($script:PckitBloatApps | Where-Object { $_[0] -eq $name })) { throw 'этого приложения нет в списке Nolagi' }
        $pkgs = @(Get-AppxPackage -Name $name -ErrorAction Stop)
        if (-not $pkgs.Count) { return [pscustomobject]@{ Ok = $true; Message = 'уже удалено' } }
        foreach ($p in $pkgs) { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop }
        return [pscustomobject]@{ Ok = $true; Message = '' }
    } catch {
        return [pscustomobject]@{ Ok = $false; Message = $_.Exception.Message }
    }
}

# ---------------------------------------------------------------- BIOS

# Nolagi сам ничего в BIOS не пишет (нет общего интерфейса, а неверная запись может убить плату):
# только перезагружает в настройки UEFI и подсказывает, где у этой платы XMP и Resizable BAR.
$script:PckitBiosHints = @(
    @{ Match = 'ASUS'; Name = 'ASUS'; Key = 'Del или F2'
        Xmp = 'F7 (Advanced Mode) → Ai Tweaker → Ai Overclock Tuner → XMP I / EXPO / D.O.C.P.'
        Rebar = 'Advanced → PCI Subsystem Settings → Above 4G Decoding = Enabled, Re-Size BAR Support = Enabled' },
    @{ Match = 'Micro-Star|MSI'; Name = 'MSI'; Key = 'Del'
        Xmp = 'OC → Extreme Memory Profile (X.M.P.) или A-XMP / EXPO → Profile 1'
        Rebar = 'Settings → Advanced → PCI Subsystem Settings → Above 4G memory = Enabled, Re-Size BAR Support = Enabled' },
    @{ Match = 'Gigabyte'; Name = 'Gigabyte / AORUS'; Key = 'Del'
        Xmp = 'Tweaker → Extreme Memory Profile (X.M.P.) / EXPO → Profile 1'
        Rebar = 'Settings → IO Ports → Above 4G Decoding = Enabled, Re-Size BAR Support = Auto' },
    @{ Match = 'ASRock'; Name = 'ASRock'; Key = 'F2 или Del'
        Xmp = 'OC Tweaker → DRAM Configuration → Load XMP / EXPO Setting → Profile 1'
        Rebar = 'Advanced → Chipset Configuration → Above 4G Decoding = Enabled, C.A.M. (Clever Access Memory) = Enabled' },
    @{ Match = 'Biostar'; Name = 'Biostar'; Key = 'Del'
        Xmp = 'O.N.E. → Memory → XMP / EXPO Profile'
        Rebar = 'Advanced → PCIe Configuration → Above 4G Decoding и Re-Size BAR' }
)

function Get-PckitBiosInfo {
    $maker = ''; $model = ''; $biosVer = ''
    try { $b = Get-CimInstance Win32_BaseBoard; $maker = ([string]$b.Manufacturer).Trim(); $model = ([string]$b.Product).Trim() } catch { }
    try { $bi = Get-CimInstance Win32_BIOS; $biosVer = ('{0} {1}' -f ([string]$bi.SMBIOSBIOSVersion).Trim(), $(if ($bi.ReleaseDate) { ([datetime]$bi.ReleaseDate).ToString('dd.MM.yyyy') } else { '' })).Trim() } catch { }
    $uefi = ([string]$env:firmware_type -eq 'UEFI')
    $hint = $script:PckitBiosHints | Where-Object { $maker -match $_.Match } | Select-Object -First 1
    return [pscustomobject]@{
        maker = $maker; model = $model; bios = $biosVer; uefi = $uefi
        known = [bool]$hint
        name  = $(if ($hint) { $hint.Name } else { $maker })
        key   = $(if ($hint) { $hint.Key } else { 'Del, F2 или F10' })
        xmp   = $(if ($hint) { $hint.Xmp } else { 'ищите раздел с названием OC / Tweaker / Memory и пункт XMP, EXPO или DOCP → Profile 1' })
        rebar = $(if ($hint) { $hint.Rebar } else { 'ищите Above 4G Decoding и Re-Size BAR (у AMD-плат может называться Smart Access Memory) → Enabled' })
    }
}

function Restart-PckitToFirmware {
    # перезагрузка сразу в настройки UEFI; на старых платах (Legacy BIOS) так нельзя
    if ([string]$env:firmware_type -ne 'UEFI') { throw 'Этот ПК загружается в режиме Legacy BIOS — Windows не умеет перезагрузить его сразу в настройки. Перезагрузите ПК и нажимайте Del или F2 при старте.' }
    & (Join-Path ([Environment]::SystemDirectory) 'shutdown.exe') /r /fw /t 5 /c 'Nolagi: перезагрузка в настройки BIOS' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('shutdown вернул код {0}' -f $LASTEXITCODE) }
}
