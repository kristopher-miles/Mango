#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $sourceRoot 'dist' }
$archivePath = Join-Path $output 'Mango-win-x64.zip'
$staging = [IO.Directory]::CreateTempSubdirectory('mango-package-').FullName
$archiveDraft = Join-Path $output ('.mango-' + [guid]::NewGuid().ToString('N') + '.zip')

Push-Location $sourceRoot
try {
    $versionArgs = @(if ($Version) { "-p:Version=$Version" })
    & $Dotnet publish src/Mango/Mango.csproj -c Release -r win-x64 --self-contained true --output $staging `
        -p:RuntimeFrameworkVersion=10.0.15 -p:ContinuousIntegrationBuild=true -p:DebugType=None --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Mango publish failed with exit code $LASTEXITCODE." }
    foreach ($required in @('Mango.exe', 'coreclr.dll', 'wwwroot/index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw "Publish is missing $required." }
    }
    [IO.File]::Copy((Join-Path $sourceRoot 'docs/windows-install.md'), (Join-Path $staging 'Install.md'))
    [IO.File]::WriteAllText((Join-Path $staging 'Mango.package'), 'win-x64')
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
}
