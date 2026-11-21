#Requires -Version 5.1
# pckit engine | bench: результат карты «CS2 FPS Benchmark» из консоли CS2.
# С параметром запуска -condebug CS2 пишет консоль в game\csgo\console.log; после пролёта карта печатает
# строку вида «FPS: Avg=268.5, P1=148.2». Читаем только новые строки — файл игры не меняем.

function Get-PckitCs2ConsoleLog {
    $root = Find-PckitCs2
    if (-not $root) { return $null }
    return (Join-Path $root 'game\csgo\console.log')
}

# включён ли -condebug в параметрах запуска CS2: $true / $false / $null (не определить)
function Test-PckitCs2Condebug {
    try {
        $lo = Get-PckitCs2LaunchOptions
        if (-not $lo -or -not $lo.Found) { return $null }
        return ((' ' + $lo.Value + ' ') -like '* -condebug *')
    } catch { return $null }
}

# все результаты бенчмарка в тексте консоли: [{ Avg; P1 }]
function ConvertFrom-PckitBenchText {
    param([string]$Text)
    $rows = New-Object System.Collections.ArrayList
    if (-not $Text) { return @() }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $re = '(?im)FPS\s*:?\s*Avg\s*[=:]\s*(?<avg>\d+(?:[.,]\d+)?)\s*,?\s*(?:P1|1%\s*low|1%)\s*[=:]\s*(?<p1>\d+(?:[.,]\d+)?)'
    foreach ($m in [regex]::Matches($Text, $re)) {
        $avg = [double]::Parse($m.Groups['avg'].Value.Replace(',', '.'), $inv)
        $p1 = [double]::Parse($m.Groups['p1'].Value.Replace(',', '.'), $inv)
        if ($avg -gt 0) { [void]$rows.Add([pscustomobject]@{ Avg = $avg; P1 = $p1 }) }
    }
    return $rows.ToArray()
}

function Get-PckitBenchStateFile { return (Join-Path (Get-PckitFpsDir) 'bench-state.json') }

# «подпись» лога: хэш последних 256 байт перед сохранённой позицией. Если там уже другие байты —
# CS2 начала лог заново (даже если новый лог длиннее старой позиции), читаем с начала.
function Get-PckitBenchSig {
    param([IO.FileStream]$Fs, [int64]$Pos)
    if ($Pos -le 0 -or $Fs.Length -lt $Pos) { return '' }
    $len = [int][math]::Min(256, $Pos)
    $b = New-Object byte[] $len
    [void]$Fs.Seek($Pos - $len, [IO.SeekOrigin]::Begin)
    $got = $Fs.Read($b, 0, $len)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($b, 0, $got)).Replace('-', '') } finally { $sha.Dispose() }
}

function Save-PckitBenchState {
    param([int64]$Pos, [string]$Sig)
    [IO.File]::WriteAllText((Get-PckitBenchStateFile), ([pscustomobject]@{ pos = $Pos; sig = $Sig } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
}

# запомнить текущий конец лога: всё, что уже в нём, — старое и в историю не попадёт
function Reset-PckitCs2BenchState {
    $log = Get-PckitCs2ConsoleLog
    if (-not $log -or -not (Test-Path -LiteralPath $log)) { Save-PckitBenchState -Pos 0 -Sig ''; return }
    try {
        $r = Read-PckitBenchLog -Log $log -Pos 0 -Sig ''
        Save-PckitBenchState -Pos $r.Pos -Sig $r.Sig
    } catch {
        # лог не прочитать (занят антивирусом и т. п.) — запомнить хотя бы его длину, иначе старый прогон станет «новым»
        Save-PckitBenchState -Pos ((Get-Item -LiteralPath $log).Length) -Sig ''
    }
}

# прочитать лог с позиции Pos до последнего перевода строки. Позиция — в байтах, без перекодирования.
# Читаем кусками по 8 МБ и оставляем только строки с «Avg»: лог с -condebug может вырасти до сотен МБ,
# а результат бенчмарка может оказаться где угодно (после пролёта игрок мог ещё сыграть матчи).
function Read-PckitBenchLog {
    param([string]$Log, [int64]$Pos, [string]$Sig, [int]$ChunkSize = 8MB)
    $fs = $null
    try {
        # игра держит файл открытым — читаем, не мешая ей писать
        $fs = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        if ($fs.Length -lt $Pos -or ($Pos -gt 0 -and $Sig -and (Get-PckitBenchSig -Fs $fs -Pos $Pos) -ne $Sig)) { $Pos = 0 }
        $keep = New-Object System.Text.StringBuilder
        [void]$fs.Seek($Pos, [IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] $ChunkSize
        $carry = New-Object byte[] 0
        while (($n = $fs.Read($buf, 0, $ChunkSize)) -gt 0) {
            $data = New-Object byte[] ($carry.Length + $n)
            [Array]::Copy($carry, 0, $data, 0, $carry.Length)
            [Array]::Copy($buf, 0, $data, $carry.Length, $n)
            $cut = [array]::LastIndexOf($data, [byte]10)
            if ($cut -lt 0) { $carry = $data; continue }   # недописанную строку оставляем на следующий раз
            foreach ($line in ([Text.Encoding]::UTF8.GetString($data, 0, $cut + 1)).Split("`n")) {
                if ($line -match 'Avg') { [void]$keep.Append($line.TrimEnd("`r")).Append("`n") }
            }
            $Pos += $cut + 1
            $rest = $data.Length - $cut - 1
            $carry = New-Object byte[] $rest
            [Array]::Copy($data, $cut + 1, $carry, 0, $rest)
        }
        return [pscustomobject]@{ Text = $keep.ToString(); Pos = $Pos; Sig = (Get-PckitBenchSig -Fs $fs -Pos $Pos) }
    } finally { if ($fs) { $fs.Dispose() } }
}

# забрать новые результаты из console.log и сохранить в историю замеров. Возвращает новые записи.
# Ошибка чтения пробрасывается — окно покажет её текст.
function Import-PckitCs2Bench {
    $log = Get-PckitCs2ConsoleLog
    if (-not $log -or -not (Test-Path -LiteralPath $log)) { return @() }
    # одновременно может работать несколько команд моста — импорт по очереди, иначе дубли.
    # Local\: все мосты в одной сессии, а Global\ без прав администратора создать нельзя
    $mutex = New-Object System.Threading.Mutex($false, 'Local\NolagiBenchImport')
    $locked = $false
    try {
        try { $locked = $mutex.WaitOne(10000) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { return @() }
        $stateFile = Get-PckitBenchStateFile
        $state = $null
        try { if (Test-Path -LiteralPath $stateFile) { $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } } catch { }

        $r = Read-PckitBenchLog -Log $log -Pos $(if ($state) { [int64]$state.pos } else { 0 }) -Sig $(if ($state) { [string]$state.sig } else { '' })
        $found = @(ConvertFrom-PckitBenchText -Text $r.Text)
        # первый раз (состояния ещё нет): в логе могут лежать прогоны за прошлые дни — берём только последний
        if (-not $state -and $found.Count -gt 1) { $found = @($found[$found.Count - 1]) }

        $new = New-Object System.Collections.ArrayList
        $saveError = $null
        foreach ($f in $found) {
            # ошибка записи одного результата не должна оставить позицию на месте — иначе при повторе дубли
            try {
                [void]$new.Add((Save-PckitFpsRecord -Record ([pscustomobject]@{
                                game = 'cs2'; source = 'bench'; at = (Get-Date).ToString('s'); avg = $f.Avg; low1 = $f.P1
                            })))
            } catch { $saveError = $_.Exception.Message }
            Start-Sleep -Milliseconds 20   # разные имена файлов, если результатов несколько
        }
        Save-PckitBenchState -Pos $r.Pos -Sig $r.Sig
        if ($saveError -and -not $new.Count) { throw ('не удалось сохранить результат: {0}' -f $saveError) }
        return $new.ToArray()
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

# что показать после импорта: самый свежий результат и «прошлый» — замер до этого импорта,
# а не второй из только что забранных
function Get-PckitBenchLatest {
    param([int]$NewCount)
    $hist = @(Get-PckitFpsHistory -Max ($NewCount + 1) -Game 'cs2' -Source 'bench')
    $latest = $null; $prev = $null
    if ($NewCount -gt 0 -and $hist.Count) {
        $latest = $hist[0]
        if ($hist.Count -gt $NewCount) { $prev = $hist[$NewCount] }
    }
    return [pscustomobject]@{ Latest = $latest; Previous = $prev }
}
# история замеров: %LOCALAPPDATA%\pckit\fps\fps-<игра>-<время>.json
function Get-PckitFpsDir {
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = Join-Path $env:USERPROFILE 'AppData\Local' }
    $dir = Join-Path $base 'pckit\fps'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    return $dir
}

function Save-PckitFpsRecord {
    param($Record)
    $file = Join-Path (Get-PckitFpsDir) ('fps-{0}-{1}.json' -f $Record.game, (Get-Date).ToString('yyyyMMdd-HHmmss-fff'))
    [IO.File]::WriteAllText($file, ($Record | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
    return $Record
}

# последние замеры, новые сверху
function Get-PckitFpsHistory {
    # Game/Source фильтруют ДО ограничения Max: иначе прошлый замер другой игры вытесняет нужный
    param([int]$Max = 20, [string]$Game = '', [string]$Source = '')
    $rows = New-Object System.Collections.ArrayList
    $files = @(Get-ChildItem -LiteralPath (Get-PckitFpsDir) -Filter 'fps-*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    foreach ($f in $files) {
        if ($rows.Count -ge $Max) { break }
        try {
            $r = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not ($r.game -and $r.avg)) { continue }   # старые замеры pckit («до»/«после») без поля game пропускаем
            if ($Game -and [string]$r.game -ne $Game) { continue }
            if ($Source -and [string]$r.source -ne $Source) { continue }
            [void]$rows.Add($r)
        } catch { }
    }
    return $rows.ToArray()
}
