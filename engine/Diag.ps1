#Requires -Version 5.1
# pckit engine | diag: отчёт о проблеме для автора («фризы после настроек», «не откатилось»).
# Только чтение: ничего не меняет. Без личных данных: имя пользователя и имя ПК вырезаются из текста,
# файлы, пароли, история браузера не читаются. Отправляет окно — и только по кнопке пользователя.

# ---------------------------------------------------------------- приватность

function Hide-PckitDiagPrivate {
    param([string]$Text)
    if (-not $Text) { return '' }
    $t = $Text
    foreach ($pair in @(@($env:USERPROFILE, '%USERPROFILE%'), @($env:USERNAME, '<user>'), @($env:COMPUTERNAME, '<pc>'))) {
        # целым словом: короткое имя («user») не должно портить слова вроде «wuauserv»
        if ($pair[0] -and $pair[0].Length -ge 3) { $t = [regex]::Replace($t, '(?<![\w])' + [regex]::Escape([string]$pair[0]) + '(?![\w])', [string]$pair[1], 'IgnoreCase') }
    }
    # серийники и MAC-адреса в тексте событий не нужны
    $t = [regex]::Replace($t, '\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b', '<mac>')
    $t = [regex]::Replace($t, '\s+', ' ').Trim()
    return $t
}

function Limit-PckitDiagText {
    param([string]$Text, [int]$Max = 240)
    $t = Hide-PckitDiagPrivate -Text $Text
    if ($t.Length -gt $Max) { $t = $t.Substring(0, $Max) + '…' }
    return $t
}

# ---------------------------------------------------------------- твики

function Get-PckitDiagTweaks {
    # что сейчас реально включено (проверка по Windows), что из этого сделал Nolagi и что застряло в журнале
    param($Actions)
    $journal = Read-PckitJournal
    $byAct = @{}
    $orphan = 0
    foreach ($k in $script:PckitJournalKinds) {
        foreach ($e in @($journal.$k)) {
            if (-not $e) { continue }
            $a = [string]$e.act
            if (-not $a) { $orphan++; continue }
            if (-not $byAct.ContainsKey($a)) { $byAct[$a] = 0 }
            $byAct[$a]++
        }
    }
    $active = New-Object System.Collections.ArrayList
    $partial = New-Object System.Collections.ArrayList
    $titles = @{}
    foreach ($a in @($Actions)) {
        if ($a.myth) { continue }
        $id = [string]$a.id
        $title = Expand-PckitActionText -Action $a -Text ([string]$a.title)
        $titles[$id] = $title
        $st = $null
        try { $st = Test-PckitActionState -Action $a } catch { continue }
        if ($st.Status -notin 'applied', 'partial') { continue }
        $row = [pscustomobject]@{
            id     = $id
            title  = $title
            group  = [string]$a.group
            risky  = [bool]([string]$a.danger -or $a.expert -or [string]$a.risk -eq 'high')
            nolagi = [bool]$byAct.ContainsKey($id)
        }
        if ($st.Status -eq 'applied') { [void]$active.Add($row) } else { [void]$partial.Add($row) }
    }
    $journalRows = @($byAct.Keys | Sort-Object | ForEach-Object {
            [pscustomobject]@{ id = $_; title = $(if ($titles.ContainsKey($_)) { $titles[$_] } else { $_ }); entries = $byAct[$_] }
        })
    return [pscustomobject]@{
        active         = $active.ToArray()
        partial        = $partial.ToArray()
        journal        = $journalRows
        journalTotal   = (Get-PckitJournalCount -Journal $journal)
        journalOrphan  = $orphan
        journalCreated = [string]$journal.created
    }
}

# ---------------------------------------------------------------- журнал Windows

# что ищем за последние дни: сбросы видеодрайвера, аппаратные ошибки, диски, аварийные перезагрузки, нехватка памяти
$script:PckitDiagEventRules = @(
    @{ Key = 'gpu.tdr'; Log = 'System'; Provider = 'Display'; Ids = @(4101); Title = 'Видеодрайвер перестал отвечать и был восстановлен (TDR)' },
    @{ Key = 'gpu.nvidia'; Log = 'System'; Provider = 'nvlddmkm'; Ids = @(); Title = 'Ошибки драйвера NVIDIA (nvlddmkm)' },
    @{ Key = 'gpu.amd'; Log = 'System'; Provider = 'amdkmdag'; Ids = @(); Title = 'Ошибки драйвера AMD' },
    @{ Key = 'whea'; Log = 'System'; Provider = 'Microsoft-Windows-WHEA-Logger'; Ids = @(); Title = 'Аппаратные ошибки WHEA (процессор, память, PCIe)' },
    @{ Key = 'power41'; Log = 'System'; Provider = 'Microsoft-Windows-Kernel-Power'; Ids = @(41); Title = 'Аварийная перезагрузка или выключение (Kernel-Power 41)' },
    @{ Key = 'bugcheck'; Log = 'System'; Provider = 'Microsoft-Windows-WER-SystemErrorReporting'; Ids = @(1001); Title = 'Синий экран (BugCheck)' },
    @{ Key = 'disk'; Log = 'System'; Provider = 'disk'; Ids = @(7, 11, 51, 153); Title = 'Ошибки диска' },
    @{ Key = 'nvme'; Log = 'System'; Provider = 'stornvme'; Ids = @(); Title = 'Ошибки NVMe-контроллера' },
    @{ Key = 'ahci'; Log = 'System'; Provider = 'storahci'; Ids = @(); Title = 'Ошибки SATA-контроллера' },
    @{ Key = 'ntfs'; Log = 'System'; Provider = 'Ntfs'; Ids = @(55, 98); Title = 'Повреждения файловой системы' },
    @{ Key = 'lowmem'; Log = 'System'; Provider = 'Microsoft-Windows-Resource-Exhaustion-Detector'; Ids = @(2004); Title = 'Windows не хватало памяти' },
    @{ Key = 'usb'; Log = 'System'; Provider = 'Microsoft-Windows-Kernel-PnP'; Ids = @(225); Title = 'Устройство не дало себя остановить (Kernel-PnP)' },
    @{ Key = 'appcrash'; Log = 'Application'; Provider = 'Application Error'; Ids = @(1000); Title = 'Вылеты программ' },
    @{ Key = 'apphang'; Log = 'Application'; Provider = 'Application Hang'; Ids = @(1002); Title = 'Зависания программ' }
)

function Get-PckitDiagEvents {
    param([int]$Days = 7)
    $since = (Get-Date).AddDays(-$Days)
    $rows = New-Object System.Collections.ArrayList
    foreach ($r in $script:PckitDiagEventRules) {
        $f = @{ LogName = $r.Log; ProviderName = $r.Provider; StartTime = $since; Level = 1, 2, 3 }
        if (@($r.Ids).Count) { $f.Id = @($r.Ids) }
        $ev = @()
        try { $ev = @(Get-WinEvent -FilterHashtable $f -MaxEvents 200 -ErrorAction Stop) } catch { $ev = @() }
        if (-not $ev.Count) { continue }
        # для вылетов программ важно, ЧТО вылетало: первое поле события — имя exe
        $apps = @()
        if ($r.Key -in 'appcrash', 'apphang') {
            $apps = @($ev | ForEach-Object { try { [string]$_.Properties[0].Value } catch { '' } } | Where-Object { $_ } |
                Group-Object | Sort-Object Count -Descending | Select-Object -First 6 | ForEach-Object { '{0} ×{1}' -f $_.Name, $_.Count })
        }
        $last = $ev | Select-Object -First 1
        [void]$rows.Add([pscustomobject]@{
                key   = $r.Key
                title = $r.Title
                count = $ev.Count
                last  = $last.TimeCreated.ToString('yyyy-MM-dd HH:mm')
                ids   = (@($ev | ForEach-Object { $_.Id } | Sort-Object -Unique) -join ',')
                apps  = $apps
                msg   = $(try { Limit-PckitDiagText -Text ([string]$last.Message) -Max 300 } catch { '' })
            })
    }
    return $rows.ToArray()
}

# ---------------------------------------------------------------- DPC и прерывания

function Get-PckitDiagDpc {
    # счётчики Windows через CIM (имена классов не переводятся, в отличие от Get-Counter): раз в секунду
    param([int]$Seconds = 10, [scriptblock]$OnStep)
    $cls = 'Win32_PerfFormattedData_PerfOS_Processor'
    try { [void](Get-CimInstance $cls -ErrorAction Stop) } catch { return [pscustomobject]@{ ok = $false; error = $_.Exception.Message } }
    $tot = New-Object System.Collections.ArrayList
    $coreMax = @{}
    for ($i = 1; $i -le $Seconds; $i++) {
        Start-Sleep -Milliseconds 1000
        if ($OnStep) { & $OnStep ('Замеряю задержки драйверов (DPC): {0} из {1} с' -f $i, $Seconds) }
        $all = @(Get-CimInstance $cls -ErrorAction SilentlyContinue)
        $t = $all | Where-Object { $_.Name -eq '_Total' } | Select-Object -First 1
        if ($t) {
            [void]$tot.Add([pscustomobject]@{
                    Dpc = [double]$t.PercentDPCTime; Int = [double]$t.PercentInterruptTime
                    Queued = [double]$t.DPCsQueuedPersec; Ints = [double]$t.InterruptsPersec; Cpu = [double]$t.PercentProcessorTime
                })
        }
        foreach ($c in ($all | Where-Object { $_.Name -ne '_Total' })) {
            $v = [double]$c.PercentDPCTime + [double]$c.PercentInterruptTime
            if (-not $coreMax.ContainsKey($c.Name) -or $coreMax[$c.Name] -lt $v) { $coreMax[$c.Name] = $v }
        }
    }
    if (-not $tot.Count) { return [pscustomobject]@{ ok = $false; error = 'счётчики не отвечают' } }
    $avg = { param($p) [math]::Round((($tot | Measure-Object -Property $p -Average).Average), 2) }
    $max = { param($p) [math]::Round((($tot | Measure-Object -Property $p -Maximum).Maximum), 2) }
    $worst = $coreMax.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    $dpcAvg = & $avg 'Dpc'; $intAvg = & $avg 'Int'
    $worstVal = $(if ($worst) { [math]::Round([double]$worst.Value, 1) } else { 0 })
    # грубая оценка: на здоровом ПК DPC+прерывания в простое — доли процента
    $verdict = 'ok'
    # одно ядро: короткий всплеск до 20% бывает и на здоровом ПК, поэтому порог выше
    if (($dpcAvg + $intAvg) -ge 3 -or $worstVal -ge 50) { $verdict = 'bad' }
    elseif (($dpcAvg + $intAvg) -ge 1.5 -or $worstVal -ge 30) { $verdict = 'warn' }
    return [pscustomobject]@{
        ok         = $true
        seconds    = $tot.Count
        cpuAvg     = (& $avg 'Cpu')
        dpcAvg     = $dpcAvg
        dpcMax     = (& $max 'Dpc')
        intAvg     = $intAvg
        intMax     = (& $max 'Int')
        queuedAvg  = [math]::Round((& $avg 'Queued'))
        intsAvg    = [math]::Round((& $avg 'Ints'))
        worstCore  = $(if ($worst) { [string]$worst.Name } else { '' })
        worstValue = $worstVal
        verdict    = $verdict
    }
}

# ---------------------------------------------------------------- система

# программы, которые сами меняют систему или висят поверх игры: частые соучастники фризов
$script:PckitDiagKnownProcs = @(
    'MSIAfterburner', 'RTSS', 'EncoderServer', 'Hone', 'ProcessLasso', 'ProcessGovernor', 'ISLC', 'Razer Cortex', 'RazerCortex',
    'NZXT CAM', 'iCUE', 'ArmouryCrate', 'LightingService', 'SignalRgb', 'OpenRGB', 'WallpaperEngine', 'wallpaper32', 'wallpaper64',
    'Overwolf', 'Medal', 'NVIDIA Share', 'nvsphelper64', 'obs64', 'Discord', 'GameBar', 'XboxGameBarWidgets', 'AsusCertService',
    'HWiNFO64', 'AIDA64', 'CapFrameX', 'PresentMon', 'Lively', 'TranslucentTB', 'Rainmeter', 'FanControl', 'SteelSeriesGG', 'LGHUB'
)

function Get-PckitDiagReg {
    param([string]$Path, [string]$Name)
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

function Get-PckitDiagSystem {
    $s = [ordered]@{}
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $s.uptimeH = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        $s.ramTotalGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        $s.ramFreeGb = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
        $s.osBuild = ('{0}.{1}' -f $os.BuildNumber, (Get-PckitDiagReg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'UBR'))
    } catch { }
    try { $s.commitPct = [int](Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory).PercentCommittedBytesInUse } catch { }
    try {
        $pf = @(Get-CimInstance Win32_PageFileUsage)
        $s.pagefileMb = [int](($pf | Measure-Object -Property AllocatedBaseSize -Sum).Sum)
        $s.pagefileAuto = [bool](Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile
    } catch { }
    try {
        $d = Get-PSDrive -Name ($env:SystemDrive.Substring(0, 1)) -ErrorAction Stop
        $s.sysDiskFreeGb = [math]::Round($d.Free / 1GB, 1)
    } catch { }
    try { $s.powerPlan = Limit-PckitDiagText -Text ((& powercfg /getactivescheme 2>$null) -join ' ') -Max 120 } catch { }
    $hags = Get-PckitDiagReg 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
    $s.hags = $(if ($hags -eq 2) { 'on' } elseif ($hags -eq 1) { 'off' } else { 'default' })
    $s.mpoOff = ((Get-PckitDiagReg 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' 'OverlayTestMode') -eq 5)
    $gm = Get-PckitDiagReg 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled'
    $s.gameMode = $(if ($gm -eq 0) { 'off' } else { 'on' })
    $s.gameDvr = $(if ((Get-PckitDiagReg 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled') -eq 0) { 'off' } else { 'on' })
    $s.netThrottling = Get-PckitDiagReg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex'
    $s.win32Priority = Get-PckitDiagReg 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation'
    $s.globalTimer = Get-PckitDiagReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests'
    try {
        $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop
        $s.vbs = $(switch ([int]$dg.VirtualizationBasedSecurityStatus) { 2 { 'on' } 1 { 'enabled-not-running' } default { 'off' } })
    } catch { }
    # службы, которые часто выключают «оптимизаторы»
    $svc = [ordered]@{}
    foreach ($n in 'SysMain', 'WSearch', 'DiagTrack', 'XblAuthManager', 'Audiosrv', 'BITS', 'wuauserv') {
        try { $x = Get-Service -Name $n -ErrorAction Stop; $svc[$n] = ('{0}/{1}' -f $x.StartType, $x.Status) } catch { }
    }
    $s.services = [pscustomobject]$svc
    # процессы: топ по памяти и известные «соучастники»
    try {
        $procs = @(Get-Process -ErrorAction SilentlyContinue)
        $s.processCount = $procs.Count
        $s.topMemory = @($procs | Sort-Object WorkingSet64 -Descending | Select-Object -First 8 | ForEach-Object { '{0} {1} МБ' -f $_.ProcessName, [math]::Round($_.WorkingSet64 / 1MB) })
        $known = @{}
        foreach ($p in $procs) { foreach ($k in $script:PckitDiagKnownProcs) { if ($p.ProcessName -like ($k + '*')) { $known[$p.ProcessName] = $true } } }
        $s.known = @($known.Keys | Sort-Object)
    } catch { }
    try {
        $st = @(Get-PckitStartupEntries | Where-Object { $_.Enabled })
        $s.startupOn = $st.Count
        $s.startup = @($st | Select-Object -First 30 | ForEach-Object { Limit-PckitDiagText -Text ([string]$_.Name) -Max 60 })
    } catch { }
    return [pscustomobject]$s
}

# ---------------------------------------------------------------- подсказки автору

function Get-PckitDiagHints {
    param($Tweaks, $Events, $Dpc, $System)
    $h = New-Object System.Collections.ArrayList
    $risky = @($Tweaks.active | Where-Object { $_.risky })
    if ($risky.Count) { [void]$h.Add(('Включено «на свой риск»: {0} — {1}' -f $risky.Count, ((@($risky | ForEach-Object { $_.id })) -join ', '))) }
    if ($Tweaks.journalTotal -gt 0 -and $Tweaks.journal.Count -gt 0) { [void]$h.Add(('В журнале отката {0} записей по {1} настройкам' -f $Tweaks.journalTotal, $Tweaks.journal.Count)) }
    if (@($Tweaks.partial | Where-Object { $_ }).Count) { [void]$h.Add(('Включено частично: {0}' -f ((@($Tweaks.partial | ForEach-Object { $_.id })) -join ', '))) }
    foreach ($e in @($Events)) {
        if ($e.key -in 'gpu.tdr', 'gpu.nvidia', 'gpu.amd', 'whea', 'power41', 'bugcheck', 'lowmem', 'disk', 'nvme', 'ntfs') { [void]$h.Add(('{0}: {1} раз, последний {2}' -f $e.title, $e.count, $e.last)) }
    }
    if ($Dpc -and $Dpc.ok -and $Dpc.verdict -ne 'ok') { [void]$h.Add(('Высокие задержки драйверов: DPC {0}% + прерывания {1}%, ядро {2} до {3}%' -f $Dpc.dpcAvg, $Dpc.intAvg, $Dpc.worstCore, $Dpc.worstValue)) }
    if ($System) {
        if ($null -ne $System.pagefileMb -and $System.pagefileMb -eq 0) { [void]$h.Add('Файл подкачки выключен') }
        if ($System.ramFreeGb -and $System.ramFreeGb -lt 1.5) { [void]$h.Add(('Мало свободной памяти: {0} ГБ' -f $System.ramFreeGb)) }
        if ($System.sysDiskFreeGb -and $System.sysDiskFreeGb -lt 15) { [void]$h.Add(('Мало места на системном диске: {0} ГБ' -f $System.sysDiskFreeGb)) }
        if (@($System.known | Where-Object { $_ }).Count) { [void]$h.Add(('Запущены: {0}' -f ((@($System.known)) -join ', '))) }
    }
    return $h.ToArray()
}

# ---------------------------------------------------------------- весь отчёт

function Get-PckitDiagReport {
    param([string]$DataDir, [int]$DpcSeconds = 10, [scriptblock]$OnStep)
    $step = { param($t) if ($OnStep) { & $OnStep $t } }
    $errors = New-Object System.Collections.ArrayList

    & $step 'Железо и драйверы'
    $hw = $null
    try { $hw = Get-PckitHardwareProfile } catch { [void]$errors.Add('hw: ' + $_.Exception.Message) }

    & $step 'Какие настройки сейчас включены'
    $tweaks = $null
    try { $tweaks = Get-PckitDiagTweaks -Actions @(Get-PckitActions -Directory $DataDir) } catch { [void]$errors.Add('tweaks: ' + $_.Exception.Message) }

    & $step 'Журнал Windows за 7 дней: сбросы видеодрайвера, ошибки железа и дисков'
    $events = @()
    try { $events = @(Get-PckitDiagEvents -Days 7) } catch { [void]$errors.Add('events: ' + $_.Exception.Message) }

    & $step 'Система: память, подкачка, питание, автозагрузка'
    $system = $null
    try { $system = Get-PckitDiagSystem } catch { [void]$errors.Add('system: ' + $_.Exception.Message) }

    $dpc = $null
    if ($DpcSeconds -gt 0) {
        try { $dpc = Get-PckitDiagDpc -Seconds $DpcSeconds -OnStep $OnStep } catch { [void]$errors.Add('dpc: ' + $_.Exception.Message) }
    }

    & $step 'Проверка ПК'
    $health = @()
    try {
        $health = @((Get-PckitHealthReport).Findings | Where-Object { [string]$_.Severity -ne 'ok' } | ForEach-Object {
                [pscustomobject]@{ severity = [string]$_.Severity; title = (Limit-PckitDiagText -Text ([string]$_.Title) -Max 160); detail = (Limit-PckitDiagText -Text ([string]$_.Detail) -Max 200) } })
    } catch { [void]$errors.Add('health: ' + $_.Exception.Message) }

    $hwOut = $null
    if ($hw) {
        $drv = [string]$hw.GpuDriverVersion
        if ($hw.GpuVendor -eq 'nvidia') { try { $n = ConvertTo-PckitNvidiaVersion -WindowsVersion $drv; if ($n) { $drv = [string]$n } } catch { } }
        $hwOut = [pscustomobject]@{
            line = (Limit-PckitDiagText -Text (Format-PckitHardwareLine -Hw $hw) -Max 300)
            cpu = [string]$hw.CpuName; gpu = [string]$hw.GpuName; driver = $drv
            ramGb = $hw.RamGb; ramType = [string]$hw.RamType; ramMhz = $hw.RamMhz
            hz = $hw.PrimaryHz; laptop = [bool]$hw.Laptop; ssd = $hw.SystemSsd
        }
    }

    return [pscustomobject]@{
        created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        hints   = @(Get-PckitDiagHints -Tweaks $tweaks -Events $events -Dpc $dpc -System $system)
        hw      = $hwOut
        tweaks  = $tweaks
        events  = $events
        dpc     = $dpc
        system  = $system
        health  = $health
        errors  = $errors.ToArray()
    }
}
