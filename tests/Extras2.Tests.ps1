#Requires -Version 5.1
# pckit | тесты: MSI для сетевой карты (pcinet), старое меню правой кнопки (значение «по умолчанию»), SMART-диски.
# Запуск: powershell -NoProfile -File tests\Extras2.Tests.ps1. HKLM не пишет; реестр — только тестовая ветка HKCU, в конце удаляется.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'engine\Common.ps1')
. (Join-Path $root 'engine\Journal.ps1')
. (Join-Path $root 'engine\Actions.ps1')
. (Join-Path $root 'engine\Applier.ps1')
. (Join-Path $root 'engine\Health.ps1')

$tmp = Join-Path $env:TEMP ('pckit-extras2-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$script:PckitStateRoot = $tmp
$testRoot = 'HKCU:\Software\PckitExtras2Test_' + [guid]::NewGuid().ToString('N').Substring(0, 6)

$script:Pass = 0; $script:Fail = 0
function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Info = '')
    if ($Condition) { $script:Pass++; Write-Host ('PASS  {0}' -f $Name) -ForegroundColor Green }
    else { $script:Fail++; Write-Host ('FAIL  {0}  {1}' -f $Name, $Info) -ForegroundColor Red }
}

try {
    $actions = @(Get-PckitActions -Directory (Join-Path $root 'data'))

    # ---------- pcinet / net.msi.on ----------
    $paths = @(Get-PckitDevRegTargets -Step ([pscustomobject]@{ target = 'pcinet'; sub = 'Interrupt Management\MessageSignaledInterruptProperties' }))
    $badP = @($paths | Where-Object { -not $_ -or [string]$_ -notlike 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI\*\Interrupt Management\MessageSignaledInterruptProperties' })
    Assert-That 'pcinet: массив путей (может быть пустым), пути из Enum\PCI' ($badP.Count -eq 0) ($badP -join '; ')
    $msi = @($actions | Where-Object { $_.id -eq 'net.msi.on' })
    Assert-That 'net.msi.on: есть, группа «Сеть», expert, medium, admin' ($msi.Count -eq 1 -and $msi[0].group -eq 'Сеть' -and $msi[0].expert -and $msi[0].risk -eq 'medium' -and $msi[0].needs -eq 'admin')
    $st = Test-PckitActionState -Action $msi[0]
    Assert-That 'net.msi.on: проверка состояния не падает' ($st.Status -in 'applied', 'notApplied', 'partial', 'notfit') $st.Status
    $dry = Invoke-PckitAction -Action $msi[0]
    Assert-That 'net.msi.on: сухой прогон не падает' ($dry.Failed -eq 0)

    # ---------- explorer.classicmenu ----------
    $cm = @($actions | Where-Object { $_.id -eq 'explorer.classicmenu' })
    Assert-That 'classicmenu: есть, Удобство, low, none, minBuild 22000' ($cm.Count -eq 1 -and $cm[0].group -eq 'Удобство' -and $cm[0].risk -eq 'low' -and $cm[0].needs -eq 'none' -and [int]$cm[0].minBuild -eq 22000)
    Assert-That 'classicmenu: настоящий CLSID, значение по умолчанию' ($cm[0].steps[0].path -eq 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32' -and $cm[0].steps[0].name -eq '' -and $cm[0].steps[0].value -eq '')

    # копия действия на тестовую ветку
    $keyPath = $testRoot + '\CLSID\{test}\InprocServer32'
    $copy = $cm[0] | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $copy.steps[0].path = $keyPath
    $copy.verify[0].path = $keyPath
    $copy.minBuild = 1
    New-Item -Path $testRoot -Force | Out-Null

    Assert-That 'classicmenu: до применения — не применено' ((Test-PckitActionState -Action $copy).Status -eq 'notApplied')
    $null = Invoke-PckitActions -Actions @($copy) -Run
    $names = @((Get-Item -LiteralPath $keyPath -ErrorAction SilentlyContinue).GetValueNames())
    Assert-That 'classicmenu: применено — есть пустое значение по умолчанию' ($names -contains '' -and (Get-Item -LiteralPath $keyPath).GetValue('') -eq '')
    Assert-That 'classicmenu: проверка видит «применено»' ((Test-PckitActionState -Action $copy).Status -eq 'applied')
    $r = Restore-PckitJournal -Journal (Read-PckitJournal) -Run
    Assert-That 'classicmenu: откат без ошибок' ($r.Failed -eq 0) ([string]$r.Failed)
    Assert-That 'classicmenu: откат убрал значение и созданные ключи' (-not (Test-Path -LiteralPath $keyPath) -and -not (Test-Path -LiteralPath ($testRoot + '\CLSID\{test}')))
    Assert-That 'classicmenu: проверка видит «не применено»' ((Test-PckitActionState -Action $copy).Status -eq 'notApplied')

    # если ключ уже был (с чужим значением), откат возвращает его и ключ не удаляет
    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -LiteralPath $keyPath -Name '(default)' -Value 'old'
    Set-ItemProperty -LiteralPath $keyPath -Name 'Other' -Value 5
    $null = Invoke-PckitActions -Actions @($copy) -Run
    Assert-That 'classicmenu: поверх чужого значения — пусто' ((Get-Item -LiteralPath $keyPath).GetValue('') -eq '')
    $r = Restore-PckitJournal -Journal (Read-PckitJournal) -Run
    Assert-That 'classicmenu: откат вернул прежнее и не тронул соседей' ($r.Failed -eq 0 -and (Get-Item -LiteralPath $keyPath).GetValue('') -eq 'old' -and (Get-Item -LiteralPath $keyPath).GetValue('Other') -eq 5)

    # ---------- SMART-диски ----------
    $fails = 0
    $data = $null
    try { $data = Get-PckitDiskHealthData } catch { $fails++ }
    Assert-That 'SMART: сбор данных не падает' ($fails -eq 0 -and $null -ne $data)
    $real = @(Get-PckitDiskHealthFindings -Data $data)
    $fields = 'Severity', 'Title', 'Detail', 'FixAction', 'Hint', 'Link'
    $badF = @($real | Where-Object { $f = $_; @($fields | Where-Object { $f.PSObject.Properties.Name -notcontains $_ }).Count -gt 0 -or -not $f.Title })
    Assert-That 'SMART: находки на этом ПК имеют все поля' ($badF.Count -eq 0)
    Assert-That 'SMART: пустые данные -> ничего' (@(Get-PckitDiskHealthFindings -Data $null).Count -eq 0 -and @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @(); PredictFailure = $false })).Count -eq 0)

    function New-TestDisk { param($Name = 'Test', $Health = 'Healthy', $Media = 'SSD', $Wear = $null, $Temp = $null) [pscustomobject]@{ Name = $Name; Health = $Health; Operational = 'OK'; MediaType = $Media; SizeGb = 500; Wear = $Wear; Temp = $Temp; ReadErrors = $null } }
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk), (New-TestDisk -Name 'B' -Media 'HDD')); PredictFailure = $false }))
    Assert-That 'SMART: всё хорошо -> одна ok «Диски исправны»' ($f.Count -eq 1 -and $f[0].Severity -eq 'ok' -and $f[0].Title -match 'Диски исправны' -and $f[0].Detail -match '2')
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk -Name 'Samsung X' -Health 'Unhealthy')); PredictFailure = $false }))
    Assert-That 'SMART: Unhealthy -> bad с названием диска' ($f.Count -eq 1 -and $f[0].Severity -eq 'bad' -and $f[0].Title -match 'Samsung X' -and $f[0].Title -match 'отказать')
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk)); PredictFailure = $true }))
    Assert-That 'SMART: PredictFailure -> bad' (@($f | Where-Object { $_.Severity -eq 'bad' }).Count -eq 1)
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk -Wear 85)); PredictFailure = $false }))
    Assert-That 'SMART: износ 85% -> warn с процентом' ($f.Count -eq 1 -and $f[0].Severity -eq 'warn' -and $f[0].Title -match '85%')
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk -Health 'Warning')); PredictFailure = $false }))
    Assert-That 'SMART: Warning -> warn' ($f.Count -eq 1 -and $f[0].Severity -eq 'warn')
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk -Temp 65)); PredictFailure = $false }))
    Assert-That 'SMART: 65 °C -> warn' ($f.Count -eq 1 -and $f[0].Severity -eq 'warn' -and $f[0].Title -match '65')
    $f = @(Get-PckitDiskHealthFindings -Data ([pscustomobject]@{ Disks = @((New-TestDisk -Wear 10 -Temp 40)); PredictFailure = $false }))
    Assert-That 'SMART: износ 10%, 40 °C -> ok' ($f.Count -eq 1 -and $f[0].Severity -eq 'ok')
} catch {
    $script:Fail++
    Write-Host ('FAIL  необработанное исключение: {0} (строка {1})' -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) -ForegroundColor Red
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ('Итого: PASS {0}, FAIL {1}' -f $script:Pass, $script:Fail)
if ($script:Fail -gt 0) { exit 1 }
exit 0
