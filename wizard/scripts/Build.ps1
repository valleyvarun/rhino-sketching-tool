param([string]$Version = ('1.0.0-dev.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')))
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a semantic version, for example 1.0.0.' }
$wizardRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = Split-Path $wizardRoot
$yakPath = Join-Path $env:ProgramFiles 'Rhino 8/System/Yak.exe'
if (!(Test-Path -LiteralPath $yakPath)) {
    $rhinoInstall = Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install'
    $yakPath = Join-Path $rhinoInstall.Path 'Yak.exe'
}
# Stage the latest plugin. This does not replace a developer's loaded Rhino assembly.
& (Join-Path $repoRoot 'rhino-plugin/scripts/Build.ps1') -Stage
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$buildRoot = Join-Path $wizardRoot ('bin/package-' + [Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $wizardRoot 'bin/payload'
New-Item -ItemType Directory -Force -Path $buildRoot,$payloadRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'rhino-plugin/bin/Validation/RhinoSketchingTool.rhp') -Destination $buildRoot
Copy-Item -LiteralPath (Join-Path $wizardRoot 'Assets/crayon-mark.png') -Destination $buildRoot
@"
name: crayon-rhino
version: $Version
authors:
  - Crayon
description: Sketch, comment and work with agents inside Rhino 8.
icon: crayon-mark.png
"@ | Set-Content -LiteralPath (Join-Path $buildRoot 'manifest.yml') -Encoding UTF8
Push-Location $buildRoot
try { & $yakPath build --platform win; if ($LASTEXITCODE -ne 0) { throw 'Yak package build failed.' } }
finally { Pop-Location }
$package = Get-ChildItem -LiteralPath $buildRoot -Filter '*.yak' | Select-Object -First 1
if (!$package) { throw 'No Yak package was produced.' }
# Only replace the fixed build payload; no recursive deletion or glob-based cleanup.
Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $payloadRoot 'crayon.yak') -Force
$hash = (Get-FileHash -LiteralPath (Join-Path $buildRoot 'RhinoSketchingTool.rhp')).Hash
[IO.File]::WriteAllLines((Join-Path $payloadRoot 'package-info.txt'), @($Version,$hash))
$sdkPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/'
& dotnet build (Join-Path $wizardRoot 'CrayonSetup.csproj') -c Release "-p:RestoreConfigFile=$(Join-Path $repoRoot 'rhino-plugin/NuGet.Config')" "-p:TargetPlatformSdkPath=$sdkPath" '-p:TargetPlatformDisplayName=Windows'
if ($LASTEXITCODE -ne 0) { throw 'Wizard build failed.' }
$dist = Join-Path $wizardRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath (Join-Path $wizardRoot 'bin/Release/net48/CrayonSetup.exe') -Destination $dist -Force
$exe = Join-Path $dist 'CrayonSetup.exe'
[IO.File]::WriteAllText((Join-Path $dist 'CrayonSetup.sha256'), (Get-FileHash -LiteralPath $exe).Hash + '  CrayonSetup.exe' + [Environment]::NewLine)
Write-Output "Website download ready: $exe"
