[CmdletBinding()]
param(
    [string] $ProxyHost = '127.0.0.1',
    [ValidateRange(1, 65535)]
    [int] $ProxyPort = 3128,
    [string] $Username = 'tcglooker-worker'
)

$ErrorActionPreference = 'Stop'

# O Windows PowerShell 5.1 nao carrega System.Net.Http automaticamente.
Add-Type -AssemblyName System.Net.Http

$securePassword = Read-Host 'Senha do proxy' -AsSecureString
$credential = [System.Net.NetworkCredential]::new($Username, $securePassword)
$proxyUri = [Uri]::new("http://${ProxyHost}:${ProxyPort}")

# Evita que o Windows PowerShell 5.1 tente negociar TLS legado com as lojas.
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor
    [System.Net.SecurityProtocolType]::Tls12

# Separa falha de rede local de falha HTTP/TLS e produz uma mensagem objetiva.
$tcpClient = [System.Net.Sockets.TcpClient]::new()
try {
    $connect = $tcpClient.BeginConnect($ProxyHost, $ProxyPort, $null, $null)
    if (-not $connect.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(5))) {
        throw "Timeout ao conectar em ${ProxyHost}:${ProxyPort}."
    }

    $tcpClient.EndConnect($connect)
    Write-Host "Proxy acessivel em ${ProxyHost}:${ProxyPort}."
}
catch {
    throw "Nao foi possivel abrir uma conexao TCP com o proxy em ${ProxyHost}:${ProxyPort}. Confirme 'docker compose ps' e a porta publicada. Causa: $($_.Exception.Message)"
}
finally {
    $tcpClient.Dispose()
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $true
$handler.Proxy = [System.Net.WebProxy]::new($proxyUri)
$handler.Proxy.Credentials = $credential
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)

try {
    $allowed = $client.GetAsync(
        'https://www.cardshall.com.br/',
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()

    if ([int] $allowed.StatusCode -eq 407) {
        throw 'O proxy recusou as credenciais.'
    }

    Write-Host "Loja passou pelo proxy autenticado (HTTP $([int] $allowed.StatusCode))."
    $allowed.Dispose()

    $generic = $client.GetAsync(
        'https://example.com/',
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()

    if ([int] $generic.StatusCode -eq 407) {
        throw 'O proxy recusou as credenciais ao acessar um destino HTTPS dinamico.'
    }

    Write-Host "Destino HTTPS dinamico passou pelo proxy (HTTP $([int] $generic.StatusCode))."
    $generic.Dispose()

    $unauthenticatedHandler = [System.Net.Http.HttpClientHandler]::new()
    $unauthenticatedHandler.UseProxy = $true
    $unauthenticatedHandler.Proxy = [System.Net.WebProxy]::new($proxyUri)
    $unauthenticatedClient = [System.Net.Http.HttpClient]::new($unauthenticatedHandler)
    $unauthenticatedClient.Timeout = [TimeSpan]::FromSeconds(30)
    $authenticationRequired = $false

    try {
        $rejected = $unauthenticatedClient.GetAsync(
            'https://example.com/',
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()

        $authenticationRequired = [int] $rejected.StatusCode -eq 407
        if (-not $authenticationRequired) {
            throw "A protecao falhou: uma requisicao sem credencial retornou HTTP $([int] $rejected.StatusCode), esperado 407."
        }

        $rejected.Dispose()
    }
    catch {
        # No Windows PowerShell 5.1, o HttpClient converte o HTTP 407 recebido
        # durante o CONNECT do proxy em WebException em vez de retornar response.
        $candidate = $_.Exception
        while ($null -ne $candidate -and -not ($candidate -is [System.Net.WebException])) {
            $candidate = $candidate.InnerException
        }

        if ($candidate -is [System.Net.WebException] -and
            $candidate.Response -is [System.Net.HttpWebResponse] -and
            [int] $candidate.Response.StatusCode -eq 407) {
            $authenticationRequired = $true
            $candidate.Response.Dispose()
        }
        else {
            throw
        }
    }
    finally {
        $unauthenticatedClient.Dispose()
        $unauthenticatedHandler.Dispose()
    }

    if ($authenticationRequired) {
        Write-Host 'Acesso sem credencial foi bloqueado corretamente (HTTP 407).'
    }
}
catch {
    $causes = [System.Collections.Generic.List[string]]::new()
    $currentException = $_.Exception
    while ($null -ne $currentException) {
        $causes.Add("$($currentException.GetType().FullName): $($currentException.Message)")
        $currentException = $currentException.InnerException
    }

    throw "Falha ao testar o proxy em $proxyUri`n$($causes -join "`nCausada por: ")"
}
finally {
    $client.Dispose()
    $handler.Dispose()
}

