[CmdletBinding()]
param(
    [string] $Destination = (
        Join-Path $env:LOCALAPPDATA 'desknav\tools\kanata-sim'
    )
)

$ErrorActionPreference = 'Stop'
$kanataV1_11_0Revision = '4e6bec4d52d044bd13cfa01cea4e02dc2d246c65'

& cargo install `
    --git https://github.com/jtroo/kanata.git `
    --rev $kanataV1_11_0Revision `
    --locked `
    --root $Destination `
    kanata-sim
if ($LASTEXITCODE -ne 0) {
    throw "Kanata simulator installation failed with exit code $LASTEXITCODE."
}

$simulator = Join-Path $Destination 'bin\kanata_simulated_input.exe'
if (-not (Test-Path -LiteralPath $simulator -PathType Leaf)) {
    throw "Kanata simulator was not installed at '$simulator'."
}

$simulator
