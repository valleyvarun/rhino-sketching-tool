# Installs the current build into Rhino 8 the way the Crayon setup wizard does: as a Rhino
# package (Yak), so it is the Crayon that Rhino loads. Close Rhino first.
#   .\scripts\Install-Package.ps1
param([string]$Version = ('1.0.0-dev.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')))
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = Split-Path $pluginRoot
if (Get-Process Rhino -ErrorAction SilentlyContinue) { throw 'Save your work and close Rhino, then run this again.' }
$yak = Join-Path $env:ProgramFiles 'Rhino 8/System/Yak.exe'
if (!(Test-Path -LiteralPath $yak)) { $yak = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install').Path 'Yak.exe' }

& (Join-Path $PSScriptRoot 'Build.ps1') -Stage
$work = Join-Path $pluginRoot ('bin/package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginRoot 'bin/Validation/RhinoSketchingTool.rhp') -Destination $work
Copy-Item -LiteralPath (Join-Path $repoRoot 'wizard/Assets/crayon-mark.png') -Destination $work
@"
name: crayon-rhino
version: $Version
authors:
  - Crayon
description: Sketch, comment and work with agents inside Rhino 8.
icon: crayon-mark.png
"@ | Set-Content -LiteralPath (Join-Path $work 'manifest.yml') -Encoding UTF8
Push-Location $work
try { & $yak build --platform win; if ($LASTEXITCODE -ne 0) { throw 'Yak package build failed.' } }
finally { Pop-Location }
$package = Get-ChildItem -LiteralPath $work -Filter '*.yak' | Select-Object -First 1
if (!$package) { throw 'No Yak package was produced.' }
& $yak install $package.FullName
if ($LASTEXITCODE -ne 0) { throw 'Yak install failed.' }
$installed = Join-Path $env:APPDATA "McNeel/Rhinoceros/packages/8.0/crayon-rhino/$Version/RhinoSketchingTool.rhp"
if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $work 'RhinoSketchingTool.rhp')).Hash) { throw 'Installed plugin verification failed.' }
Write-Output "Crayon $Version installed: $installed"
