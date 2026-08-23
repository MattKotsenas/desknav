param(
    [string] $CredentialPath =
        "$env:LOCALAPPDATA\pointer-ui-vm\pointer-ui-dev.credential.xml",
    [string] $ArtifactPath,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$VmName = 'pointer-ui-dev'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $ArtifactPath) {
    $packages = @(Get-ChildItem `
        (Join-Path $repositoryRoot 'artifacts\package') `
        -Filter '*.zip' `
        -File)
    if ($packages.Count -ne 1) {
        throw "Expected one runtime zip, found $($packages.Count)."
    }
    $ArtifactPath = $packages[0].FullName
}
if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot `
        'artifacts\acceptance\kanata-runtime.json'
}

$ArtifactPath = [IO.Path]::GetFullPath($ArtifactPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$checksumPath = "$ArtifactPath.sha256"
$measureScript = Join-Path $PSScriptRoot 'Measure-Cursor.ps1'
$guestRoot = 'C:\dogfood\desknav-runtime-acceptance'
$runtimeTask = 'DesknavRuntimeAcceptance'
$measureTask = 'DesknavCursorMeasurement'
$runtimePort = 9999
$checks = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "desknav-runtime-$([Guid]::NewGuid().ToString('N'))"
$session = $null
$keyboard = $null
$initialVmState = $null
$bodyError = $null
$credential = $null
$interactiveUser = $null

$keys = @{
    Caps = [uint32] 0x14
    Space = [uint32] 0x20
    Escape = [uint32] 0x1B
    H = [uint32] 0x48
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-Check([string] $Name, [bool] $Passed, [object] $Evidence) {
    $checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Evidence = $Evidence
    })
}

function Invoke-Key(
    [ValidateSet('PressKey', 'ReleaseKey', 'TypeKey')]
    [string] $Method,
    [uint32] $Code
) {
    $response = Invoke-CimMethod -InputObject $keyboard `
        -MethodName $Method -Arguments @{ KeyCode = $Code }
    if ($response.ReturnValue -ne 0) {
        throw "$Method failed for key $Code with $($response.ReturnValue)."
    }
}

function Type-Key([uint32] $Code) {
    Invoke-Key TypeKey $Code
    Start-Sleep -Milliseconds 80
}

function Test-GuestFile([string] $Path, [int] $Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 100
        $exists = Invoke-Command -Session $session {
            param($Path)
            Test-Path -LiteralPath $Path
        } -ArgumentList $Path
    } while (-not $exists -and (Get-Date) -lt $deadline)
    $exists
}

function Wait-GuestFile([string] $Path, [int] $Seconds = 20) {
    $exists = Test-GuestFile $Path $Seconds
    if (-not $exists) {
        throw "Guest file did not appear: '$Path'."
    }
}

function Wait-InteractiveUser {
    $deadline = (Get-Date).AddMinutes(2)
    do {
        Start-Sleep -Milliseconds 200
        $user = Invoke-Command -Session $session {
            Get-Process explorer -IncludeUserName |
                Select-Object -First 1 -ExpandProperty UserName
        }
    } while (-not $user -and (Get-Date) -lt $deadline)
    if (-not $user) {
        throw 'No interactive Explorer session is available.'
    }
    $user
}

function Set-BaseLayer {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $echo = Invoke-Command -Session $session {
            param($Port)
            $client = [Net.Sockets.TcpClient]::new()
            try {
                $client.Connect('127.0.0.1', $Port)
                $stream = $client.GetStream()
                $stream.ReadTimeout = 2000
                $request = [Text.Encoding]::UTF8.GetBytes(
                    "{`"ChangeLayer`":{`"new`":`"base`"}}`n")
                $stream.Write($request, 0, $request.Length)
                $reader = [IO.StreamReader]::new($stream)
                do {
                    $line = $reader.ReadLine()
                } while ($line -and
                    $line -notlike '*"LayerChange":{"new":"base"}*')
                $line
            }
            catch [Net.Sockets.SocketException] {
                $null
            }
            catch [IO.IOException] {
                $null
            }
            finally {
                $client.Dispose()
            }
        } -ArgumentList $runtimePort
    } while (-not $echo -and (Get-Date) -lt $deadline)
    if ($echo -notlike '*"LayerChange":{"new":"base"}*') {
        throw 'Kanata did not confirm the base layer.'
    }
}

function Wait-Hook {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Set-BaseLayer
        $readyPath = Join-Path $guestRoot `
            "hook-$([Guid]::NewGuid().ToString('N')).ready"
        $job = Invoke-Command -VMName $VmName -Credential $credential -AsJob {
            param($Port, $ReadyPath)
            $client = [Net.Sockets.TcpClient]::new()
            try {
                try {
                    $client.Connect('127.0.0.1', $Port)
                    $stream = $client.GetStream()
                    $stream.ReadTimeout = 10000
                }
                catch [Net.Sockets.SocketException] {
                    return
                }

                Set-Content -LiteralPath $ReadyPath -Value ready -Encoding ascii
                try {
                    $reader = [IO.StreamReader]::new($stream)
                    do {
                        $line = $reader.ReadLine()
                    } while ($line -and
                        $line -notlike '*"LayerChange":{"new":"wm"}*')
                    $line
                }
                catch [IO.IOException] {
                    $null
                }
            }
            finally {
                $client.Dispose()
            }
        } -ArgumentList $runtimePort, $readyPath
        try {
            $observerReady = Test-GuestFile $readyPath 10
            if ($observerReady) {
                Type-Key $keys.Caps
                if (Wait-Job $job -Timeout 12) {
                    $echo = Receive-Job $job
                    if ($echo -like '*"LayerChange":{"new":"wm"}*') {
                        Set-BaseLayer
                        return
                    }
                }
            }
        }
        finally {
            Stop-Job $job -ErrorAction SilentlyContinue
            Remove-Job $job -Force -ErrorAction SilentlyContinue
            Invoke-Command -Session $session {
                param($Path)
                Remove-Item -LiteralPath $Path -ErrorAction SilentlyContinue
            } -ArgumentList $readyPath
        }
    } while ((Get-Date) -lt $deadline)
    throw 'Kanata did not observe hardware input.'
}

function Stop-ReleaseProcesses {
    Invoke-Command -Session $session {
        param($Root)
        Get-CimInstance Win32_Process |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.StartsWith(
                    "$Root\releases\",
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            }
    } -ArgumentList $guestRoot
}

function Start-InteractiveTask(
    [string] $TaskName,
    [string] $Executable,
    [string] $Arguments
) {
    Invoke-Command -Session $session {
        param($TaskName, $Executable, $Arguments, $User)
        $action = New-ScheduledTaskAction -Execute $Executable `
            -Argument $Arguments
        $principal = New-ScheduledTaskPrincipal -UserId $User `
            -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Principal $principal -Force | Out-Null
        Start-ScheduledTask $TaskName
    } -ArgumentList $TaskName, $Executable, $Arguments, $interactiveUser
}

function Stop-Runtime {
    Invoke-Command -Session $session {
        param($TaskName)
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    } -ArgumentList $runtimeTask
    Stop-ReleaseProcesses
}

function Wait-Runtime([string] $ReleasePath, [int] $ExcludedProcessId = 0) {
    $executable = Join-Path $ReleasePath $manifest.kanata.executable
    $configuration = Join-Path $ReleasePath $manifest.configuration
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 200
        $process = Invoke-Command -Session $session {
            param($Executable, $Configuration, $ExcludedProcessId)
            Get-CimInstance Win32_Process |
                Where-Object {
                    $_.ExecutablePath -eq $Executable -and
                    $_.CommandLine -like "*$Configuration*" -and
                    $_.ProcessId -ne $ExcludedProcessId
                } |
                Select-Object -First 1 ProcessId, CommandLine
        } -ArgumentList $executable, $configuration, $ExcludedProcessId
    } while (-not $process -and (Get-Date) -lt $deadline)
    if (-not $process) {
        throw "Kanata did not start from '$ReleasePath'."
    }
    Set-BaseLayer
    Wait-Hook
    $process
}

function Start-Runtime([string] $ReleasePath) {
    Stop-Runtime
    $executable = Join-Path $ReleasePath $manifest.kanata.executable
    $configuration = Join-Path $ReleasePath $manifest.configuration
    Invoke-Command -Session $session {
        param($TaskName)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false `
            -ErrorAction SilentlyContinue
    } -ArgumentList $runtimeTask
    Start-InteractiveTask `
        $runtimeTask `
        $executable `
        "-c `"$configuration`" --port $runtimePort"
    Wait-Runtime $ReleasePath
}

function Restart-Runtime([string] $ReleasePath, [int] $PreviousProcessId) {
    Stop-Runtime
    Invoke-Command -Session $session {
        param($TaskName)
        Start-ScheduledTask $TaskName
    } -ArgumentList $runtimeTask
    Wait-Runtime $ReleasePath $PreviousProcessId
}

function Measure-Left {
    $ready = Join-Path $guestRoot 'cursor.ready'
    $arm = Join-Path $guestRoot 'cursor.arm'
    $armed = Join-Path $guestRoot 'cursor.armed'
    $measure = Join-Path $guestRoot 'cursor.measure'
    $result = Join-Path $guestRoot 'cursor.json'
    $desktopDeadline = (Get-Date).AddMinutes(2)
    do {
        Invoke-Command -Session $session {
            param(
                $TaskName,
                $Ready,
                $Arm,
                $Armed,
                $Measure,
                $Result
            )
            Stop-ScheduledTask -TaskName $TaskName `
                -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false `
                -ErrorAction SilentlyContinue
            Get-CimInstance Win32_Process |
                Where-Object {
                    $_.Name -eq 'powershell.exe' -and
                    $_.CommandLine -like '*Measure-Cursor.ps1*'
                } |
                ForEach-Object {
                    Stop-Process $_.ProcessId -ErrorAction SilentlyContinue
                }
            foreach ($path in @(
                $Ready, $Arm, $Armed, $Measure, $Result, "$Result.tmp"
            )) {
                Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
            }
        } -ArgumentList (
            $measureTask,
            $ready,
            $arm,
            $armed,
            $measure,
            $result
        )
        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', "`"$(Join-Path $guestRoot 'Measure-Cursor.ps1')`"",
            '-ReadyPath', "`"$ready`"",
            '-ArmPath', "`"$arm`"",
            '-ArmedPath', "`"$armed`"",
            '-MeasurePath', "`"$measure`"",
            '-ResultPath', "`"$result`""
        ) -join ' '
        Start-InteractiveTask $measureTask 'powershell.exe' $arguments
        $desktopReady = Test-GuestFile $ready
    } while (-not $desktopReady -and (Get-Date) -lt $desktopDeadline)
    if (-not $desktopReady) {
        throw 'The interactive VM desktop did not become ready.'
    }

    Invoke-Command -Session $session {
        param($Path)
        Set-Content -LiteralPath $Path -Value arm -Encoding ascii
    } -ArgumentList $arm
    Wait-GuestFile $armed
    Invoke-Command -Session $session {
        param($Path)
        Set-Content -LiteralPath $Path -Value measure -Encoding ascii
    } -ArgumentList $measure

    Type-Key $keys.Caps
    Type-Key $keys.Space
    Invoke-Key PressKey $keys.H
    Start-Sleep -Milliseconds 300
    Invoke-Key ReleaseKey $keys.H
    Type-Key $keys.Escape
    Wait-GuestFile $result

    $json = Invoke-Command -Session $session {
        param($Path, $TaskName)
        $content = Get-Content -LiteralPath $Path -Raw
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false `
            -ErrorAction SilentlyContinue
        $content
    } -ArgumentList $result, $measureTask
    $measurement = $json | ConvertFrom-Json
    if ($measurement.Error) {
        throw $measurement.Error
    }
    $measurement
}

try {
    foreach ($required in @(
        $ArtifactPath, $checksumPath, $CredentialPath, $measureScript
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required file not found: '$required'."
        }
    }

    $checksum = Get-Content $checksumPath -Raw
    $artifactName = [IO.Path]::GetFileName($ArtifactPath)
    $match = [regex]::Match(
        $checksum.TrimEnd(),
        "^([0-9a-f]{64})  $([regex]::Escape($artifactName))$")
    $artifactHash = Get-Sha256 $ArtifactPath
    if (-not $match.Success -or $artifactHash -ne $match.Groups[1].Value) {
        throw 'Runtime artifact checksum is invalid.'
    }

    New-Item -ItemType Directory $temporaryRoot | Out-Null
    $runtimeRoot = Join-Path $temporaryRoot runtime
    Expand-Archive $ArtifactPath $runtimeRoot
    $manifest = Get-Content (Join-Path $runtimeRoot runtime.json) -Raw |
        ConvertFrom-Json
    $kanataUri = [Uri] $manifest.kanata.uri
    $kanataAsset = [IO.Path]::GetFileName($kanataUri.AbsolutePath)
    $kanataArchive = Join-Path $repositoryRoot `
        "artifacts\tools\kanata-runtime\$kanataAsset"
    if (-not (Test-Path $kanataArchive) -or
        (Get-Sha256 $kanataArchive) -ne $manifest.kanata.sha256) {
        throw 'Pinned Kanata archive is missing or invalid; run verification.'
    }
    $kanataRoot = Join-Path $temporaryRoot kanata
    Expand-Archive $kanataArchive $kanataRoot
    $kanataExecutable = Join-Path $kanataRoot $manifest.kanata.executable

    $vm = Get-VM $VmName
    $initialVmState = $vm.State
    if ($vm.State -in @('Off', 'Saved')) {
        Start-VM $VmName | Out-Null
    } elseif ($vm.State -ne 'Running') {
        throw "VM '$VmName' is '$($vm.State)'."
    }

    $credential = Import-Clixml $CredentialPath
    $deadline = (Get-Date).AddMinutes(2)
    do {
        try {
            $session = New-PSSession -VMName $VmName `
                -Credential $credential -ErrorAction Stop
        }
        catch {
            Start-Sleep -Seconds 2
        }
    } while (-not $session -and (Get-Date) -lt $deadline)
    if (-not $session) {
        throw "PowerShell Direct did not connect to '$VmName'."
    }
    $interactiveUser = Wait-InteractiveUser

    $vmSystem = Get-CimInstance -Namespace root\virtualization\v2 `
        -ClassName Msvm_ComputerSystem |
        Where-Object ElementName -eq $VmName
    $keyboard = Get-CimAssociatedInstance -InputObject $vmSystem `
        -ResultClassName Msvm_Keyboard
    if (-not $keyboard) {
        throw "Virtual keyboard not found for '$VmName'."
    }

    $releaseName = "candidate-$($artifactHash.Substring(0, 12))"
    Invoke-Command -Session $session {
        param($Root, $Release)
        New-Item -ItemType Directory `
            (Join-Path $Root "releases\$Release") -Force | Out-Null
    } -ArgumentList $guestRoot, $releaseName
    Copy-Item $measureScript $guestRoot -ToSession $session
    $candidate = Join-Path $guestRoot "releases\$releaseName"
    Copy-Item $kanataExecutable $candidate -ToSession $session
    Copy-Item (Join-Path $runtimeRoot $manifest.configuration) `
        $candidate -ToSession $session

    $guestHashes = Invoke-Command -Session $session {
        param($Release, $Configuration, $Executable)
        [pscustomobject]@{
            Configuration = (Get-FileHash `
                (Join-Path $Release $Configuration) -Algorithm SHA256).Hash
            Executable = (Get-FileHash `
                (Join-Path $Release $Executable) -Algorithm SHA256).Hash
        }
    } -ArgumentList (
        $candidate,
        $manifest.configuration,
        $manifest.kanata.executable
    )
    $configurationHash = Get-Sha256 `
        (Join-Path $runtimeRoot $manifest.configuration)
    $executableHash = Get-Sha256 $kanataExecutable
    Add-Check provisioned-bits (
        $guestHashes.Configuration.ToLowerInvariant() -eq
            $configurationHash -and
        $guestHashes.Executable.ToLowerInvariant() -eq $executableHash
    ) $guestHashes

    $candidateProcess = Start-Runtime $candidate
    $startup = Measure-Left
    Add-Check startup (
        $startup.DeltaX -lt -5 -and [Math]::Abs($startup.DeltaY) -le 2
    ) ([pscustomobject]@{
        Release = $releaseName
        ProcessId = $candidateProcess.ProcessId
        Movement = $startup
    })

    $restartedProcess = Restart-Runtime `
        $candidate `
        $candidateProcess.ProcessId
    $restart = Measure-Left
    Add-Check restart (
        $restartedProcess.ProcessId -ne $candidateProcess.ProcessId -and
        $restart.DeltaX -lt -5 -and [Math]::Abs($restart.DeltaY) -le 2
    ) ([pscustomobject]@{
        Release = $releaseName
        PreviousProcessId = $candidateProcess.ProcessId
        ProcessId = $restartedProcess.ProcessId
        Movement = $restart
    })
}
catch {
    $bodyError = $_.Exception.ToString()
}
finally {
    if ($keyboard) {
        try {
            Invoke-Key ReleaseKey $keys.H
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($session) {
        try {
            Stop-ReleaseProcesses
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
        try {
            Invoke-Command -Session $session {
                param($RuntimeTask, $MeasureTask, $Root)
                foreach ($task in @($RuntimeTask, $MeasureTask)) {
                    Stop-ScheduledTask $task -ErrorAction SilentlyContinue
                    Unregister-ScheduledTask $task -Confirm:$false `
                        -ErrorAction SilentlyContinue
                }
                Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
            } -ArgumentList $runtimeTask, $measureTask, $guestRoot
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
        try {
            Remove-PSSession $session
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($initialVmState -in @('Off', 'Saved')) {
        try {
            Stop-VM $VmName -TurnOff
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$result = [pscustomobject]@{
    Passed = -not $bodyError -and
        -not ($checks.Passed -contains $false) -and
        $cleanupErrors.Count -eq 0
    ArtifactSha256 = if (Test-Path $ArtifactPath) {
        Get-Sha256 $ArtifactPath
    } else {
        $null
    }
    Checks = $checks
    Error = $bodyError
    CleanupErrors = $cleanupErrors
}
New-Item -ItemType Directory (Split-Path $OutputPath) -Force | Out-Null
$result | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8
if (-not $result.Passed) {
    exit 1
}
