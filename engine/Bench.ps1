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

# забрать новые результаты из console.log и сохранить в историю замеров. Возвращает новые записи.
function Import-PckitCs2Bench {
    $log = Get-PckitCs2ConsoleLog
    if (-not $log -or -not (Test-Path -LiteralPath $log)) { return @() }
    $stateFile = Get-PckitBenchStateFile
    $pos = 0L
    try { if (Test-Path -LiteralPath $stateFile) { $pos = [int64]((Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json).pos) } } catch { }

    $text = ''
    $fs = $null
    try {
        # игра держит файл открытым — читаем, не мешая ей писать
        $fs = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        if ($fs.Length -lt $pos) { $pos = 0 }   # CS2 начала лог заново
        [void]$fs.Seek($pos, [IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] ([int]($fs.Length - $pos))
        $n = $fs.Read($buf, 0, $buf.Length)
        $text = [Text.Encoding]::UTF8.GetString($buf, 0, $n)
        # недописанную последнюю строку оставляем на следующий раз
        $cut = $text.LastIndexOf("`n")
        if ($cut -lt 0) { $text = ''; $n = 0 } else { $n = [Text.Encoding]::UTF8.GetByteCount($text.Substring(0, $cut + 1)); $text = $text.Substring(0, $cut + 1) }
        $pos += $n
    } finally { if ($fs) { $fs.Dispose() } }

    $new = New-Object System.Collections.ArrayList
    foreach ($r in @(ConvertFrom-PckitBenchText -Text $text)) {
        [void]$new.Add((Save-PckitFpsRecord -Record ([pscustomobject]@{
                        game = 'cs2'; source = 'bench'; at = (Get-Date).ToString('s'); avg = $r.Avg; low1 = $r.P1
                    })))
        Start-Sleep -Milliseconds 20   # разные имена файлов, если результатов несколько
    }
    [IO.File]::WriteAllText($stateFile, ([pscustomobject]@{ pos = $pos } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
    return $new.ToArray()
}
