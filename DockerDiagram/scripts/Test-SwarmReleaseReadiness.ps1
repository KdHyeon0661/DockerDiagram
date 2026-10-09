param(
    [switch]$SkipTests,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Detail)
    $results.Add([pscustomobject]@{ Check = $Name; Status = $Status; Detail = $Detail })
}

function Invoke-DockerProbe {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = (($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $combinedOutput = (($standardOutput.Result, $standardError.Result) -join [Environment]::NewLine).Trim()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $combinedOutput
    }
}

Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        dotnet test 'Tests/DockerDiagram.Tests.csproj' -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
        Add-Result 'Automated tests' 'PASS' 'Release test suite passed.'
    }

    if (-not $SkipBuild) {
        dotnet build 'DockerDiagram.csproj' -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
        Add-Result 'Release build' 'PASS' 'Release build passed.'
    }

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) {
        Add-Result 'Docker CLI' 'WARN' 'docker executable was not found. Stack deploy/remove cannot run.'
    }
    else {
        $clientProbe = Invoke-DockerProbe $docker.Source @('--version')
        if ($clientProbe.ExitCode -eq 0) {
            Add-Result 'Docker CLI' 'PASS' $clientProbe.Output
        }
        else {
            Add-Result 'Docker CLI' 'WARN' $clientProbe.Output
        }

        $serverProbe = Invoke-DockerProbe $docker.Source @('version', '--format', '{{.Server.Version}}')
        if ($serverProbe.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($serverProbe.Output)) {
            Add-Result 'Docker Engine' 'PASS' "Server $($serverProbe.Output)"

            $swarmProbe = Invoke-DockerProbe $docker.Source @('info', '--format', '{{.Swarm.LocalNodeState}} control={{.Swarm.ControlAvailable}} nodes={{.Swarm.Nodes}}')
            if ($swarmProbe.ExitCode -eq 0) {
                Add-Result 'Swarm state' 'INFO' $swarmProbe.Output
            }
            else {
                Add-Result 'Swarm state' 'WARN' $swarmProbe.Output
            }
        }
        else {
            Add-Result 'Docker Engine' 'BLOCKED' $serverProbe.Output
        }
    }
}
catch {
    Add-Result 'Verification script' 'BLOCKED' $_.Exception.Message
}
finally {
    Pop-Location
}

$results | Format-Table -AutoSize -Wrap
if ($results.Status -contains 'BLOCKED') { exit 2 }
if ($results.Status -contains 'WARN') { exit 1 }
exit 0
