#Requires -Version 5.1
# тест отчёта о проблеме: только чтение, ничего не меняется и никуда не отправляется
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:PckitEngineDir = Join-Path $root 'engine'
foreach ($f in Get-ChildItem -LiteralPath $script:PckitEngineDir -Filter *.ps1) { . $f.FullName }

$pass = 0; $fail = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { $script:pass++; Write-Host ('PASS  ' + $Name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ('FAIL  ' + $Name) -ForegroundColor Red }
}

# приватность: имя пользователя, имя ПК и MAC вырезаются, но не куски других слов
$saved = @($env:USERNAME, $env:USERPROFILE, $env:COMPUTERNAME)
try {
    $env:USERNAME = 'user'; $env:USERPROFILE = 'C:\Users\user'; $env:COMPUTERNAME = 'DESKTOP-ABC123'
    $t = Hide-PckitDiagPrivate -Text 'C:\Users\user\AppData\x.exe на DESKTOP-ABC123, user вошёл, служба wuauserv, MAC 00-1A-2B-3C-4D-5E'
    Check 'профиль заменён' ($t -like '*%USERPROFILE%\AppData\x.exe*')
    Check 'имя ПК заменено' ($t -notmatch 'DESKTOP-ABC123' -and $t -like '*<pc>*')
    Check 'имя пользователя целым словом' ($t -like '*<user> вошёл*')
    Check 'wuauserv не испорчен' ($t -like '*wuauserv*')
    Check 'MAC заменён' ($t -like '*<mac>*' -and $t -notmatch '4D-5E')
} finally { $env:USERNAME = $saved[0]; $env:USERPROFILE = $saved[1]; $env:COMPUTERNAME = $saved[2] }
Check 'длинный текст обрезан' ((Limit-PckitDiagText -Text ('x' * 500) -Max 100).Length -le 101)

# подсказки: рискованные, журнал, DPC, подкачка
$tw = [pscustomobject]@{ active = @([pscustomobject]@{ id = 'risk.a'; risky = $true }, [pscustomobject]@{ id = 'safe.b'; risky = $false }); partial = @(); journal = @([pscustomobject]@{ id = 'risk.a' }); journalTotal = 3 }
$dpc = [pscustomobject]@{ ok = $true; verdict = 'bad'; dpcAvg = 2; intAvg = 2; worstCore = '0'; worstValue = 60 }
$sys = [pscustomobject]@{ pagefileMb = 0; ramFreeGb = 8; sysDiskFreeGb = 100; known = @('ProcessLasso') }
$ev = @([pscustomobject]@{ key = 'gpu.tdr'; title = 'TDR'; count = 2; last = '2026-11-27 20:00' }, [pscustomobject]@{ key = 'appcrash'; title = 'Вылеты'; count = 5; last = '' })
$h = @(Get-PckitDiagHints -Tweaks $tw -Events $ev -Dpc $dpc -System $sys)
Check 'риск в подсказках' (@($h | Where-Object { $_ -like '*свой риск*: 1*risk.a*' }).Count -eq 1)
Check 'журнал в подсказках' (@($h | Where-Object { $_ -like '*журнале отката 3*' }).Count -eq 1)
Check 'TDR в подсказках' (@($h | Where-Object { $_ -like 'TDR: 2*' }).Count -eq 1)
Check 'вылеты программ не в подсказках' (@($h | Where-Object { $_ -like 'Вылеты*' }).Count -eq 0)
Check 'DPC в подсказках' (@($h | Where-Object { $_ -like '*задержки драйверов*' }).Count -eq 1)
Check 'подкачка в подсказках' (@($h | Where-Object { $_ -eq 'Файл подкачки выключен' }).Count -eq 1)
Check 'оптимизаторы в подсказках' (@($h | Where-Object { $_ -like '*ProcessLasso*' }).Count -eq 1)
Check 'пустой отчёт без ошибок' ((@(Get-PckitDiagHints -Tweaks $null -Events @() -Dpc $null -System $null)).Count -eq 0)

# живые части (только чтение)
$e = @(Get-PckitDiagEvents -Days 1)
Check 'журнал Windows читается' ($null -ne $e)
$s = Get-PckitDiagSystem
Check 'система читается' ($null -ne $s -and $s.PSObject.Properties['hags'])
$d = Get-PckitDiagDpc -Seconds 2
Check 'DPC замер' ($d.ok -eq $false -or ($d.seconds -ge 1 -and $d.verdict -in 'ok', 'warn', 'bad'))
$tws = Get-PckitDiagTweaks -Actions @(Get-PckitActions -Directory (Join-Path $root 'data'))
Check 'настройки читаются' ($null -ne $tws -and $null -ne $tws.journalTotal)
$json = $tws | ConvertTo-Json -Depth 6 -Compress
Check 'отчёт сериализуется' ($json.Length -gt 10)

# BIOS: только чтение, подсказки всегда есть
$bi = Get-PckitBiosInfo
Check 'BIOS: подсказки есть' ($bi.xmp.Length -gt 10 -and $bi.rebar.Length -gt 10 -and $bi.key)
Check 'BIOS: Gigabyte узнаётся' (@($script:PckitBiosHints | Where-Object { 'Gigabyte Technology Co., Ltd.' -match $_.Match }).Count -eq 1)
Check 'BIOS: MSI узнаётся' (@($script:PckitBiosHints | Where-Object { 'Micro-Star International Co., Ltd.' -match $_.Match }).Count -eq 1)

Write-Host ('{0} passed, {1} failed' -f $pass, $fail)
if ($fail) { exit 1 }
