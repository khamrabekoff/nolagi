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

    switch ($Cmd) {
        'scan' {
            Out-Msg @{ t = 'step'; key = 'cpu'; text = 'Процессор и память' }
            $hw = Get-PckitHardwareProfile
            Out-Msg @{ t = 'step'; key = 'gpu'; text = 'Видеокарта и драйвер' }
            Out-Msg @{ t = 'step'; key = 'disk'; text = 'Диски и сеть' }
            Out-Msg @{ t = 'step'; key = 'cs2'; text = 'Steam, CS2 и Dota 2' }
            $actions = @(Get-PckitActions -Directory $dataDir)
            Out-Msg @{ t = 'step'; key = 'tweaks'; text = 'Текущие настройки Windows' }
            $rows = Get-ActionRows $actions $hw -Progress
            $j = Read-PckitJournal
            Out-Msg @{ t = 'result'; hw = (Get-HwPayload $hw); actions = $rows; undoCount = (Get-PckitJournalCount -Journal $j) }
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
                    elseif ($skips.Count) { Out-Msg @{ t = 'item'; id = [string]$a.id; ok = $false; msg = [string]$skips[0].Message } }
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
                    [pscustomobject]@{ id = [string]$_.Id; name = [string]$_.Name; description = [string]$_.Description; publisher = [string]$_.Publisher; enabled = [bool]$_.Enabled; advice = [string]$_.Advice; adviceNote = [string]$_.AdviceNote }
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
        'fps-status' {
            Out-Msg @{
                t          = 'result'
                presentMon = [bool](Find-PckitPresentMon)
                games      = @(Get-PckitFpsGames | ForEach-Object { [pscustomobject]@{ id = $_.Id; name = $_.Name; found = $_.Found; running = (Test-PckitProcessRunning $_.Exe) } })
                history    = @(Get-PckitFpsHistory)
                faceit     = [bool](@(Get-Process -Name 'FACEIT', 'faceitclient' -ErrorAction SilentlyContinue).Count)
            }
        }
        'fps-install' {
            Out-Msg @{ t = 'progress'; i = 0; n = 2; text = 'Узнаю последнюю версию PresentMon' }
            $rel = Get-PckitPresentMonRelease
            Out-Msg @{ t = 'progress'; i = 1; n = 2; text = ('Скачиваю {0} и проверяю подпись Intel' -f $rel.Name) }
            $exe = Install-PckitPresentMon -Url $rel.Url -Name $rel.Name
            Out-Msg @{ t = 'result'; ok = $true; version = [string]$rel.Version; path = [string]$exe }
        }
        'fps-measure' {
            # ids: [игра, секунды]. Окно отменяет замер файлом fps-cancel.flag рядом с движком.
            $want = Get-IdList
            $game = @(Get-PckitFpsGames | Where-Object { $_.Id -eq $want[0] })[0]
            if (-not $game) { throw 'неизвестная игра' }
            $seconds = 60
            if ($want.Count -gt 1) { $seconds = [math]::Max(10, [math]::Min(300, [int]$want[1])) }
            $pm = Find-PckitPresentMon
            if (-not $pm) { throw 'PresentMon не установлен' }
            $cancelFlag = Join-Path $Root 'fps-cancel.flag'
            Remove-Item -LiteralPath $cancelFlag -Force -ErrorAction SilentlyContinue
            $cancelled = { Test-Path -LiteralPath $cancelFlag }

            # 1) ждём игру (до 10 минут)
            $waited = 0
            while (-not (Test-PckitProcessRunning $game.Exe)) {
                if (& $cancelled) { Out-Msg @{ t = 'result'; cancelled = $true }; return }
                if ($waited -ge 600) { throw ('{0} так и не запустилась за 10 минут' -f $game.Name) }
                Out-Msg @{ t = 'progress'; phase = 'wait'; i = $waited; n = 600; text = ('Жду запуска {0}' -f $game.Name) }
                Start-Sleep -Seconds 1; $waited++
            }
            # 2) пять секунд, чтобы переключиться в игру
            for ($c = 5; $c -ge 1; $c--) {
                if (& $cancelled) { Out-Msg @{ t = 'result'; cancelled = $true }; return }
                Out-Msg @{ t = 'progress'; phase = 'countdown'; i = 5 - $c; n = 5; left = $c; text = 'Переключитесь в игру' }
                Start-Sleep -Seconds 1
            }
            # 3) замер: каждую секунду — живой FPS
            $csv = Join-Path (Get-PckitFpsDir) ('capture-{0}.csv' -f $game.Id)
            $proc = Start-PckitFpsCapture -Exe $pm -ProcessName $game.Exe -Seconds $seconds -OutCsv $csv
            $tail = New-PckitFpsTail -Path $csv
            $all = New-Object 'System.Collections.Generic.List[double]'
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                while (-not $proc.HasExited) {
                    Start-Sleep -Milliseconds 1000
                    if (& $cancelled) {
                        try { $proc.Kill() } catch { }
                        Out-Msg @{ t = 'result'; cancelled = $true }; return
                    }
                    $new = Read-PckitFpsTail -Tail $tail
                    foreach ($x in $new) { $all.Add($x) }
                    $fps = 0
                    if ($new.Count) { $sum = 0.0; foreach ($x in $new) { $sum += $x }; if ($sum -gt 0) { $fps = [math]::Round(1000.0 * $new.Count / $sum) } }
                    $el = [math]::Min($seconds, [int]$sw.Elapsed.TotalSeconds)
                    Out-Msg @{ t = 'progress'; phase = 'run'; i = $el; n = $seconds; fps = $fps; text = 'Идёт замер' }
                    if ($sw.Elapsed.TotalSeconds -gt $seconds + 30) { try { $proc.Kill() } catch { }; break }   # PresentMon завис
                }
                foreach ($x in (Read-PckitFpsTail -Tail $tail)) { $all.Add($x) }
            } finally {
                if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
                Remove-Item -LiteralPath $csv -Force -ErrorAction SilentlyContinue
            }
            if ($all.Count -lt 100) {
                if (-not (Test-PckitProcessRunning $game.Exe)) { throw ('{0} закрылась во время замера' -f $game.Name) }
                throw 'слишком мало кадров — игра была свёрнута или на паузе?'
            }
            $stats = Get-PckitFpsStats -Times $all.ToArray()
            $prev = @(Get-PckitFpsHistory | Where-Object { $_.game -eq $game.Id })[0]
            $rec = Save-PckitFpsResult -Game $game.Id -Seconds $seconds -Stats $stats
            Out-Msg @{ t = 'result'; record = $rec; previous = $prev; gameName = $game.Name }
        }
        default { throw "unknown command: $Cmd" }
    }
} catch {
    Out-Msg @{ t = 'error'; msg = $_.Exception.Message }
    exit 1
}
