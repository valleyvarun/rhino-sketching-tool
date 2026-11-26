# Installs the current build into Rhino 8 the way the Crayon setup wizard does: as a Rhino
# package (Yak), so it is the Crayon that Rhino loads. Close Rhino first.
#   .\scripts\Install-Package.ps1
param([string]$Version)
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = Split-Path $pluginRoot
if (!$PSBoundParameters.ContainsKey('Version')) {
    $Version = [IO.File]::ReadAllText((Join-Path $repoRoot 'release.version.txt')).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a semantic version in major.minor.patch format.' }
if (Get-Process Rhino -ErrorAction SilentlyContinue) { throw 'Save your work and close Rhino, then run this again.' }
$yak = Join-Path $env:ProgramFiles 'Rhino 8/System/Yak.exe'
if (!(Test-Path -LiteralPath $yak)) { $yak = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install').Path 'Yak.exe' }

& (Join-Path $PSScriptRoot 'Build.ps1') -Stage -Version $Version
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

# Replace whatever Crayon is there, the way the setup wizard does (wizard/Setup/Package.cs).
# Yak makes this version the active one whatever its number; Rhino removes the others when it
# next starts. Only a folder of this same version has to go first, or its old files would stay.
$versionFolder = Join-Path $env:APPDATA "McNeel/Rhinoceros/packages/8.0/crayon-rhino/$Version"
if (Test-Path -LiteralPath $versionFolder) { Remove-Item -LiteralPath $versionFolder -Recurse -Force }
& $yak install $package.FullName
if ($LASTEXITCODE -ne 0) { throw 'Yak install failed.' }
$installed = Join-Path $versionFolder 'RhinoSketchingTool.rhp'
if (!(Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $work 'RhinoSketchingTool.rhp')).Hash) { throw 'Installed plugin verification failed.' }

# Make what Rhino has recorded about the plug-in agree with the package: the file it loaded
# last (after a test run that is a build in bin/), that it loads when Rhino starts, and no
# "never load" left from a build that failed.
$rhino = 'HKCU:\Software\McNeel\Rhinoceros\8.0'
$id = '299a696b-aeb1-4c25-bd67-f6c04f5bed05'
$registration = "$rhino\Plug-Ins\$id"
if (Test-Path -LiteralPath $registration) {
    if (!(Test-Path -LiteralPath "$registration\PlugIn")) { New-Item -Path "$registration\PlugIn" | Out-Null }
    Set-ItemProperty -LiteralPath "$registration\PlugIn" -Name FileName -Value ([IO.Path]::GetFullPath($installed))
    Set-ItemProperty -LiteralPath $registration -Name LoadMode -Value 1 -Type DWord
}
$options = "$rhino\Global Options\Plug-ins\$id"
if ((Test-Path -LiteralPath $options) -and (Get-Item -LiteralPath $options).GetValue('LoadProtection') -eq '2') {
    Remove-ItemProperty -LiteralPath $options -Name LoadProtection
}
Write-Output "Crayon $Version installed: $installed"
