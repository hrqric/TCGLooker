# TCGLooker

API para agregar ofertas de cartas Pokémon TCG em lojas suportadas, ajudando montadores de deck a encontrar as cartas desejadas e avisando quando itens da wishlist ficarem disponíveis.

O projeto está na fase de fundação arquitetural. A proposta inicial, o modelo de dados, os contratos da API e as decisões ainda abertas estão em [docs/architecture/base-architecture.md](docs/architecture/base-architecture.md).

## Princípios da primeira versão

- monólito modular, com API e processamento em segundo plano no mesmo repositório;
- PostgreSQL hospedado no Supabase como fonte de verdade, sem Supabase Auth no MVP;
- Cards Hall e Tabletop TCG como primeiros conectores;
- cobertura de todas as coleções, idiomas, condições e acabamentos encontrados nas lojas;
- conectores de lojas explicitamente suportadas, sem scraping de URLs arbitrárias;
- separação entre carta, impressão/variante e oferta de uma loja;
- coleta idempotente e notificações com deduplicação;
- Telegram como primeiro canal recomendado; WhatsApp após validar opt-in e templates do provedor.

## Estado atual

A fundação usa .NET 10 e está separada em API, Worker, Application, Domain e Infra. Os conectores da Cards Hall e da Tabletop TCG coletam cartas Pokémon por HTTP, o Worker persiste as ofertas no PostgreSQL e a API expõe busca somente de ofertas disponíveis.

## Configuração local

API e Worker compartilham o mesmo armazenamento local de User Secrets do .NET. Cadastre a conexão uma única vez na raiz do repositório:

```powershell
dotnet user-secrets set --project TCGLooker.API "ConnectionStrings:DefaultConnection" "SUA_CONNECTION_STRING"
```

O segredo fica fora do repositório e não deve ser colocado em `appsettings.json` ou commitado. Para uma base nova, execute `database/bootstrap.sql`. Para uma base criada pela versão anterior, execute `database/migrations/002_connectors_stock_lifecycle.sql`.

Depois, inicie os dois processos em terminais separados sem definir variáveis:

```powershell
dotnet run --project TCGLooker.Worker
```

```powershell
dotnet run --project TCGLooker.API --launch-profile http
```

Teste a busca em `http://localhost:5204/api/v1/cards/search?q=Charizard`. A primeira varredura é completa e pode demorar, mas ofertas confirmadas em estoque são publicadas ao final de cada página. Depois disso, o Worker consulta novidades a cada 15 minutos e realiza uma reconciliação completa a cada 24 horas.

## Política de disponibilidade

- resultados da API incluem somente ofertas `in_stock`;
- estoque explicitamente zerado é marcado imediatamente como indisponível;
- ausência só marca uma oferta como indisponível após duas varreduras completas bem-sucedidas;
- falhas e varreduras incrementais nunca retiram ofertas;
- ofertas indisponíveis são removidas depois de 30 dias quando não há notificação pendente; cartas e impressões permanecem no catálogo.
- uma loja que responda `403 Forbidden` entra em espera por 24 horas; as demais lojas continuam sendo coletadas normalmente.
