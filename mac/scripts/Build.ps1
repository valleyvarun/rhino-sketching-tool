# Builds mac/dist/CrayonSetup.pkg. On macOS use PowerShell 7, Rhino 8 and a .NET SDK.
# On Windows, the system bsdtar writes the same flat product archive and script permissions.
param(
    [string]$Version,
    [string]$YakPath,
    [string]$InstallerIdentity,
    [string]$NotaryProfile
)
$ErrorActionPreference = 'Stop'
$macRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = Split-Path $macRoot
if (!$Version) { $Version = [IO.File]::ReadAllText((Join-Path $repoRoot 'release.version')).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a semantic version such as 0.2.0.' }
$displayVersion = 'v' + ($Version -replace '^(\d+\.\d+)\.0$', '$1')
$onWindows = $env:OS -eq 'Windows_NT'
if (!$onWindows -and !(Test-Path '/usr/bin/productbuild')) { throw 'Build the installer on Windows or macOS.' }
if ($onWindows -and ($InstallerIdentity -or $NotaryProfile)) { throw 'Installer signing and notarization require macOS.' }
if ($NotaryProfile -and !$InstallerIdentity) { throw 'A Developer ID Installer identity is required for notarization.' }

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Write-PlainText([string]$Path, [string]$Contents) {
    [IO.File]::WriteAllText($Path, $Contents.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}

if (!$YakPath) {
    if ($onWindows) {
        $YakPath = Join-Path $env:ProgramFiles 'Rhino 8/System/Yak.exe'
        if (!(Test-Path -LiteralPath $YakPath)) {
            $registryInstall = Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install' -ErrorAction SilentlyContinue
            if ($registryInstall.Path) { $YakPath = Join-Path $registryInstall.Path 'Yak.exe' }
        }
    } else {
        $YakPath = '/Applications/Rhino 8.app/Contents/Resources/bin/yak'
    }
}
if (!(Test-Path -LiteralPath $YakPath -PathType Leaf)) { throw 'Supply the path to Rhino 8 Yak with -YakPath.' }
$YakPath = (Resolve-Path -LiteralPath $YakPath).Path

# Build the plug-in and the Mac distribution of its Rhino package.
$project = Join-Path $macRoot 'plugin/RhinoSketchingTool.Mac.csproj'
$config = Join-Path $repoRoot 'rhino-plugin/NuGet.Config'
Invoke-Checked dotnet @('build', $project, '-c', 'Release', '-nologo', "-p:Version=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:RestoreConfigFile=$config")
$plugin = Join-Path $macRoot 'plugin/bin/Release/RhinoSketchingTool.rhp'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($plugin).ProductVersion -ne $Version) { throw 'Plug-in version differs from the requested package version.' }
$work = Join-Path $macRoot ('bin/product-' + [Guid]::NewGuid().ToString('N'))
$yakRoot = Join-Path $work 'yak'
$scripts = Join-Path $work 'scripts'
$product = Join-Path $work 'product'
$component = Join-Path $product 'Crayon.pkg'
foreach ($path in $yakRoot, $scripts, $component) { New-Item -ItemType Directory -Force -Path $path | Out-Null }

Copy-Item -LiteralPath $plugin -Destination $yakRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'wizard/Assets/crayon-mark.png') -Destination $yakRoot
Write-PlainText (Join-Path $yakRoot 'manifest.yml') (@(
    'name: crayon-rhino', "version: $Version", 'authors:', '  - Crayon',
    'description: Sketch, comment and work with agents inside Rhino 8.', 'icon: crayon-mark.png', ''
) -join "`n")
Push-Location $yakRoot
try { Invoke-Checked $YakPath @('build', '--platform', 'mac') }
finally { Pop-Location }
$yak = Get-Item -LiteralPath (Join-Path $yakRoot "crayon-rhino-$Version-rh8_0-mac.yak")
$pluginHash = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($yak.FullName)
try {
    $manifest = $archive.GetEntry('manifest.yml')
    if (!$manifest) { throw 'Missing Yak manifest.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $manifestText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifestText -notmatch ('(?m)^version:\s*' + [Regex]::Escape($Version) + '\s*$')) { throw 'Yak manifest version differs from the plug-in.' }
    $entry = $archive.GetEntry('RhinoSketchingTool.rhp')
    if (!$entry) { throw 'The Yak package does not contain RhinoSketchingTool.rhp.' }
    $stream = $entry.Open()
    try {
        if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $pluginHash) { throw 'Packaged plug-in differs from the build.' }
    } finally { $stream.Dispose() }
} finally { $archive.Dispose() }

# Scripts in a component travel together. Apple Installer invokes postinstall.
Copy-Item -LiteralPath $yak.FullName -Destination $scripts
$engine = [IO.File]::ReadAllText((Join-Path $macRoot 'installer/engine.sh'))
$values = @{
    '@VERSION@' = $Version
    '@PACKAGE_FILE@' = $yak.Name
    '@PACKAGE_SHA256@' = (Get-FileHash -LiteralPath $yak.FullName -Algorithm SHA256).Hash
    '@PLUGIN_SHA256@' = $pluginHash
}
foreach ($token in $values.Keys) {
    $assignment = '="' + $token + '"'
    if ([regex]::Matches($engine, [regex]::Escape($assignment)).Count -ne 1) { throw "Expected a single $token assignment in engine.sh." }
    $engine = $engine.Replace($assignment, '="' + $values[$token] + '"')
}
Write-PlainText (Join-Path $scripts 'engine.sh') $engine
Write-PlainText (Join-Path $scripts 'postinstall') ([IO.File]::ReadAllText((Join-Path $macRoot 'installer/postinstall')))
$definition = [IO.File]::ReadAllText((Join-Path $macRoot 'package/Distribution.xml')).Replace('@VERSION@', $Version).Replace('@DISPLAY_VERSION@', $displayVersion)
$identifier = 'com.crayon.rhino8.setup'
$resources = Join-Path $macRoot 'package/Resources'
$built = Join-Path $work 'CrayonSetup.pkg'

if ($onWindows) {
    $tar = Join-Path $env:SystemRoot 'System32/tar.exe'
    # bsdtar reads this mtree as an archive, setting Unix modes independently of NTFS.
    # The cpio odc stream is the Scripts file produced by Apple's pkgbuild --nopayload.
    $mtree = Join-Path $work 'scripts.mtree'
    Write-PlainText $mtree (@(
        '#mtree', '/set uid=0 gid=0', '. type=dir mode=0755',
        './postinstall type=file mode=0755 content=postinstall',
        './engine.sh type=file mode=0644 content=engine.sh',
        "./$($yak.Name) type=file mode=0644 content=$($yak.Name)", ''
    ) -join "`n")
    Push-Location $scripts
    try { Invoke-Checked $tar @('--format', 'odc', '-czf', (Join-Path $component 'Scripts'), "@$mtree") }
    finally { Pop-Location }

    $info = [IO.File]::ReadAllText((Join-Path $macRoot 'package/PackageInfo.xml')).Replace('@VERSION@', $Version)
    Write-PlainText (Join-Path $component 'PackageInfo') $info
    Write-PlainText (Join-Path $product 'Distribution') $definition
    Copy-Item -LiteralPath $resources -Destination $product -Recurse
    $productTree = Join-Path $macRoot 'package/Product.mtree'
    Push-Location $product
    try { Invoke-Checked $tar @('--format', 'xar', '--options', 'xar:compression=none', '-cf', $built, "@$productTree") }
    finally { Pop-Location }
} else {
    $componentFile = Join-Path $work 'Crayon.pkg'
    Invoke-Checked /bin/chmod @('0755', $scripts, (Join-Path $scripts 'postinstall'))
    Invoke-Checked /bin/chmod @('0644', (Join-Path $scripts 'engine.sh'), (Join-Path $scripts $yak.Name))
    Invoke-Checked /usr/bin/pkgbuild @('--identifier', $identifier, '--version', $Version, '--nopayload', '--scripts', $scripts, $componentFile)
    # productbuild takes a package filename and rewrites it as an internal #URL.
    $distribution = Join-Path $work 'Distribution.xml'
    Write-PlainText $distribution $definition.Replace('>#Crayon.pkg<', '>Crayon.pkg<')
    $arguments = @('--distribution', $distribution, '--resources', $resources, '--package-path', $work)
    if ($InstallerIdentity) { $arguments += @('--sign', $InstallerIdentity, '--timestamp') }
    Invoke-Checked /usr/bin/productbuild ($arguments + $built)
    if ($NotaryProfile) {
        Invoke-Checked /usr/bin/xcrun @('notarytool', 'submit', $built, '--keychain-profile', $NotaryProfile, '--wait')
        Invoke-Checked /usr/bin/xcrun @('stapler', 'staple', $built)
        Invoke-Checked /usr/bin/xcrun @('stapler', 'validate', $built)
    }
}

# Reopen the actual product and its inner Scripts archive, including the Unix launch mode.
$tar = if ($onWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { '/usr/bin/tar' }
$verified = Join-Path $work 'verified'
$unpacked = Join-Path $verified 'unpacked'
New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
Invoke-Checked $tar @('-xf', $built, '-C', $verified)
$distXML = [xml][IO.File]::ReadAllText((Join-Path $verified 'Distribution'))
$ref = $distXML.SelectSingleNode("//pkg-ref[@id='$identifier' and text()='#Crayon.pkg']")
if (!$ref -or $ref.version -ne $Version -or $distXML.DocumentElement.product.version -ne $Version) {
    throw 'Distribution and embedded component versions do not match the requested release.'
}
$meta = [xml][IO.File]::ReadAllText((Join-Path $verified 'Crayon.pkg/PackageInfo'))
if ($meta.DocumentElement.identifier -ne $identifier -or $meta.DocumentElement.version -ne $Version -or
    !$meta.SelectSingleNode("/pkg-info/scripts/postinstall[@file='./postinstall' or @file='postinstall']")) {
    throw 'The embedded component is missing its version, identity or postinstall script.'
}
$scriptArchive = Join-Path $verified 'Crayon.pkg/Scripts'
$entries = Invoke-Checked $tar @('-tvf', $scriptArchive)
if (!($entries -match '^-rwx[r-]-[x-][r-]-[x-].* (\./)?postinstall$')) { throw 'postinstall lost its execute permission.' }
Invoke-Checked $tar @('-xf', $scriptArchive, '-C', $unpacked)
foreach ($name in 'postinstall', 'engine.sh', $yak.Name) {
    if ((Get-FileHash -LiteralPath (Join-Path $scripts $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $unpacked $name)).Hash) {
        throw "$name changed during packaging."
    }
}
$distDir = Join-Path $macRoot 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$final = Join-Path $distDir 'CrayonSetup.pkg'
Copy-Item -LiteralPath $built -Destination $final -Force
$digest = (Get-FileHash -LiteralPath $final).Hash
Write-PlainText (Join-Path $distDir 'CrayonSetup.sha256') "$digest  CrayonSetup.pkg`n"
Write-Output "Mac installer: $final"
Write-Output "SHA-256: $digest"
