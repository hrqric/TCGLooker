# Proxy residencial seguro do TCGLooker

Este pacote executa um Squid autenticado no seu PC para o Worker do Northflank.
Ele foi desenhado para trafegar pela sua rede privada Tailscale, sem abrir a porta
3128 no roteador.

As protecoes aplicadas sao:

- autenticacao com usuario e senha armazenada como hash bcrypt;
- porta publicada somente no endereco definido em `SQUID_BIND_IP`;
- destinos HTTPS dinamicos, permitindo lojas privadas cadastradas pela API;
- somente tunel HTTPS (`CONNECT` na porta 443);
- container sem root, sem capabilities, com filesystem somente leitura e limites
  de CPU, memoria e processos;
- credenciais fora da imagem e ignoradas pelo Git;
- credenciais excluidas do contexto de build pelo `.dockerignore`;
- bloqueio final de qualquer requisicao nao autorizada.

## 1. Preparar Docker e Tailscale

Instale o Docker Desktop e o cliente Tailscale para Windows. Entre na mesma tailnet
que sera configurada no Northflank. A antiga extensao Tailscale para Docker Desktop
foi descontinuada e nao e necessaria para esta configuracao.

Nao crie redirecionamento de porta no roteador. O protocolo de proxy entre o Worker
e o Squid e HTTP com autenticacao Basic; a confidencialidade dessa etapa vem do
tunel WireGuard da Tailscale.

## 2. Criar a credencial

No PowerShell, a partir da raiz do repositorio:

```powershell
Set-Location .\ops\home-proxy
.\scripts\New-Credentials.ps1
```

O script pede a senha de forma interativa, portanto ela nao fica no historico do
terminal. Use uma senha aleatoria e exclusiva, idealmente com 24 caracteres ou mais.

## 3. Iniciar o proxy

Copie `.env.example` para `.env` e mantenha `127.0.0.1`. Assim, nem sua rede local
consegue acessar o Squid diretamente.

```powershell
Copy-Item .env.example .env
docker compose up -d --build
docker compose ps
```

Teste conectividade, destinos dinamicos e autenticacao:

```powershell
.\scripts\Test-Proxy.ps1
```

O teste deve informar que Cards Hall e um destino HTTPS dinamico passaram com a
credencial, enquanto uma tentativa sem credencial recebeu HTTP 407.

Depois, em um PowerShell executado como administrador, publique o Squid somente na
tailnet e confira o endereco criado:

```powershell
tailscale serve --bg --tcp=3128 tcp://127.0.0.1:3128
tailscale serve status
tailscale ip -4
```

Use `tailscale serve --tcp=3128 off` para remover esse encaminhamento. Nao use
`tailscale funnel`: Funnel tornaria o servico publico na internet.

## 4. Dar acesso ao Worker no Northflank

No Northflank:

1. Execute `tailscale ip -4` no PC. Copie
   `tailscale-policy.fragment.hujson.example`, troque o IP de exemplo por esse IP
   e mescle as secoes na policy que ja existe na sua tailnet.
2. Crie no Tailscale um OAuth Client com permissao **Write** para `auth_keys` e
   selecione somente `tag:northflank`. Guarde o Client ID e o Client Secret.
3. Em **Project settings > Tailscale**, habilite o sidecar e informe o Client ID,
   Client Secret e a tag.
4. Ative **Restrict Tailscale**. Crie uma tag de recurso no Northflank, por exemplo
   `tailscale-access`, selecione-a nessa restricao e aplique-a somente ao Worker.
   Essa tag do Northflank e diferente de `tag:northflank` do Tailscale.
5. Ative **Auto-redeploy on key regeneration**, salve e faca redeploy do Worker.
6. Nas variaveis secretas do Worker, configure:

```text
Scraping__Proxy__Enabled=true
Scraping__Proxy__Url=http://IP-OU-FQDN-TAILSCALE-DO-SQUID:3128
Scraping__Proxy__Username=tcglooker-worker
Scraping__Proxy__Password=SENHA_CRIADA_PELO_NEW-CREDENTIALS
```

Use o FQDN completo, por exemplo `proxy.exemplo-ts.net`; o nome curto da maquina
nao funciona no sidecar do Northflank.

Na politica de acesso da Tailscale, o alias `tcglooker-home-proxy` aponta para o IP
Tailscale do seu PC e aceita na porta TCP 3128 apenas origens com
`tag:northflank`. Isso cria a restricao de origem; o Squid restringe autenticacao,
portas e destinos. Revise regras amplas preexistentes na sua policy: as permissoes
da Tailscale sao cumulativas, portanto uma regra que libera `*` pode anular na
pratica essa segmentacao.

## Operacao

```powershell
# Ver os logs
docker compose logs -f squid

# Reiniciar
docker compose restart squid

# Parar
docker compose down

# Trocar a senha (substitui o arquivo existente)
.\scripts\New-Credentials.ps1 -Force
docker compose restart squid
```

O proxy depende de o PC, o Docker Desktop e a Tailscale permanecerem ligados. Se
o IPv4/nome Tailscale mudar, atualize `Scraping__Proxy__Url` no Northflank.

