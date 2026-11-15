param([switch]$WaitForUnlock)
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $pluginRoot 'bin/Validation/RhinoSketchingTool.rhp'
$target = Join-Path $pluginRoot 'bin/Release/RhinoSketchingTool.rhp'
$status = Join-Path $pluginRoot 'bin/update-status.txt'
if (!(Test-Path -LiteralPath $source)) { throw 'Build the staged plugin first.' }
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
do {
    try {
        # Opening exclusively tests Rhino's assembly lock without disturbing its session.
        $stream = [IO.File]::Open($target, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch [IO.IOException] {
        if (!$WaitForUnlock) {
            Set-Content -LiteralPath $status -Value 'Waiting for Rhino to close.'
            $worker = Join-Path $PSScriptRoot 'Update-Plugin.ps1'
            Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$worker`" -WaitForUnlock"
            Write-Output 'Update queued; it will install automatically when Rhino closes.'
            exit 0
        }
        Start-Sleep -Seconds 2
        continue
    }
    try {
        $bytes = [IO.File]::ReadAllBytes($source)
        if ($stream.Length -gt 0) {
            $backup = [IO.File]::Create($target + '.previous')
            try { $stream.CopyTo($backup) } finally { $backup.Dispose() }
        }
        $stream.Position = 0
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.SetLength($bytes.Length)
        $stream.Flush()
    } finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Installed plugin verification failed.' }
    Set-Content -LiteralPath $status -Value 'Updated plugin installed and verified.'
    Write-Output 'Updated plugin installed and verified.'
    break
} while ($true)
