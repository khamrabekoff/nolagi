#Requires -Version 5.1
# Builds dist\pckit.exe: C# (csc from .NET Framework 4.x, already in Windows) + embedded engine payload.
param([switch]$Dev)
$ErrorActionPreference = 'Stop'
$app = $PSScriptRoot
$repo = Split-Path -Parent $app
$obj = Join-Path $app 'obj'
$dist = Join-Path $repo 'dist'
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

# payload: engine\, data\, recipes\, app\bridge.ps1 (same layout as the repo)
$stage = Join-Path $obj 'payload'
New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
foreach ($d in 'engine', 'data', 'recipes') { Copy-Item (Join-Path $repo $d) (Join-Path $stage $d) -Recurse }
Copy-Item (Join-Path $app 'bridge.ps1') (Join-Path $stage 'app\bridge.ps1')
$zip = Join-Path $obj 'payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

# server key lives outside the repo (never committed)
$keyFile = Join-Path $obj 'appkey.txt'
$secrets = Join-Path (Split-Path -Parent $repo) 'server\SECRETS.txt'
$key = ''
if (Test-Path $secrets) { $key = ((Get-Content $secrets -Encoding UTF8 | Where-Object { $_ -match 'X-Pckit-Key' }) -replace '^.*:\s*', '').Trim() }
if (-not $key) { Write-Warning 'server key not found: statistics will be off in this build' }
[IO.File]::WriteAllText($keyFile, $key)

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$refs = @(
    (Join-Path $wpf 'PresentationFramework.dll'), (Join-Path $wpf 'PresentationCore.dll'), (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $fw 'System.Xaml.dll'), (Join-Path $fw 'System.Web.Extensions.dll'),
    (Join-Path $fw 'System.IO.Compression.dll'), (Join-Path $fw 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $fw 'System.Core.dll'), (Join-Path $fw 'System.dll'), (Join-Path $fw 'System.Xml.dll'),
    (Join-Path $fw 'System.Windows.Forms.dll'), (Join-Path $fw 'System.Drawing.dll')   # значок в трее
)
$exe = Join-Path $dist 'Nolagi.exe'
$manifest = Join-Path $app 'app.manifest'
if ($Dev) { $exe = Join-Path $app 'obj-dev\nolagi-dev.exe'; New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null; $manifest = Join-Path $app 'obj-dev\dev.manifest'; (Get-Content (Join-Path $app 'app.manifest') -Raw).Replace('requireAdministrator','asInvoker') | Set-Content $manifest -Encoding UTF8 }
$args = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$exe",
    "/win32manifest:$manifest",
    "/resource:$zip,payload.zip", "/resource:$(Join-Path $app 'src\Theme.xaml'),Theme.xaml", "/resource:$keyFile,appkey.txt")
$icon = Join-Path $app 'nolagi.ico'
if (Test-Path $icon) { $args += "/win32icon:$icon" }
$args += ($refs | ForEach-Object { "/r:$_" })
$args += (Get-ChildItem (Join-Path $app 'src') -Filter *.cs | ForEach-Object { $_.FullName })
& (Join-Path $fw 'csc.exe') @args
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host ("built {0} ({1:N0} KB)" -f $exe, ((Get-Item $exe).Length / 1KB))
