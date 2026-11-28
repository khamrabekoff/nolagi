#Requires -Version 5.1
# pckit.exe <-> engine bridge. One command per run; every stdout line is one JSON message:
#   {"t":"step","text":"..."}                       - scan stage
#   {"t":"progress","i":3,"n":7,"text":"..."}       - long operation progress
#   {"t":"item","id":"...","ok":true,"msg":"..."}  - result for one item
#   {"t":"result", ...}                             - final payload
#   {"t":"error","msg":"..."}
param(
    [Parameter(Mandatory)][string]$Cmd,
    [string]$IdsFile = '',
    [string]$Root = ''
)

if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Out-Msg($obj) { [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 6 -Compress)); [Console]::Out.Flush() }
# ids come in a UTF-8 JSON file: names of startup entries may contain commas and quotes
function Get-IdList {
    if (-not $IdsFile -or -not (Test-Path -LiteralPath $IdsFile)) { return @() }
    $raw = [IO.File]::ReadAllText($IdsFile, [Text.Encoding]::UTF8)
    return @((ConvertFrom-Json $raw) | ForEach-Object { [string]$_ } | Where-Object { $_ })
}

try {
    foreach ($f in Get-ChildItem -LiteralPath (Join-Path $Root 'engine') -Filter *.ps1) { . $f.FullName }
    $dataDir = Join-Path $Root 'data'

    function Get-HwPayload($hw) {
        return [pscustomobject]@{
            line    = (Format-PckitHardwareLine -Hw $hw)
            cpu     = [string]$hw.CpuName
            gpu     = [string]$hw.GpuName
            ramGb   = $hw.RamGb
            ramType = [string]$hw.RamType
            ramMhz  = $hw.RamMhz
            ssd     = $hw.SystemSsd
            hz      = $hw.PrimaryHz
            laptop  = [bool]$hw.Laptop
            os      = $(try { ((Get-CimInstance Win32_OperatingSystem).Caption -replace '^Microsoft ','') + ' ' + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion } catch { '' })
            build   = [string]$hw.Build
            driver  = $($v = [string]$hw.GpuDriverVersion; if ($hw.GpuVendor -eq 'nvidia') { try { $n = ConvertTo-PckitNvidiaVersion -WindowsVersion $v; if ($n) { $v = [string]$n } } catch { } }; $v)
            wired   = [bool]$hw.WiredNetwork
            wifi    = [bool]$hw.WifiOnly
            cs2     = [bool]$hw.Cs2Path
            dota    = [bool]$hw.DotaPath
            raw     = $hw
        }
    }

    # бенчмарк CS2: включена ли запись консоли, новые результаты (забираются при каждом вызове) и прошлый результат
    function Get-BenchPayload {
        $new = @(); $readError = ''
        try { $new = @(Import-PckitCs2Bench) } catch { $readError = $_.Exception.Message }
        $last = Get-PckitBenchLatest -NewCount $new.Count
        return [pscustomobject]@{
            cs2       = [bool](Find-PckitCs2)
            condebug  = (Test-PckitCs2Condebug)
            newCount  = $new.Count
            latest    = $last.Latest
            previous  = $last.Previous
            readError = $readError
        }
    }

    # action ids that have journal entries: only these can be rolled back one by one
    function Get-UndoableIds {
        $j = Read-PckitJournal
        $set = @{}
        foreach ($k in $script:PckitJournalKinds) { foreach ($e in @($j.$k)) { if ($e -and $e.act) { $set[[string]$e.act] = $true } } }
        return $set
    }

    function Get-ActionRows($actions, $hw, [switch]$Progress) {
        $undoable = Get-UndoableIds
        $rows = New-Object System.Collections.ArrayList
        $n = @($actions).Count; $i = 0
        foreach ($a in $actions) {
            $i++
            if ($Progress -and ($i % 5 -eq 1)) { Out-Msg @{ t = 'progress'; i = $i; n = $n; text = [string]$a.title } }
            if ($a.myth) { continue }
            $st = Test-PckitActionState -Action $a
            if ($st.Status -eq 'unsupported') { continue }
            $adv = $null
            try { $adv = Get-PckitSmartAdvice -Action $a -Hw $hw } catch { }
            [void]$rows.Add([pscustomobject]@{
                    id          = [string]$a.id
                    group       = [string]$a.group
                    title       = (Expand-PckitActionText -Action $a -Text ([string]$a.title))
                    what        = (Expand-PckitActionText -Action $a -Text ([string]$a.what))
                    effect      = [string]$a.effect
                    level       = [string]$a.effectLevel
                    risk        = [string]$a.risk
                    danger      = [string]$a.danger
                    note        = [string]$a.note
                    expert      = [bool]$a.expert
                    status      = [string]$st.Status
                    notFit      = $(if ($st.Status -eq 'notfit') { [string](@($st.Checks)[0].Detail) } else { '' })
                    recommended = [bool]($adv -and $adv.Recommended)
                    reason      = $(if ($adv) { [string]$adv.Reason } else { '' })
                    undoable    = [bool]$undoable[[string]$a.id]
                })
        }
        return $rows.ToArray()
    }

    # команды, которые пишут журнал отката, — строго по одной: иначе две копии моста затрут записи друг друга
    if ($Cmd -in 'apply', 'undo', 'undoall', 'startup-off', 'startup-on', 'bench-enable', 'dns-set', 'prio-set', 'prio-unset') {
        $journalLock = New-Object System.Threading.Mutex($false, 'Local\NolagiJournal')
        try { [void]$journalLock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
    }

    switch ($Cmd) {
        'scan' {
            Out-Msg @{ t = 'step'; key = 'cpu'; text = 'Процессор и память' }
            $hw = Get-PckitHardwareProfile
            Out-Msg @{ t = 'step'; key = 'gpu'; text = 'Видеокарта и драйвер' }
            Out-Msg @{ t = 'step'; key = 'disk'; text = 'Диски и сеть' }
            Out-Msg @{ t = 'step'; key = 'cs2'; text = 'Steam, CS2 и Dota 2' }
            # 1.5.1: убрать красный счётчик fps в Dota 2 у тех, кто включал прежнюю настройку
            try {
                $dl = New-Object System.Threading.Mutex($false, 'Local\NolagiJournal')
                try { [void]$dl.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
                try { [void](Remove-PckitDotaFpsCounter) } finally { $dl.ReleaseMutex(); $dl.Dispose() }
            } catch { }
            $actions = @(Get-PckitActions -Directory $dataDir)
            Out-Msg @{ t = 'step'; key = 'tweaks'; text = 'Текущие настройки Windows' }
            $rows = Get-ActionRows $actions $hw -Progress
            $j = Read-PckitJournal
            Out-Msg @{ t = 'result'; hw = (Get-HwPayload $hw); actions = $rows; undoCount = (Get-PckitJournalCount -Journal $j); myths = @($actions | Where-Object { $_.myth } | ForEach-Object { [pscustomobject]@{ title = [string]$_.title; what = [string]$_.what; effect = [string]$_.effect } }) }
        }
        'state' {
            $hw = Get-PckitHardwareProfile
            $want = Get-IdList
            $actions = @(Get-PckitActions -Directory $dataDir | Where-Object { -not $want.Count -or $want -contains $_.id })
            Out-Msg @{ t = 'result'; actions = (Get-ActionRows $actions $hw); undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'apply' {
            $want = Get-IdList
            $all = @(Get-PckitActions -Directory $dataDir)
            # no @() around it: the function returns , @(...) and @() would nest the array
            $sel = Sort-PckitActionsPowerFirst -Actions @($all | Where-Object { $want -contains $_.id })
            $n = $sel.Count + 1
            Out-Msg @{ t = 'progress'; i = 0; n = $n; text = 'Точка восстановления Windows' }
            $rp = $null
            try { $rp = New-PckitRestorePoint -Run } catch { }
            $i = 0
            foreach ($a in $sel) {
                $i++
                Out-Msg @{ t = 'progress'; i = $i; n = $n; text = (Expand-PckitActionText -Action $a -Text ([string]$a.title)) }
                try {
                    $res = Invoke-PckitActions -Actions @($a) -Run
                    $r = @($res.Results)[0]
                    $fails = @($r.Results | Where-Object { $_.Status -eq 'fail' })
                    $skips = @($r.Results | Where-Object { $_.Status -eq 'skip' })
                    if ($fails.Count) { Out-Msg @{ t = 'item'; id = [string]$a.id; ok = $false; msg = (($fails | ForEach-Object { $_.Message }) -join '; ') } }
                    # пропуск (службы или задачи нет на этом ПК) — не ошибка: итог решает проверка после применения
                    elseif ($skips.Count) { Out-Msg @{ t = 'item'; id = [string]$a.id; ok = $true; msg = ('пропущено: {0}' -f (($skips | ForEach-Object { $_.Message }) -join '; ')) } }
                    else { Out-Msg @{ t = 'item'; id = [string]$a.id; ok = $true; msg = '' } }
                } catch {
                    Out-Msg @{ t = 'item'; id = [string]$a.id; ok = $false; msg = $_.Exception.Message }
                }
            }
            Out-Msg @{ t = 'progress'; i = $n; n = $n; text = 'Проверяю, что всё применилось' }
            $hw = Get-PckitHardwareProfile
            Out-Msg @{ t = 'result'; actions = (Get-ActionRows $sel $hw); restorePoint = $(if ($rp) { [string]$rp.Status } else { 'failed' }); undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'undo' {
            $want = Get-IdList
            $all = @(Get-PckitActions -Directory $dataDir)
            $i = 0
            foreach ($id in $want) {
                $i++
                $a = $all | Where-Object { $_.id -eq $id } | Select-Object -First 1
                $title = if ($a) { Expand-PckitActionText -Action $a -Text ([string]$a.title) } else { $id }
                Out-Msg @{ t = 'progress'; i = $i; n = $want.Count; text = $title }
                try {
                    $r = Restore-PckitJournal -Journal (Read-PckitJournal) -Run -ActionId $id
                    Out-Msg @{ t = 'item'; id = $id; ok = ($r.Failed -eq 0 -and $r.Ok -gt 0); msg = $(if ($r.Failed) { (@($r.Items | Where-Object { $_.Status -eq 'fail' } | ForEach-Object { $_.Message }) -join '; ') } elseif ($r.Ok -eq 0) { 'нечего откатывать: настройку включили не через Nolagi' } else { '' }) }
                } catch { Out-Msg @{ t = 'item'; id = $id; ok = $false; msg = $_.Exception.Message } }
            }
            $hw = Get-PckitHardwareProfile
            Out-Msg @{ t = 'result'; actions = (Get-ActionRows @($all | Where-Object { $want -contains $_.id }) $hw); undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'undoall' {
            Out-Msg @{ t = 'progress'; i = 0; n = 1; text = 'Возвращаю все изменения Nolagi' }
            $r = Restore-PckitJournal -Journal (Read-PckitJournal) -Run
            foreach ($it in @($r.Items)) { Out-Msg @{ t = 'item'; id = [string]$it.Title; ok = ($it.Status -ne 'fail'); msg = [string]$it.Message } }
            Out-Msg @{ t = 'result'; ok = $r.Ok; failed = $r.Failed; undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'clean-list' {
            $targets = @(Get-PckitCleanTargets | Where-Object { $_.Kind -notin 'info', 'dism', 'drivers' })
            $rows = New-Object System.Collections.ArrayList
            $i = 0
            foreach ($t in $targets) {
                $i++
                Out-Msg @{ t = 'progress'; i = $i; n = $targets.Count; text = [string]$t.Title }
                $s = Get-PckitCleanSize -Target $t
                [void]$rows.Add([pscustomobject]@{ id = [string]$t.Id; group = [string]$t.Group; title = [string]$t.Title; what = [string]$t.What; risk = [string]$t.Risk; note = [string]$t.Note; sizeMb = [math]::Round([double]$s.SizeMb, 1) })
            }
            Out-Msg @{ t = 'result'; items = $rows.ToArray() }
        }
        'clean' {
            $want = Get-IdList
            $sel = @(Get-PckitCleanTargets | Where-Object { $want -contains $_.Id })
            $i = 0; $total = 0.0
            foreach ($t in $sel) {
                $i++
                Out-Msg @{ t = 'progress'; i = $i; n = $sel.Count; text = [string]$t.Title }
                try {
                    $r = @((Invoke-PckitClean -Targets @($t) -Run).Results)[0]
                    if ($r.FreedMb -gt 0) { $total += $r.FreedMb }
                    Out-Msg @{ t = 'item'; id = [string]$t.Id; ok = ($r.Status -ne 'fail'); msg = $(if ($r.Status -eq 'fail') { [string]$r.Message } else { ('освобождено {0} МБ' -f $r.FreedMb) }) }
                } catch { Out-Msg @{ t = 'item'; id = [string]$t.Id; ok = $false; msg = $_.Exception.Message } }
            }
            Out-Msg @{ t = 'result'; freedMb = [math]::Round($total, 1) }
        }
        'startup-list' {
            $rows = @(Get-PckitStartupEntries | ForEach-Object {
                    # окно ждёт off/keep/'' (движок отдаёт русский текст совета)
                    $adv = switch ([string]$_.Advice) { 'можно отключить' { 'off' } 'нужно' { 'keep' } default { '' } }
                    $kind = $(if ($_.PSObject.Properties['Kind'] -and $_.Kind) { [string]$_.Kind } else { 'app' })
                    [pscustomobject]@{ id = [string]$_.Id; kind = $kind; name = [string]$_.Name; description = [string]$_.Description; publisher = [string]$_.Publisher; where = [string]$_.Where; enabled = [bool]$_.Enabled; advice = $adv; adviceNote = [string]$_.AdviceNote }
                })
            Out-Msg @{ t = 'result'; items = $rows }
        }
        { $_ -in 'startup-off', 'startup-on' } {
            $want = Get-IdList
            $on = ($Cmd -eq 'startup-on')
            $sel = @(Get-PckitStartupEntries | Where-Object { $want -contains $_.Id })
            $journal = Read-PckitJournal
            $ok = 0; $failed = 0; $i = 0
            foreach ($e in $sel) {
                $i++
                Out-Msg @{ t = 'progress'; i = $i - 1; n = $sel.Count; text = [string]$e.Name }
                $r = Set-PckitStartupEnabled -Entry $e -Enabled $on -Journal $journal
                if ($r.Status -eq 'ok') { $ok++ } else { $failed++ }
                Out-Msg @{ t = 'item'; id = [string]$e.Id; name = [string]$e.Name; ok = ($r.Status -eq 'ok'); msg = [string]$r.Message }
            }
            if (-not (Test-PckitJournalEmpty -Journal $journal)) { [void](Save-PckitJournal -Journal $journal) }
            Out-Msg @{ t = 'result'; ok = $ok; failed = $failed }
        }
        'health' {
            $rep = Get-PckitHealthReport
            $rows = @($rep.Findings | ForEach-Object { [pscustomobject]@{ severity = [string]$_.Severity; title = [string]$_.Title; detail = [string]$_.Detail; hint = [string]$_.Hint; link = [string]$_.Link } })
            Out-Msg @{ t = 'result'; items = $rows }
        }
        'repair-system' {
            $r = Invoke-PckitRepairSystem -OnStep { param($text) Out-Msg @{ t = 'progress'; i = 0; n = 0; text = $text } }
            Out-Msg @{ t = 'result'; dism = $r.DismCode; sfcCode = $r.SfcCode; sfc = $r.Sfc }
        }
        'repair-network' {
            $steps = @(Invoke-PckitRepairNetwork)
            foreach ($s in $steps) { Out-Msg @{ t = 'item'; id = $s.Text; name = $s.Text; ok = $s.Ok; msg = '' } }
            Out-Msg @{ t = 'result'; ok = @($steps | Where-Object { $_.Ok }).Count; failed = @($steps | Where-Object { -not $_.Ok }).Count }
        }
        'dns-bench' {
            $b = Get-PckitDnsBenchmark -OnStep { param($text) Out-Msg @{ t = 'progress'; i = 0; n = 0; text = $text } }
            $rows = @($b.Rows | ForEach-Object { [pscustomobject]@{ key = $_.Key; name = $_.Name; servers = $_.Servers; ms = $(if ($null -eq $_.Ms) { -1 } else { $_.Ms }); current = [bool]$_.Current; doh = [bool]$_.Doh; note = [string]$_.Note } })
            Out-Msg @{ t = 'result'; items = $rows; adapters = @($b.Adapters); win11 = ([Environment]::OSVersion.Version.Build -ge 22000) }
        }
        'dns-set' {
            # ids: [key, "doh"?]
            $want = @(Get-IdList)
            $journal = Read-PckitJournal
            $done = @(Set-PckitDns -Key ([string]$want[0]) -Doh ($want -contains 'doh') -Journal $journal)
            if (-not (Test-PckitJournalEmpty -Journal $journal)) { [void](Save-PckitJournal -Journal $journal) }
            foreach ($d in $done) { Out-Msg @{ t = 'item'; id = $d.Alias; name = $d.Alias; ok = $d.Ok; msg = $d.Message } }
            Out-Msg @{ t = 'result'; ok = @($done | Where-Object { $_.Ok }).Count; failed = @($done | Where-Object { -not $_.Ok }).Count; undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'apps-list' {
            $rows = @(Get-PckitBloatApps | ForEach-Object { [pscustomobject]@{ id = $_.Id; name = $_.Name; level = $_.Level; note = $_.Note } })
            Out-Msg @{ t = 'result'; items = $rows }
        }
        'apps-remove' {
            $want = @(Get-IdList)
            $all = @(Get-PckitBloatApps)
            $sel = @($all | Where-Object { $want -contains $_.Id })
            $ok = 0; $failed = 0; $i = 0
            foreach ($a in $sel) {
                Out-Msg @{ t = 'progress'; i = $i; n = $sel.Count; text = [string]$a.Name }
                $i++
                $r = Remove-PckitBloatApp -Id $a.Id
                if ($r.Ok) { $ok++ } else { $failed++ }
                Out-Msg @{ t = 'item'; id = [string]$a.Id; name = [string]$a.Name; ok = [bool]$r.Ok; msg = [string]$r.Message }
            }
            Out-Msg @{ t = 'result'; ok = $ok; failed = $failed }
        }
        'prio-list' {
            $set = @(Get-PckitPriorityExes)
            $games = @(Get-PckitSteamGames | ForEach-Object { [pscustomobject]@{ name = $_.Name; exe = $_.Exe; path = $_.Path; high = ($set -contains $_.Exe) } })
            Out-Msg @{ t = 'result'; games = $games; high = $set }
        }
        { $_ -in 'prio-set', 'prio-unset' } {
            $want = @(Get-IdList)
            $high = ($Cmd -eq 'prio-set')
            $journal = Read-PckitJournal
            $ok = 0; $failed = 0
            foreach ($exe in $want) {
                $r = Set-PckitGamePriority -Exe ([string]$exe) -High $high -Journal $journal
                if ($r.Ok) { $ok++ } else { $failed++ }
                Out-Msg @{ t = 'item'; id = [string]$exe; name = [string]$exe; ok = [bool]$r.Ok; msg = [string]$r.Message }
            }
            if (-not (Test-PckitJournalEmpty -Journal $journal)) { [void](Save-PckitJournal -Journal $journal) }
            Out-Msg @{ t = 'result'; ok = $ok; failed = $failed; undoCount = (Get-PckitJournalCount -Journal (Read-PckitJournal)) }
        }
        'fps-status' {
            Out-Msg @{
                t       = 'result'
                bench   = (Get-BenchPayload)
                history = @(Get-PckitFpsHistory -Game 'cs2' -Source 'bench')
            }
        }
        'bench-check' { Out-Msg @{ t = 'result'; bench = (Get-BenchPayload) } }
        'bench-enable' {
            $a = @(Get-PckitActions -Directory $dataDir | Where-Object { $_.id -eq 'cs2.condebug' })
            $res = Invoke-PckitActions -Actions $a -Run
            $fails = @(@(@($res.Results)[0].Results) | Where-Object { $_.Status -in 'fail', 'skip' })
            if ($fails.Count) { throw ([string]$fails[0].Message) }
            # всё, что уже лежит в console.log, — старое: в историю попадут только следующие прогоны.
            # Сам Reset при нечитаемом логе запоминает его длину; сюда долетает только ошибка записи состояния.
            $warn = ''
            try { Reset-PckitCs2BenchState } catch { $warn = ('Запись консоли включена, но не удалось запомнить конец console.log ({0}) — первый результат может оказаться старым.' -f $_.Exception.Message) }
            Out-Msg @{ t = 'result'; bench = (Get-BenchPayload); warn = $warn }
        }
        'bios-info' { Out-Msg @{ t = 'result'; bios = (Get-PckitBiosInfo) } }
        'bios-restart' { Restart-PckitToFirmware; Out-Msg @{ t = 'result'; ok = $true } }
        'diag' {
            # отчёт о проблеме: только чтение, отправляет окно
            $rep = Get-PckitDiagReport -DataDir $dataDir -DpcSeconds 10 -OnStep { param($text) Out-Msg @{ t = 'progress'; i = 0; n = 0; text = $text } }
            Out-Msg @{ t = 'result'; report = $rep }
        }
        default { throw "unknown command: $Cmd" }
    }
} catch {
    Out-Msg @{ t = 'error'; msg = $_.Exception.Message }
    exit 1
} finally {
    if ($journalLock) { try { $journalLock.ReleaseMutex() } catch { }; $journalLock.Dispose() }
}
