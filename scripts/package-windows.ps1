#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')] [string] $Platform = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $sourceRoot 'dist' }
$archivePath = Join-Path $output "Mango-$Platform.zip"
$staging = [IO.Directory]::CreateTempSubdirectory('mango-package-').FullName
$console = $null
$archiveDraft = Join-Path $output ('.mango-' + [guid]::NewGuid().ToString('N') + '.zip')

Push-Location $sourceRoot
try {
    $versionArgs = @(if ($Version) { "-p:Version=$Version" })
    $platformArgs = @(if ($Platform -ne 'win-x64') { '-p:MangoLinux=true' })
    $commonArgs = @('-p:RuntimeFrameworkVersion=10.0.15', '-p:ContinuousIntegrationBuild=true', '-p:DebugType=None', '--nologo') + $versionArgs
    & $Dotnet publish src/Mango/Mango.csproj -c Release -r $Platform --self-contained true --output $staging @commonArgs @platformArgs
    if ($LASTEXITCODE -ne 0) { throw "Mango publish failed with exit code $LASTEXITCODE." }
    $nativeFiles = if ($Platform -eq 'win-x64') { @('Mango.exe', 'coreclr.dll') } else { @('Mango', 'libcoreclr.so') }
    foreach ($required in ($nativeFiles + 'wwwroot/index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw "Publish is missing $required." }
    }
    if ($Platform -eq 'win-x64') {
        $console = [IO.Directory]::CreateTempSubdirectory('mango-console-').FullName
        & $Dotnet publish src/Mango/Mango.csproj -c Release -r $Platform --self-contained true --output $console @commonArgs -p:MangoConsole=true
        if ($LASTEXITCODE -ne 0) { throw "Mango console publish failed with exit code $LASTEXITCODE." }
        [IO.File]::Copy((Join-Path $console 'Mango.exe'), (Join-Path $staging 'Mango.Cli.exe'))
        $installGuide = 'docs/windows-install.md'
    } else {
        $installGuide = 'docs/linux-install.md'
        [IO.File]::Copy((Join-Path $sourceRoot 'scripts/run-linux-managed.sh'), (Join-Path $staging 'run-linux-managed.sh'))
    }
    [IO.File]::Copy((Join-Path $sourceRoot $installGuide), (Join-Path $staging 'Install.md'))
    [IO.File]::Copy((Join-Path $sourceRoot 'docs/admin-cli.md'), (Join-Path $staging 'Admin.md'))
    [IO.File]::WriteAllText((Join-Path $staging 'Mango.package'), $Platform)
    [IO.Directory]::CreateDirectory($output) | Out-Null

    $files = [IO.Directory]::GetFiles($staging, '*', [IO.SearchOption]::AllDirectories)
    [Array]::Sort($files, [StringComparer]::Ordinal)
    $timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $file = [IO.File]::Open($archiveDraft, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($path in $files) {
                $name = [IO.Path]::GetRelativePath($staging, $path).Replace('\', '/')
                # UseStaticFiles serves the packaged UI directly.
                if ($name -eq 'Mango.staticwebassets.endpoints.json') { continue }
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $entry.ExternalAttributes = 0
                $source = [IO.File]::OpenRead($path)
                try {
                    $destination = $entry.Open()
                    try { $source.CopyTo($destination) }
                    finally { $destination.Dispose() }
                }
                finally { $source.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }
    [IO.File]::Move($archiveDraft, $archivePath, $true)
    Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Select-Object Path, Hash
}
finally {
    Pop-Location
    if ([IO.File]::Exists($archiveDraft)) { [IO.File]::Delete($archiveDraft) }
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ([IO.Path]::GetDirectoryName($staging) -eq $tempRoot -and [IO.Path]::GetFileName($staging) -like 'mango-package-*') {
        [IO.Directory]::Delete($staging, $true)
    }
    if ($console -and [IO.Path]::GetDirectoryName($console) -eq $tempRoot -and [IO.Path]::GetFileName($console) -like 'mango-console-*') {
        [IO.Directory]::Delete($console, $true)
    }
}
