# TCGLooker

API para agregar ofertas de cartas Pokémon TCG em lojas suportadas, permitir busca por carta e avisar usuários quando itens da wishlist ficarem disponíveis.

O projeto está na fase de fundação arquitetural. A proposta inicial, o modelo de dados, os contratos da API e as decisões ainda abertas estão em [docs/architecture/base-architecture.md](docs/architecture/base-architecture.md).

## Princípios da primeira versão

- monólito modular, com API e processamento em segundo plano no mesmo repositório;
- PostgreSQL hospedado no Supabase como fonte de verdade, sem Supabase Auth no MVP;
- Cards Hall e Tabletop TCG como primeiros conectores;
- conectores de lojas explicitamente suportadas, sem scraping de URLs arbitrárias;
- separação entre carta, impressão/variante e oferta de uma loja;
- coleta idempotente e notificações com deduplicação;
- Telegram como primeiro canal recomendado; WhatsApp após validar opt-in e templates do provedor.

## Estado atual

A fundação usa .NET 10 e está separada em API, Worker, Application, Domain e Infra. O domínio inicial, os contratos dos conectores, os health checks e o schema PostgreSQL revisável estão criados. A próxima entrega é o primeiro fluxo vertical de scraping, começando por uma das duas lojas.

## Configuração local

Copie apenas os nomes de configuração de `.env.example` para o gerenciador de segredos do ambiente. Nunca versione a senha do Supabase. O schema inicial está em `database/bootstrap.sql`; ele ainda não foi aplicado a nenhum projeto Supabase.
