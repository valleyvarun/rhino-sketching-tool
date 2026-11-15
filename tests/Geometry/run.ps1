param(
    [string]$RhinoCommonPath = "$env:USERPROFILE\.nuget\packages\rhinocommon\8.0.23304.9001\lib\net48\RhinoCommon.dll"
)
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$sourcePath = Join-Path $testRoot '..\..\rhino-plugin\Drawing\ShapeRecognition.cs'
$testOutput = Join-Path $testRoot 'bin'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $RhinoCommonPath)) { throw "Pass -RhinoCommonPath with the path to RhinoCommon.dll." }
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
$executable = Join-Path $testOutput 'GeometryChecks.exe'
& $compilerPath /nologo /platform:x64 "/out:$executable" "/reference:$RhinoCommonPath" $sourcePath (Join-Path $testRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Geometry check compilation failed.' }
Copy-Item -LiteralPath $RhinoCommonPath -Destination (Join-Path $testOutput 'RhinoCommon.dll') -Force
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Geometry checks failed.' }
