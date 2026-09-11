# Banco de dados

`bootstrap.sql` descreve o schema inicial do TCGLooker. Ele é idempotente para criação da base, mas ainda não foi executado em um projeto Supabase e não substitui o histórico de migrations definitivo.

Para uma base já criada com a primeira versão, execute em ordem
`migrations/002_connectors_stock_lifecycle.sql` e
`migrations/003_dynamic_store_workers_and_scope.sql` e
`migrations/004_wishlist_outbox_notifications.sql` no SQL Editor.

## Aplicação inicial

1. Crie ou selecione o projeto Supabase.
2. Revise `bootstrap.sql` no SQL Editor do projeto.
3. Execute o script e confirme a criação do schema privado `tcglooker`.
4. Configure `ConnectionStrings__DefaultConnection` no ambiente da API e do Worker.
5. Verifique `/health/ready` antes de ativar o Worker.

Para um backend persistente, prefira a conexão direta se o host tiver IPv6. Em runtime IPv4-only, use o Supavisor em modo sessão, porta 5432. Migrations e ferramentas administrativas devem usar a conexão direta.

As tabelas não ficam em `public` e o schema não é concedido a `anon` ou `authenticated`; portanto, elas não são expostas pela Data API. O Supabase Auth emite os JWTs, mas somente a API do TCGLooker usa a conexão de servidor e acessa o schema privado.
