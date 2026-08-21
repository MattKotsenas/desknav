#requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..'
)).Path
$solution = Join-Path $root 'desknav.slnx'
$requiredFiles = @(
    'AGENTS.md',
    'Directory.Build.props',
    'Directory.Packages.props',
    'LICENSE',
    'README.md',
    'desknav.slnx',
    'global.json'
)
foreach ($requiredFile in $requiredFiles)
{
    if (-not (Test-Path `
            -LiteralPath (Join-Path $root $requiredFile) `
            -PathType Leaf))
    {
        throw "Required repository file is missing: $requiredFile"
    }
}

function Read-ProjectXml
{
    param([Parameter(Mandatory)][string] $Path)

    $content = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($content))
    {
        throw "Project XML is empty: $Path"
    }
    [xml] $document = $content
    if ($document.DocumentElement.Name -ne 'Project')
    {
        throw "Project XML must have a Project root: $Path"
    }
    return $document
}

$null = Read-ProjectXml (
    Join-Path $root 'Directory.Build.props'
)
$null = Read-ProjectXml (
    Join-Path $root 'Directory.Packages.props'
)
$null = Get-Content `
    -LiteralPath (Join-Path $root 'global.json') `
    -Raw |
    ConvertFrom-Json

[xml] $solutionDocument = Get-Content `
    -LiteralPath $solution `
    -Raw
if ($solutionDocument.DocumentElement.Name -ne 'Solution')
{
    throw 'desknav.slnx must have a Solution root element.'
}

dotnet sln $solution list | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$listedProjects = @(
    $solutionDocument.SelectNodes('//Project') |
        ForEach-Object {
            $_.Path.Replace('/', '\')
        }
)
function Get-GitFiles
{
    $startInfo = [Diagnostics.ProcessStartInfo]::new(
        'git')
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '-c',
        'core.quotepath=false',
        '-C',
        $root,
        'ls-files',
        '--cached',
        '--others',
        '--exclude-standard',
        '-z'
    ))
    {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    try
    {
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0)
        {
            throw "git ls-files failed: $errorOutput"
        }
        return $output.Split(
            [char] 0,
            [StringSplitOptions]::RemoveEmptyEntries)
    }
    finally
    {
        $process.Dispose()
    }
}

$discoveredProjects = @(
    Get-GitFiles |
        Where-Object {
            [IO.Path]::GetExtension($_).EndsWith(
                'proj',
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            $_.Replace('/', '\')
        }
)
$unsupportedProjects = @(
    $discoveredProjects |
        Where-Object {
            [IO.Path]::GetExtension($_) -ne '.csproj'
        }
)
if ($unsupportedProjects.Count -gt 0)
{
    throw (
        'Only C# projects are supported. Unsupported: [{0}].' -f
        ($unsupportedProjects -join ', ')
    )
}

$missing = @(
    $discoveredProjects |
        Where-Object { $_ -notin $listedProjects }
)
$stale = @(
    $listedProjects |
        Where-Object { $_ -notin $discoveredProjects }
)
if ($missing.Count -gt 0 -or $stale.Count -gt 0)
{
    throw (
        "desknav.slnx project drift. Missing: [{0}]. Stale: [{1}]." -f
        ($missing -join ', '),
        ($stale -join ', ')
    )
}

if ($discoveredProjects.Count -eq 0)
{
    Write-Output (
        'Repository metadata and empty solution validated. ' +
        'No .NET projects are present.'
    )
    exit 0
}

foreach ($project in $discoveredProjects)
{
    $projectPath = Join-Path $root $project
    foreach ($configuration in @('Debug', 'Release'))
    {
        $evaluated = (
            dotnet msbuild `
                $projectPath `
                -nologo `
                -p:Configuration=$configuration `
                -getProperty:TargetFramework `
                -getProperty:TargetFrameworks
        ) -join "`n"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $properties = (
            $evaluated |
                ConvertFrom-Json
        ).Properties
        $multiTargeted = -not [string]::IsNullOrWhiteSpace(
            $properties.TargetFrameworks)
        $unsupportedTarget =
            $properties.TargetFramework -notin @(
                'net10.0',
                'net10.0-windows'
            )
        if ($multiTargeted -or $unsupportedTarget)
        {
            throw (
                '{0} must target exactly net10.0 or net10.0-windows in {1}. Evaluated: [{2}]' -f
                $project,
                $configuration,
                ($properties | ConvertTo-Json -Compress)
            )
        }
    }
}

dotnet restore `
    $solution `
    -p:Configuration=Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build `
    $solution `
    -c Release `
    --no-restore `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet format `
    $solution `
    --verify-no-changes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test `
    $solution `
    -c Release `
    --no-build `
    --nologo `
    --filter '(Category!=Visual&Category!=VmAcceptance)&(TestCategory!=Visual&TestCategory!=VmAcceptance)'
exit $LASTEXITCODE
