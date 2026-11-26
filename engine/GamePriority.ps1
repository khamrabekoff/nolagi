#Requires -Version 5.1
# pckit engine | высокий приоритет процессора для любой игры (как в Process Lasso, но без программы в фоне)
# Механизм тот же, что у настроек CS2 и Dota 2: Image File Execution Options\<exe>\PerfOptions, CpuPriorityClass = 3 (High).
# Windows применяет его сама при каждом запуске exe с таким именем. Всё пишется в журнал отката.

$script:PckitIfeoRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'

# системные и служебные программы: им приоритет не меняем никогда
$script:PckitPrioDeny = '^(explorer|csrss|smss|wininit|winlogon|lsass|services|svchost|dwm|audiodg|conhost|taskmgr|system|registry|fontdrvhost|sihost|ctfmon|searchhost|startmenuexperiencehost|runtimebroker|spoolsv|msmpeng|nissrv|securityhealthservice|powershell|pwsh|cmd|nolagi)\.exe$'
# exe в папке игры, которые точно не сама игра
$script:PckitPrioSkipExe = '(?i)(unins|setup|install|crash|report|redist|vcredist|vc_redist|dxsetup|directx|dotnet|easyanticheat|eac_|beservice|battleye|helper|launcherpatcher|updater|update|uploader|cefprocess|webhelper|steamerrorreporter|ue4prereq|prereq|ffmpeg|benchmark|config|settings|editor)'

function Test-PckitPrioExeName {
    # имя exe годится для приоритета: только «имя.exe», без путей, не системное
    param([string]$Exe)
    if (-not $Exe -or $Exe -notmatch '^[^\\/:*?"<>|]+\.exe$') { return $false }
    if ($Exe -match $script:PckitPrioDeny) { return $false }
    return $true
}

function Get-PckitSteamLibraries {
    $roots = New-Object System.Collections.ArrayList
    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    if ($steam) { [void]$roots.Add(($steam -replace '/', '\').TrimEnd('\')) }
    foreach ($r in @($roots)) {
        $lib = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $lib)) { continue }
        try {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($lib), '"path"\s+"([^"]+)"')) {
                $p = ($m.Groups[1].Value -replace '\\\\', '\').TrimEnd('\')
                if (-not ($roots | Where-Object { $_ -ieq $p })) { [void]$roots.Add($p) }
            }
        } catch { }
    }
    return @($roots | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'steamapps') })
}

function ConvertTo-PckitLetters {
    param([string]$Text)
    return (($Text.ToLowerInvariant()) -replace '[^a-z0-9]', '')
}

function Get-PckitGameMainExe {
    # главный exe игры (до 4 уровней вглубь): похожий на название игры, «Shipping» у Unreal, из папки win64;
    # инструменты, установщики и античит-лаунчеры отбрасываются; при равенстве — самый крупный
    param([string]$Dir, [string]$Name = '')
    $nameKey = ConvertTo-PckitLetters $Name
    $dirKey = ConvertTo-PckitLetters (Split-Path -Leaf $Dir)
    $files = @()
    try {
        $files = @(Get-ChildItem -LiteralPath $Dir -Filter *.exe -File -Recurse -Depth 4 -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notmatch $script:PckitPrioSkipExe -and $_.Name -notmatch '(?i)vconsole|console|tool|server|dedicated|hammer|workshop|crashpad|qtwebengine|vrcompositor|overlay|sdk' -and $_.Length -gt 256KB })
    } catch { }
    if (-not $files.Count) { return $null }
    $scored = foreach ($f in $files) {
        $base = ConvertTo-PckitLetters ([IO.Path]::GetFileNameWithoutExtension($f.Name) -replace '(?i)-win64-shipping$', '')
        $score = 0
        if ($base -and $nameKey -and ($nameKey.Contains($base) -or $base.Contains($nameKey))) { $score += 100 }
        if ($base -and $dirKey -and ($dirKey.Contains($base) -or $base.Contains($dirKey))) { $score += 60 }
        # короткие имена Source 2 / id Tech: cs2.exe внутри «Counter-Strike Global Offensive», dota2.exe внутри «dota 2 beta»
        if ($base.Length -ge 3 -and $nameKey.StartsWith($base.Substring(0, [Math]::Min(3, $base.Length)))) { $score += 15 }
        if ($f.Name -match '(?i)shipping') { $score += 50 }
        if ($f.FullName -match '(?i)\\(win64|x64|bin64)\\') { $score += 20 }
        [pscustomobject]@{ File = $f; Score = $score }
    }
    $best = $scored | Sort-Object @{ Expression = 'Score'; Descending = $true }, @{ Expression = { $_.File.Length }; Descending = $true } | Select-Object -First 1
    return $best.File.FullName
}

function Get-PckitSteamGames {
    # установленные игры Steam: { Name, Exe (имя файла), Path }
    $list = New-Object System.Collections.ArrayList
    foreach ($lib in Get-PckitSteamLibraries) {
        foreach ($acf in @(Get-ChildItem -LiteralPath (Join-Path $lib 'steamapps') -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue)) {
            try {
                $t = [IO.File]::ReadAllText($acf.FullName)
                $name = [regex]::Match($t, '"name"\s+"([^"]+)"').Groups[1].Value
                $dir = [regex]::Match($t, '"installdir"\s+"([^"]+)"').Groups[1].Value
                if (-not $name -or -not $dir) { continue }
                if ($name -match '(?i)redistributable|proton|steamvr|steam linux runtime|dedicated server|\bsdk\b') { continue }
                $full = Join-Path $lib ('steamapps\common\' + $dir)
                if (-not (Test-Path -LiteralPath $full)) { continue }
                $exe = Get-PckitGameMainExe -Dir $full -Name $name
                if (-not $exe) { continue }
                $leaf = Split-Path -Leaf $exe
                if (-not (Test-PckitPrioExeName -Exe $leaf)) { continue }
                [void]$list.Add([pscustomobject]@{ Name = $name; Exe = $leaf; Path = $exe })
            } catch { }
        }
    }
    return @($list | Sort-Object Name)
}

function Get-PckitPriorityExes {
    # exe, которым сейчас задан высокий приоритет через PerfOptions
    $list = New-Object System.Collections.ArrayList
    $keys = @()
    try { $keys = @(Get-ChildItem -LiteralPath $script:PckitIfeoRoot -ErrorAction Stop) } catch { return @() }
    foreach ($k in $keys) {
        $perf = Join-Path $k.PSPath 'PerfOptions'
        try {
            $v = (Get-ItemProperty -LiteralPath $perf -Name CpuPriorityClass -ErrorAction Stop).CpuPriorityClass
            if ([int]$v -eq 3) { [void]$list.Add([string]$k.PSChildName) }
        } catch { }
    }
    return @($list | Sort-Object)
}

function Set-PckitGamePriority {
    param([string]$Exe, [bool]$High, $Journal)
    if (-not (Test-PckitPrioExeName -Exe $Exe)) { return [pscustomobject]@{ Ok = $false; Message = 'этой программе приоритет менять нельзя' } }
    $path = '{0}\{1}\PerfOptions' -f $script:PckitIfeoRoot, $Exe
    try {
        if ($High) {
            Add-PckitRegTracked -Journal $Journal -Path $path -Name 'CpuPriorityClass' -Value 3 -Type 'DWord'
        } else {
            if (Test-PckitRegValue -Path $path -Name 'CpuPriorityClass') { Remove-PckitRegTracked -Journal $Journal -Path $path -Name 'CpuPriorityClass' }
        }
        return [pscustomobject]@{ Ok = $true; Message = '' }
    } catch {
        return [pscustomobject]@{ Ok = $false; Message = $_.Exception.Message }
    }
}
