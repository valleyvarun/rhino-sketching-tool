param([switch]$Stage, [string]$Version)
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $pluginRoot 'RhinoSketchingTool.csproj'
$arguments = @('build', $project, '-c', 'Release', "-p:RestoreConfigFile=$(Join-Path $pluginRoot 'NuGet.Config')")
if ($PSBoundParameters.ContainsKey('Version')) { $arguments += "-p:Version=$Version" }
# Avoid probing inaccessible per-user Windows SDK registrations in restricted shells.
$windowsSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
if (Test-Path -LiteralPath $windowsSdk) {
    $arguments += "-p:TargetPlatformSdkPath=$windowsSdk/"
    $arguments += '-p:TargetPlatformDisplayName=Windows'
}
$arguments += '-p:OutputPath=bin/Validation/'
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed. If Rhino has the plugin loaded, close Rhino or use -Stage.' }
if (!$Stage) { & (Join-Path $PSScriptRoot 'Update-Plugin.ps1') }
