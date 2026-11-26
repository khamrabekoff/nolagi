#Requires -Version 5.1
# тест приоритета для любой игры: запись — в HKCU:\Software\pckit-test\IFEO вместо настоящего раздела
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:PckitEngineDir = Join-Path $root 'engine'
. (Join-Path $root 'engine\Common.ps1')
. (Join-Path $root 'engine\Journal.ps1')
. (Join-Path $root 'engine\GamePriority.ps1')
$script:PckitStateRoot = Join-Path $env:TEMP 'pckit-prio-test-state'
New-Item -ItemType Directory -Force -Path $script:PckitStateRoot | Out-Null
$script:PckitIfeoRoot = 'HKCU:\Software\pckit-test\IFEO'
Remove-Item -LiteralPath 'HKCU:\Software\pckit-test' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $script:PckitIfeoRoot -Force | Out-Null

$pass = 0; $fail = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { $script:pass++; Write-Host ('PASS  ' + $Name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ('FAIL  ' + $Name) -ForegroundColor Red }
}

# 1. какие имена допустимы
Check 'игра подходит' (Test-PckitPrioExeName -Exe 'eldenring.exe')
Check 'explorer.exe — нельзя' (-not (Test-PckitPrioExeName -Exe 'explorer.exe'))
Check 'svchost.exe — нельзя' (-not (Test-PckitPrioExeName -Exe 'SVCHOST.EXE'))
Check 'путь вместо имени — нельзя' (-not (Test-PckitPrioExeName -Exe 'C:\Games\x.exe'))
Check 'не exe — нельзя' (-not (Test-PckitPrioExeName -Exe 'game.bat'))
Check 'пусто — нельзя' (-not (Test-PckitPrioExeName -Exe ''))

# 2. включить, увидеть в списке, откатить
$j = New-PckitJournal
$r = Set-PckitGamePriority -Exe 'fakegame.exe' -High $true -Journal $j
Check 'включение без ошибок' ($r.Ok)
$v = (Get-ItemProperty -LiteralPath "$($script:PckitIfeoRoot)\fakegame.exe\PerfOptions").CpuPriorityClass
Check 'CpuPriorityClass = 3' ($v -eq 3)
Check 'игра в списке приоритетов' (@(Get-PckitPriorityExes) -contains 'fakegame.exe')
Check 'системному exe отказ' (-not (Set-PckitGamePriority -Exe 'lsass.exe' -High $true -Journal $j).Ok)
$res = Restore-PckitJournal -Journal $j -Run
Check 'откат без ошибок' ($res.Failed -eq 0)
Check 'после отката значения нет' (-not (Test-PckitRegValue -Path "$($script:PckitIfeoRoot)\fakegame.exe\PerfOptions" -Name 'CpuPriorityClass'))

# 3. убрать уже стоящий приоритет и вернуть откатом
New-Item -Path "$($script:PckitIfeoRoot)\other.exe\PerfOptions" -Force | Out-Null
New-ItemProperty -LiteralPath "$($script:PckitIfeoRoot)\other.exe\PerfOptions" -Name CpuPriorityClass -Value 3 -PropertyType DWord -Force | Out-Null
$j2 = New-PckitJournal
Check 'снятие без ошибок' ((Set-PckitGamePriority -Exe 'other.exe' -High $false -Journal $j2).Ok)
Check 'после снятия не в списке' (@(Get-PckitPriorityExes) -notcontains 'other.exe')
[void](Restore-PckitJournal -Journal $j2 -Run)
Check 'откат вернул приоритет' (@(Get-PckitPriorityExes) -contains 'other.exe')

# 4. игры Steam (только чтение; на ПК без Steam список пустой)
$games = @(Get-PckitSteamGames)
Check 'список игр читается' ($null -ne $games)
Check 'у игр есть имя и exe' (@($games | Where-Object { -not $_.Name -or $_.Exe -notmatch '\.exe$' }).Count -eq 0)
foreach ($g in $games) { Write-Host ('      {0,-40} {1}' -f $g.Name, $g.Exe) -ForegroundColor DarkGray }

Remove-Item -LiteralPath 'HKCU:\Software\pckit-test' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $script:PckitStateRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ('Итого: PASS {0}, FAIL {1}' -f $pass, $fail)
if ($fail) { exit 1 }
