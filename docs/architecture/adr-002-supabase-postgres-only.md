# ADR-002: usar Supabase como PostgreSQL gerenciado

- Status: aceito
- Data: 2026-08-27

## Contexto

O MVP precisa de PostgreSQL, mas ainda não precisa de Supabase Auth, Storage, Realtime ou Data API. A API e o Worker são processos de backend persistentes e precisam controlar consultas, transações e o fluxo de outbox diretamente.

## Decisão

Usar o banco PostgreSQL do Supabase por connection string via Npgsql. As tabelas ficam no schema privado `tcglooker`, sem concessões para os papéis `anon` e `authenticated`. O SDK Supabase não é uma dependência da solução.

Em ambientes persistentes com IPv6, usar conexão direta. Em ambientes somente IPv4, usar Supavisor em modo sessão. Migrations e ferramentas administrativas usam conexão direta.

## Consequências

- o domínio e os casos de uso permanecem independentes do provedor;
- a Data API não expõe acidentalmente as tabelas operacionais;
- segredos do banco existem apenas na API, no Worker e no pipeline de migrations;
- autenticação e políticas RLS serão desenhadas quando a identidade entrar no escopo;
- trocar a hospedagem PostgreSQL não exige alterar Domain ou Application.
