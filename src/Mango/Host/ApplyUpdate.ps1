$ErrorActionPreference = 'Stop'
$install = $env:MANGO_UPDATE_INSTALL
$stage = $env:MANGO_UPDATE_STAGE
$ready = $env:MANGO_UPDATE_READY
$receipt = $env:MANGO_UPDATE_RECEIPT
$backup = "$stage.previous"
$failed = "$stage.failed"
$message = "Installed Mango $env:MANGO_UPDATE_VERSION."
$ok = $false
$movedOld = $false
$movedNew = $false
$child = $null
$armed = "$ready.armed"
$go = "$ready.go"
$committed = $false
$restartOld = $false

function Start-Mango([string] $extra) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = [IO.Path]::Combine($install, 'Mango.exe')
    $start.WorkingDirectory = $install
    $start.Arguments = "$env:MANGO_UPDATE_ARGS $extra"
    $start.UseShellExecute = $false
    return [Diagnostics.Process]::Start($start)
}

try {
    [IO.File]::WriteAllText($armed, '')
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while (-not [IO.File]::Exists($go) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not [IO.File]::Exists($go)) { return }
    $committed = $true
    # Leave the old host alone if its graceful shutdown stalls.
    $parent = Get-Process -Id ([int] $env:MANGO_UPDATE_PID) -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -ne [long] $env:MANGO_UPDATE_STARTED) { $parent = $null }
    if ($parent) {
        if (-not $parent.WaitForExit(90000)) { throw 'The previous Mango process did not exit.' }
    }
    $marker = [IO.File]::Open([IO.Path]::Combine($install, 'Mango.package'), 'Open', 'ReadWrite', 'None')
    $marker.Dispose()
    [IO.Directory]::Move($install, $backup)
    $movedOld = $true
    [IO.Directory]::Move($stage, $install)
    $movedNew = $true
    $child = Start-Mango "--update-ready $env:MANGO_UPDATE_NONCE"
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not [IO.File]::Exists($ready) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if ($child.HasExited -or -not [IO.File]::Exists($ready) -or [IO.File]::ReadAllText($ready) -ne $env:MANGO_UPDATE_VERSION) {
        throw 'The new Mango process did not report healthy startup.'
    }
    $ok = $true
} catch {
    $message = "Mango $env:MANGO_UPDATE_VERSION update failed: $($_.Exception.Message)"
    try {
        if ($child -and -not $child.HasExited) {
            $child.Kill()
            if (-not $child.WaitForExit(10000)) { throw 'The replacement process is still running.' }
        }
        if ($movedNew) { [IO.Directory]::Move($install, $failed) }
        if ($movedOld) {
            [IO.Directory]::Move($backup, $install)
            $message += ' The previous files were restored.'
        }
        $restartOld = -not $parent -or $parent.HasExited
    } catch {
        $message += " Recovery needs attention: $($_.Exception.Message)"
    }
} finally {
    [IO.File]::Delete($ready)
    [IO.File]::Delete($armed)
    [IO.File]::Delete($go)
    if ($committed) {
        try {
            $result = @{ version = $env:MANGO_UPDATE_VERSION; failed = (-not $ok); message = $message } | ConvertTo-Json -Compress
            $pending = "$receipt.$env:MANGO_UPDATE_NONCE"
            [IO.File]::WriteAllText($pending, $result)
            if ([IO.File]::Exists($receipt)) { [IO.File]::Replace($pending, $receipt, [NullString]::Value) }
            else { [IO.File]::Move($pending, $receipt) }
        } finally {
            if ($restartOld) { $null = Start-Mango '' }
        }
    }
}
