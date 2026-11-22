# Draws the interface that sits over the viewport (toolbar, option row, colour picker,
# tooltip, tick, icons) twice with the plug-in's own painting code, and compares the two:
#   Wpf  the plug-in as it is on Windows, painting with WPF
#   Eto  the plug-in as it is compiled for a Mac, painting with Eto (rhino-plugin/Mac/MacDrawing.cs)
# Both run here on Windows and outside Rhino, so the Mac's painting can be checked without
# a Mac. Rhino is not started; RhinoCommon and Eto are loaded from the installed Rhino 8.
#
#   .\mac\tests\Drawing\run.ps1
#
# Pictures go to bin/out/wpf and bin/out/eto, and bin/out/compare has each pair side by
# side with their difference in red. The toolbar and the icons are shapes only and have to
# come out the same; pictures with text differ, since the Mac build sets text in the
# system's font, and so does the colour picker's square, whose fade to black is exact on a
# Mac (see Colors.Transparent in MacDrawing.cs). Look at those rather than count them.
param([string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 8/System'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $root '../../..'))
if (!(Test-Path -LiteralPath (Join-Path $RhinoSystem 'netcore/RhinoCommon.dll'))) { throw "Rhino 8 was not found at $RhinoSystem. Pass -RhinoSystem with its System folder." }
foreach ($kind in 'Wpf', 'Eto') {
    & dotnet build (Join-Path $root "$kind/Drawing$kind.csproj") -c Release -nologo -v:q "-p:RhinoSystem=$RhinoSystem" "-p:RestoreConfigFile=$(Join-Path $repoRoot 'rhino-plugin/NuGet.Config')"
    if ($LASTEXITCODE -ne 0) { throw "The $kind drawing test did not build." }
}
$env:RHINO_SYSTEM = $RhinoSystem
$wpf = Join-Path $root 'Wpf/bin/Release/net8.0-windows/DrawingWpf.exe'
$eto = Join-Path $root 'Eto/bin/Release/net8.0-windows/DrawingEto.exe'
$out = Join-Path $root 'bin/out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

& $wpf (Join-Path $out 'wpf');               if ($LASTEXITCODE -ne 0) { throw 'Drawing with WPF failed.' }
& $eto (Join-Path $out 'eto');               if ($LASTEXITCODE -ne 0) { throw 'Drawing with Eto failed.' }
& $eto (Join-Path $out 'eto-pixels') pixels; if ($LASTEXITCODE -ne 0) { throw 'Drawing with Eto, pixel by pixel, failed.' }

Write-Output ''
Write-Output 'Windows painting (first) against Mac painting (second):'
& $wpf --compare (Join-Path $out 'wpf') (Join-Path $out 'eto') (Join-Path $out 'compare') --alike 'toolbar,icons'
$shapes = $LASTEXITCODE
Write-Output ''
Write-Output "The Mac painting's two ways of handing a picture to Rhino (as a PNG, and pixel by pixel):"
& $wpf --compare (Join-Path $out 'eto') (Join-Path $out 'eto-pixels') --alike '*'
$ways = $LASTEXITCODE
Write-Output ''
if ($shapes -ne 0 -or $ways -ne 0) { throw 'Pictures that should be the same are not. See the lines marked above, and bin/out/compare.' }
Write-Output "The toolbar and icons match, and both ways give the same pictures. Side by side: $(Join-Path $out 'compare')"
