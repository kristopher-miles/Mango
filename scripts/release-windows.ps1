#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Patch', 'Minor', 'Major')] [string] $Bump = 'Patch',
    [switch] $Cutoff,
    [string] $Dotnet = 'dotnet',
    [string] $Notes = 'Mango for Windows x64. See Install.md in the ZIP.',
    [string] $PublisherKey = (Join-Path $env:LOCALAPPDATA 'Mango.Release/publisher-key.dpapi'),
    [switch] $BuildOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repository = 'kristopher-miles/Mango'
$endpoint = "https://api.github.com/repos/$repository/releases"

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $commit = git rev-parse HEAD
    $refs = git ls-remote --refs "https://github.com/$repository.git" 'refs/heads/main' 'refs/tags/v*'
    $previous = $refs | ForEach-Object {
        if ($_ -match '\srefs/tags/v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
            [version]::new([int] $Matches[1], [int] $Matches[2], [int] $Matches[3])
        }
    } | Sort-Object -Descending | Select-Object -First 1
    $previousWindow = 1
    if ($previous) {
        git fetch --no-tags "https://github.com/$repository.git" "refs/tags/v$previous"
        [xml] $previousProject = (git show 'FETCH_HEAD:src/Mango/Mango.csproj') -join "`n"
        $previousWindow = [int] $previousProject.SelectSingleNode('//AssemblyMetadata[@Include="MangoProtocolWindow"]/@Value').Value
    } else { $previous = [version] '0.0.0' }
    $version = switch ($Bump) {
        'Patch' { [version]::new($previous.Major, $previous.Minor, $previous.Build + 1) }
        'Minor' { [version]::new($previous.Major, $previous.Minor + 1, 0) }
        'Major' { [version]::new($previous.Major + 1, 0, 0) }
    }
    if ($version.Major -gt 65534 -or $version.Minor -gt 65534 -or $version.Build -gt 65534) {
        throw 'This version exceeds the Windows assembly version limit.'
    }
    if (-not $BuildOnly -and $refs -notcontains "$commit`trefs/heads/main") {
        throw "Push the reviewed commit to $repository main before releasing it."
    }
    [xml] $project = (git show "${commit}:src/Mango/Mango.csproj") -join "`n"
    $window = [int] $project.SelectSingleNode('//AssemblyMetadata[@Include="MangoProtocolWindow"]/@Value').Value
    if ($window -ne ($previousWindow + [int] $Cutoff.IsPresent)) {
        throw 'A protocol change requires increasing MangoProtocolWindow by one and explicitly passing -Cutoff.'
    }

    $output = Join-Path $PWD "dist/v$version"
    $source = [IO.Directory]::CreateTempSubdirectory('mango-release-').FullName
    try {
        git archive --format=zip --output="$source/source.zip" $commit
        [IO.Compression.ZipFile]::ExtractToDirectory("$source/source.zip", "$source/tree")
        & "$source/tree/scripts/package-windows.ps1" -Dotnet $Dotnet -OutputDirectory $output -Version "$version"
    }
    finally {
        [IO.Directory]::Delete($source, $true)
    }
    $asset = 'Mango-win-x64.zip'
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $asset" | Set-Content -LiteralPath (Join-Path $output "$asset.sha256") -Encoding ascii
    if ($BuildOnly) { return }

    $manifest = @{ version = "$version"; window = $window; cutoff = $Cutoff.IsPresent; platform = 'win-x64';
        bytes = (Get-Item -LiteralPath (Join-Path $output $asset)).Length; sha256 = $hash } | ConvertTo-Json -Compress
    $manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifest)
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $keyBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PublisherKey), $null, 'CurrentUser')
        try { $rsa.ImportPkcs8PrivateKey($keyBytes, [ref] $null) }
        finally { [Array]::Clear($keyBytes) }
        $publicKey = git show "${commit}:scripts/update-publisher.pub"
        if ($rsa.ExportSubjectPublicKeyInfoPem().Trim() -ne ($publicKey -join "`n").Trim()) {
            throw 'The publisher key does not match the public key pinned in the release commit.'
        }
        $signature = $rsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally { $rsa.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $output 'mango-update.json'), $manifestBytes)
    [IO.File]::WriteAllBytes((Join-Path $output 'mango-update.sig'), $signature)

    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    if (-not $token) {
        $credentials = "protocol=https`nhost=github.com`npath=$repository.git`n`n" |
            git -c credential.helper= -c credential.helper=manager credential fill
        $password = $credentials | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
        if (-not $password) { throw 'Set GH_TOKEN or sign in to GitHub through your Git credential helper.' }
        $token = $password.Substring(9)
    }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' }
    $release = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' -TimeoutSec 60 -Body (
        @{ tag_name = "v$version"; target_commitish = $commit; name = "Mango $version"; body = $Notes; draft = $true } | ConvertTo-Json -Compress)
    try {
        foreach ($name in @($asset, "$asset.sha256", 'mango-update.json', 'mango-update.sig')) {
            $upload = $release.upload_url -replace '\{.*$', ''
            $result = Invoke-RestMethod -Method Post -Uri "${upload}?name=$name" -Headers $headers `
                -ContentType 'application/octet-stream' -InFile (Join-Path $output $name) -TimeoutSec 600
            if ($result.state -ne 'uploaded' -or ($name -eq $asset -and $result.digest -ne "sha256:$hash")) {
                throw "GitHub could not verify $name."
            }
        }
        $published = Invoke-RestMethod -Method Patch -Uri "$endpoint/$($release.id)" -Headers $headers `
            -ContentType 'application/json' -Body '{"draft":false}' -TimeoutSec 60
        $published.html_url
    }
    catch {
        throw "Check the GitHub release draft v$version before retrying. $($_.Exception.Message)"
    }
}
finally {
    Pop-Location
}
