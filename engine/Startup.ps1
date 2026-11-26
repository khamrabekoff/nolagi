#Requires -Version 5.1
# pckit engine | startup: программы в автозагрузке — список, совет, включить/выключить с откатом
# Механизм тот же, что у Диспетчера задач: ...\Explorer\StartupApproved (первый байт 02 = включено, 03 = выключено).
# Сама запись в Run и ярлык в папке «Автозагрузка» не удаляются — программу всегда можно включить обратно.

$script:PckitStartupKeep = 'SecurityHealth|Windows Defender|Realtek|RtkAud|RAVCpl|NVIDIA|nvcpl|NvBackend|AMD|Radeon|Intel|igfx|IAStor|FACEIT|Vanguard|EasyAntiCheat|BattlEye|Logitech|LGHUB|Razer|Synaptics|ELAN|Bluetooth|SteelSeries|Corsair|iCUE|Wacom|HD Audio|Waves|Dolby|Nahimic|ASUS|Armoury|GIGABYTE|MSI Center|Dragon Center|Lenovo|Vantage|HP |Dell'
$script:PckitStartupOff = 'Steam|Discord|Epic ?Games|EpicGamesLauncher|EADesktop|EA app|Origin|Ubisoft|uplay|Battle\.net|GOG Galaxy|Spotify|Teams|Skype|Telegram|Yandex|Opera|Brave|Vivaldi|MicrosoftEdgeAutoLaunch|Zoom|uTorrent|qBittorrent|BitTorrent|AnyDesk|TeamViewer|Adobe|CCleaner|Overwolf|Medal|Viber|WhatsApp|Slack|Notion|Dropbox|Google ?Drive|GoogleDrive|Cortana|Copilot|Rockstar|Riot Client|Wallpaper ?Engine|Lightshot|Punto|Outplayed|ChromeAutoLaunch'

function Get-PckitStartupExe {
    # из командной строки автозапуска достаём путь к exe
    param([string]$Command)
    if (-not $Command) { return $null }
    $cmd = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    $path = $null
    if ($cmd.StartsWith('"')) {
        $end = $cmd.IndexOf('"', 1)
        if ($end -gt 1) { $path = $cmd.Substring(1, $end - 1) }
    } elseif ($cmd -match '^(.+?\.(exe|cmd|bat|com))(\s|$)') {
        $path = $matches[1]
    } else {
        $path = ($cmd -split '\s+')[0]
    }
    return $path
}

function Get-PckitStartupAdvice {
    param([string]$Name, [string]$Exe, [string]$Publisher, [switch]$Task)
    $hay = ('{0} {1} {2}' -f $Name, $Exe, $Publisher)
    if ($hay -match $script:PckitStartupKeep) {
        return [pscustomobject]@{ Advice = 'нужно'; Note = 'драйвер, античит, защита Windows или утилита устройства — лучше оставить' }
    }
    # задачи-обновлялки (Google, Edge, Opera, Adobe…): выключить можно, но программа перестанет обновляться сама
    if ($Task -and $hay -match 'Update|Updater|Upgrade|AutoUpdate|Обновл') {
        return [pscustomobject]@{ Advice = 'на ваше усмотрение'; Note = 'это автообновление: если отключить, программа будет обновляться только при запуске или вручную — для браузеров лучше оставить' }
    }
    if ($hay -match $script:PckitStartupOff) {
        return [pscustomobject]@{ Advice = 'можно отключить'; Note = 'программа запустится сама, когда вы её откроете; Windows будет стартовать быстрее' }
    }
    return [pscustomobject]@{ Advice = 'на ваше усмотрение'; Note = 'если не знаете, что это, — лучше оставить' }
}

function Get-PckitApprovedState {
    # $true = включено (нет записи тоже значит «включено»)
    param([string]$ApprovedPath, [string]$Name)
    try {
        $key = Get-Item -LiteralPath $ApprovedPath -ErrorAction Stop
        $val = $key.GetValue($Name, $null)
        if ($val -is [byte[]] -and $val.Length -gt 0 -and (($val[0] -band 1) -eq 1)) { return $false }
    } catch { }
    return $true
}

function Get-PckitStartupEntries {
    $list = New-Object System.Collections.ArrayList
    $approvedRoot = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved'

    $sources = @(
        @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKCU'; Sub = 'Run'; Where = 'реестр, этот пользователь'; Admin = $false },
        @{ Path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKLM'; Sub = 'Run'; Where = 'реестр, все пользователи'; Admin = $true },
        @{ Path = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'; Hive = 'HKLM'; Sub = 'Run32'; Where = 'реестр, все пользователи (32-бит)'; Admin = $true }
    )
    foreach ($src in $sources) {
        $key = $null
        try { $key = Get-Item -LiteralPath $src.Path -ErrorAction Stop } catch { continue }
        foreach ($n in $key.GetValueNames()) {
            if (-not $n) { continue }
            $command = [string]$key.GetValue($n, '', 'DoNotExpandEnvironmentNames')
            [void]$list.Add((New-PckitStartupEntry -Name $n -Command $command -Where $src.Where -Admin $src.Admin `
                        -ApprovedPath ('{0}:\{1}\{2}' -f $src.Hive, $approvedRoot, $src.Sub) -Id ('{0}|{1}|{2}' -f $src.Hive, $src.Sub, $n)))
        }
    }

    $folders = @(
        @{ Path = [Environment]::GetFolderPath('Startup'); Hive = 'HKCU'; Where = 'папка «Автозагрузка», этот пользователь'; Admin = $false },
        @{ Path = [Environment]::GetFolderPath('CommonStartup'); Hive = 'HKLM'; Where = 'папка «Автозагрузка», все пользователи'; Admin = $true }
    )
    $shell = $null
    foreach ($fd in $folders) {
        if (-not $fd.Path -or -not (Test-Path -LiteralPath $fd.Path)) { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $fd.Path -File -ErrorAction SilentlyContinue)) {
            if ($f.Name -ieq 'desktop.ini') { continue }
            $command = $f.FullName
            if ($f.Extension -ieq '.lnk') {
                try {
                    if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }
                    $lnk = $shell.CreateShortcut($f.FullName)
                    if ($lnk.TargetPath) { $command = ('"{0}" {1}' -f $lnk.TargetPath, $lnk.Arguments).Trim() }
                } catch { }
            }
            $display = [IO.Path]::GetFileNameWithoutExtension($f.Name)
            [void]$list.Add((New-PckitStartupEntry -Name $display -ApprovedName $f.Name -Command $command -Where $fd.Where -Admin $fd.Admin `
                        -ApprovedPath ('{0}:\{1}\StartupFolder' -f $fd.Hive, $approvedRoot) -Id ('{0}|StartupFolder|{1}' -f $fd.Hive, $f.Name)))
        }
    }
    foreach ($t in @(Get-PckitStartupTasks)) { [void]$list.Add($t) }
    # без «запятой»: вызывающий код оборачивает результат в @() сам
    return @($list | Sort-Object -Property @{ Expression = { -not $_.Enabled } }, Name)
}

# ---- задачи планировщика: только сторонние (не из \Microsoft\), только вкл/выкл, без удаления

function Get-PckitTaskTriggerText {
    # понятное «когда запускается» по триггерам задачи
    param($Task)
    $parts = New-Object System.Collections.ArrayList
    foreach ($tr in @($Task.Triggers)) {
        if (-not $tr) { continue }
        $cls = [string]$tr.CimClass.CimClassName
        $txt = switch -Wildcard ($cls) {
            '*LogonTrigger'        { 'при входе в Windows' }
            '*BootTrigger'         { 'при включении ПК' }
            '*DailyTrigger'        { 'каждый день' }
            '*WeeklyTrigger'       { 'раз в неделю' }
            '*MonthlyTrigger'      { 'раз в месяц' }
            '*IdleTrigger'         { 'когда ПК простаивает' }
            '*EventTrigger'        { 'по событию в системе' }
            '*SessionStateChange*' { 'при разблокировке или подключении' }
            '*RegistrationTrigger' { 'после установки задачи' }
            '*TimeTrigger'         { 'по расписанию' }
            default                { 'по расписанию' }
        }
        $rep = $null
        try { $rep = [string]$tr.Repetition.Interval } catch { }
        if ($rep -match '^PT(?:(\d+)H)?(?:(\d+)M)?$' -and ($matches[1] -or $matches[2])) {
            $h = [int]$matches[1]; $m = [int]$matches[2]
            $every = if ($h -and $m) { '{0} ч {1} мин' -f $h, $m } elseif ($h) { '{0} ч' -f $h } else { '{0} мин' -f $m }
            $txt = ('{0}, потом каждые {1}' -f $txt, $every)
        }
        if (-not $parts.Contains($txt)) { [void]$parts.Add($txt) }
    }
    if ($parts.Count -eq 0) { return 'только вручную' }
    return ($parts -join '; ')
}

function Get-PckitStartupTasks {
    $list = New-Object System.Collections.ArrayList
    $tasks = @()
    try { $tasks = @(Get-ScheduledTask -ErrorAction Stop) } catch { return @() }
    foreach ($t in $tasks) {
        $tp = [string]$t.TaskPath
        if ($tp -like '\Microsoft\*') { continue }
        $full = $tp + [string]$t.TaskName
        # такие имена не передать в schtasks через cmd без риска (откат идёт через cmd /c)
        if ($full -match '["%^]') { continue }
        $act = @($t.Actions | Where-Object { $_ -and $_.PSObject.Properties['Execute'] -and $_.Execute }) | Select-Object -First 1
        $command = ''
        if ($act) { $command = ('"{0}" {1}' -f ([string]$act.Execute).Trim('"'), [string]$act.Arguments).Trim() }
        $exe = Get-PckitStartupExe -Command $command
        $publisher = ''
        $description = [string]$t.Description
        if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
            try {
                $vi = (Get-Item -LiteralPath $exe -ErrorAction Stop).VersionInfo
                $publisher = [string]$vi.CompanyName
                if (-not $description) { $description = [string]$vi.FileDescription }
            } catch { }
        }
        # системные задачи Windows бывают и вне \Microsoft\ — их программа лежит в папке Windows
        if ($exe -and $env:windir -and $exe.StartsWith($env:windir + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $description = ($description -replace '\s+', ' ').Trim()
        if ($description.Length -gt 160) { $description = $description.Substring(0, 157) + '…' }
        $advice = Get-PckitStartupAdvice -Name ([string]$t.TaskName) -Exe $exe -Publisher $publisher -Task
        [void]$list.Add([pscustomobject]@{
                Id           = ('TASK|{0}' -f $full)
                Kind         = 'task'
                Name         = (([string]$t.TaskName) -replace '[_ ]?\{[0-9A-Fa-f-]{36}\}$', '')
                Description  = $description
                Command      = $command
                Exe          = $exe
                Exists       = [bool]($exe -and (Test-Path -LiteralPath $exe))
                Publisher    = $publisher
                Where        = ('планировщик: {0}' -f (Get-PckitTaskTriggerText -Task $t))
                Admin        = $true
                TaskPath     = $tp
                TaskName     = [string]$t.TaskName
                FullPath     = $full
                ApprovedPath = ''
                ApprovedName = ''
                Enabled      = ([string]$t.State -ne 'Disabled')
                Advice       = $advice.Advice
                AdviceNote   = $advice.Note
            })
    }
    return @($list)
}

function Set-PckitStartupTaskEnabled {
    param($Entry, [bool]$Enabled, $Journal)
    try {
        if ($Enabled) {
            Enable-ScheduledTask -TaskPath $Entry.TaskPath -TaskName $Entry.TaskName -ErrorAction Stop | Out-Null
            $undo = 'schtasks /Change /TN "{0}" /DISABLE' -f $Entry.FullPath
        } else {
            Disable-ScheduledTask -TaskPath $Entry.TaskPath -TaskName $Entry.TaskName -ErrorAction Stop | Out-Null
            $undo = 'schtasks /Change /TN "{0}" /ENABLE' -f $Entry.FullPath
        }
        if ($Journal) { Add-PckitCommandTracked -Journal $Journal -Undo $undo -Note ('задача {0}' -f $Entry.TaskName) }
        return [pscustomobject]@{ Status = 'ok'; Message = '' }
    } catch {
        return [pscustomobject]@{ Status = 'fail'; Message = $_.Exception.Message }
    }
}

function New-PckitStartupEntry {
    param([string]$Name, [string]$ApprovedName, [string]$Command, [string]$Where, [bool]$Admin, [string]$ApprovedPath, [string]$Id)
    if (-not $ApprovedName) { $ApprovedName = $Name }
    $exe = Get-PckitStartupExe -Command $Command
    $publisher = ''
    $description = ''
    if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
        try {
            $vi = (Get-Item -LiteralPath $exe -ErrorAction Stop).VersionInfo
            $publisher = [string]$vi.CompanyName
            $description = [string]$vi.FileDescription
        } catch { }
    }
    $advice = Get-PckitStartupAdvice -Name $Name -Exe $exe -Publisher $publisher
    return [pscustomobject]@{
        Id           = $Id
        Name         = $Name
        Description  = $description
        Command      = $Command
        Exe          = $exe
        Exists       = [bool]($exe -and (Test-Path -LiteralPath $exe))
        Publisher    = $publisher
        Where        = $Where
        Admin        = $Admin
        ApprovedPath = $ApprovedPath
        ApprovedName = $ApprovedName
        Enabled      = (Get-PckitApprovedState -ApprovedPath $ApprovedPath -Name $ApprovedName)
        Advice       = $advice.Advice
        AdviceNote   = $advice.Note
    }
}

function Get-PckitApprovedBytes {
    # формат Диспетчера задач: 12 байт; выключено = 03 00 00 00 + FILETIME момента отключения
    param([bool]$Enabled)
    if ($Enabled) { return [byte[]](2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) }
    $ft = [BitConverter]::GetBytes([DateTime]::Now.ToFileTime())
    return [byte[]](@(3, 0, 0, 0) + $ft)
}

function Set-PckitStartupEnabled {
    param($Entry, [bool]$Enabled, $Journal)
    if ($Entry.Enabled -eq $Enabled) { return [pscustomobject]@{ Status = 'ok'; Message = 'уже так' } }
    if ($Entry.PSObject.Properties['Kind'] -and $Entry.Kind -eq 'task') { return (Set-PckitStartupTaskEnabled -Entry $Entry -Enabled $Enabled -Journal $Journal) }
    try {
        $bytes = Get-PckitApprovedBytes -Enabled $Enabled
        if ($Journal) {
            Add-PckitRegTracked -Journal $Journal -Path $Entry.ApprovedPath -Name $Entry.ApprovedName -Value $bytes -Type 'Binary'
        } else {
            if (-not (Test-Path -LiteralPath $Entry.ApprovedPath)) { New-Item -Path $Entry.ApprovedPath -Force | Out-Null }
            New-ItemProperty -LiteralPath $Entry.ApprovedPath -Name $Entry.ApprovedName -Value $bytes -PropertyType Binary -Force | Out-Null
        }
        return [pscustomobject]@{ Status = 'ok'; Message = '' }
    } catch {
        return [pscustomobject]@{ Status = 'fail'; Message = $_.Exception.Message }
    }
}

function Set-PckitStartupEntries {
    # применяет пачку изменений с одним журналом отката
    param([object[]]$Entries, [bool]$Enabled)
    $journal = Read-PckitJournal
    $ok = 0; $failed = 0
    $messages = New-Object System.Collections.ArrayList
    foreach ($e in $Entries) {
        $r = Set-PckitStartupEnabled -Entry $e -Enabled $Enabled -Journal $journal
        if ($r.Status -eq 'ok') { $ok++ } else { $failed++; [void]$messages.Add(('{0}: {1}' -f $e.Name, $r.Message)) }
    }
    if (-not (Test-PckitJournalEmpty -Journal $journal)) { [void](Save-PckitJournal -Journal $journal) }
    return [pscustomobject]@{ Ok = $ok; Failed = $failed; Messages = $messages.ToArray() }
}
