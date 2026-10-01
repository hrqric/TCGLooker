[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[A-Za-z0-9_.-]{3,32}$')]
    [string] $Username = 'tcglooker-worker',

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$proxyDirectory = Split-Path -Parent $PSScriptRoot
$secretsDirectory = Join-Path $proxyDirectory 'secrets'
$credentialsFile = Join-Path $secretsDirectory 'users'
$image = 'tcglooker/squid-proxy:local'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker nao foi encontrado. Instale e inicie o Docker Desktop primeiro.'
}

if ((Test-Path -LiteralPath $credentialsFile) -and -not $Force) {
    throw "O arquivo de credenciais ja existe. Use -Force apenas se quiser substituir: $credentialsFile"
}

New-Item -ItemType Directory -Path $secretsDirectory -Force | Out-Null

Push-Location $proxyDirectory
try {
    docker compose build squid
    if ($LASTEXITCODE -ne 0) {
        throw 'Nao foi possivel construir a imagem do proxy.'
    }

    Write-Host 'Digite uma senha longa e exclusiva quando o htpasswd solicitar.'
    docker run --rm -it `
        --user 0:0 `
        --volume "${secretsDirectory}:/output" `
        --entrypoint htpasswd `
        $image -cB /output/users $Username

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $credentialsFile)) {
        throw 'Nao foi possivel criar o arquivo de credenciais.'
    }
}
finally {
    Pop-Location
}

Write-Host "Credencial criada para '$Username'. O arquivo esta ignorado pelo Git."

