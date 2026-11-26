#Requires -Version 5.1
# тест инструментов: ремонт и удаление не запускаются (только проверка, что список и DNS читаются), ничего не меняется
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:PckitEngineDir = Join-Path $root 'engine'
foreach ($f in Get-ChildItem -LiteralPath $script:PckitEngineDir -Filter *.ps1) { . $f.FullName }

$pass = 0; $fail = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { $script:pass++; Write-Host ('PASS  ' + $Name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ('FAIL  ' + $Name) -ForegroundColor Red }
}

# службы: «по требованию» выполнено и при выключенной службе
Check 'manual = manual' (Test-PckitScModeEqual -Current 'demand' -Target 'demand')
Check 'disabled устраивает manual' (Test-PckitScModeEqual -Current 'disabled' -Target 'demand')
Check 'manual не устраивает disabled' (-not (Test-PckitScModeEqual -Current 'demand' -Target 'disabled'))
Check 'auto не устраивает manual' (-not (Test-PckitScModeEqual -Current 'auto' -Target 'demand'))

# встроенные приложения: только из списка Nolagi, у каждого понятное имя и уровень
$apps = @(Get-PckitBloatApps)
Check 'список приложений читается' ($null -ne $apps)
Check 'у приложений есть имя и уровень' (@($apps | Where-Object { -not $_.Name -or $_.Level -notin 'safe', 'caution' }).Count -eq 0)
$r = Remove-PckitBloatApp -Id 'APPX|Microsoft.WindowsStore'
Check 'Магазин удалить нельзя (не в списке)' (-not $r.Ok)
$names = @($script:PckitBloatApps | ForEach-Object { $_[0] })
Check 'Xbox и Магазин не в списке' (@($names | Where-Object { $_ -match 'Xbox|GamingApp|WindowsStore|StorePurchase' }).Count -eq 0)

# DNS: адаптеры и замер (только чтение)
$ad = @(Get-PckitDnsAdapters)
Check 'адаптеры с интернетом найдены' ($ad.Count -ge 1)
$ms = Measure-PckitDnsServer -Server '1.1.1.1'
Check 'Cloudflare отвечает (нужен интернет)' ($null -ne $ms -and $ms -ge 0)
Check 'несуществующий DNS = нет ответа' ($null -eq (Measure-PckitDnsServer -Server '192.0.2.1'))

# Resizable BAR: на ПК без NVIDIA — $null, иначе есть размер
$rb = Get-PckitResizableBar
Check 'ReBar: результат корректный' ($null -eq $rb -or $rb.Bar1Mb -gt 0)

Write-Host ('Итого: PASS {0}, FAIL {1}' -f $pass, $fail)
if ($fail) { exit 1 }
