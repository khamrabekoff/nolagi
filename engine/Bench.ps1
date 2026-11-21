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
    $r = Read-PckitBenchLog -Log $log -Pos 0 -Sig ''
    Save-PckitBenchState -Pos $r.Pos -Sig $r.Sig
}

# прочитать лог с позиции Pos до последнего перевода строки. Позиция — в байтах, без перекодирования.
function Read-PckitBenchLog {
    param([string]$Log, [int64]$Pos, [string]$Sig)
    $fs = $null
    try {
        # игра держит файл открытым — читаем, не мешая ей писать
        $fs = [IO.File]::Open($Log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        if ($fs.Length -lt $Pos -or ($Pos -gt 0 -and $Sig -and (Get-PckitBenchSig -Fs $fs -Pos $Pos) -ne $Sig)) { $Pos = 0 }
        $text = ''
        if ($fs.Length -gt $Pos) {
            [void]$fs.Seek($Pos, [IO.SeekOrigin]::Begin)
            $buf = New-Object byte[] ([int]($fs.Length - $Pos))
            $n = $fs.Read($buf, 0, $buf.Length)
            # недописанную последнюю строку оставляем на следующий раз
            $cut = if ($n -gt 0) { [array]::LastIndexOf($buf, [byte]10, $n - 1) } else { -1 }
            if ($cut -ge 0) {
                $text = [Text.Encoding]::UTF8.GetString($buf, 0, $cut + 1)
                $Pos += $cut + 1
            }
        }
        return [pscustomobject]@{ Text = $text; Pos = $Pos; Sig = (Get-PckitBenchSig -Fs $fs -Pos $Pos) }
    } finally { if ($fs) { $fs.Dispose() } }
}

# забрать новые результаты из console.log и сохранить в историю замеров. Возвращает новые записи.
# Ошибка чтения (файл занят игрой) пробрасывается — окно скажет «закройте CS2».
function Import-PckitCs2Bench {
    $log = Get-PckitCs2ConsoleLog
    if (-not $log -or -not (Test-Path -LiteralPath $log)) { return @() }
    # одновременно может работать несколько команд моста — импорт по очереди, иначе дубли
    $mutex = New-Object System.Threading.Mutex($false, 'Global\NolagiBenchImport')
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
        foreach ($f in $found) {
            [void]$new.Add((Save-PckitFpsRecord -Record ([pscustomobject]@{
                            game = 'cs2'; source = 'bench'; at = (Get-Date).ToString('s'); avg = $f.Avg; low1 = $f.P1
                        })))
            Start-Sleep -Milliseconds 20   # разные имена файлов, если результатов несколько
        }
        Save-PckitBenchState -Pos $r.Pos -Sig $r.Sig
        return $new.ToArray()
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
