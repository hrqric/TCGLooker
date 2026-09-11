# ADR 003 — Workers dinâmicos por site e Supabase Auth

Status: aceito  
Data: 2026-08-31

## Contexto

O agendador anterior percorria todos os conectores sequencialmente. Um site lento atrasava os demais e novos sites dependiam de registro estático na aplicação. O produto também passou a aceitar sites globais e exclusivos de um usuário.

## Decisão

- O processo Worker contém um supervisor que consulta sites habilitados a cada 30 segundos e mantém exatamente um loop independente por registro de `store`.
- Uma trava consultiva PostgreSQL por `store_id` impede coleta duplicada quando mais de uma instância do Worker estiver ativa.
- Sites usam `scope = global | user`; `owner_user_id` é obrigatório apenas no escopo `user`.
- Supabase Auth emite o access token. A API valida assinatura, emissor, audiência e expiração pelo endpoint OIDC/JWKS do projeto.
- O claim `sub` é a identidade externa. Sites privados sempre recebem o proprietário derivado desse claim.
- Cadastro global exige `app_metadata.tcglooker_role = admin`. `user_metadata` não participa de autorização.
- O schema `tcglooker` continua privado e fora da Data API. A autorização é aplicada pela API e pelas consultas SQL orientadas ao usuário.
- Nesta etapa somente `connector_type = liga_magic` é aceito. Adicionar uma URL não cria um parser novo.

### Bypass de autenticação local

O secret `dev=true` habilita uma identidade administrativa fixa sem token somente quando o host está em `Development`. A configuração não exige `Supabase:Authority` nesse modo. Em qualquer outro ambiente, `dev=true` impede a inicialização em vez de remover autenticação silenciosamente.

## Validação de site

Antes do `insert`, a API:

1. aceita somente URL HTTPS na porta padrão, sem credenciais;
2. resolve o domínio e rejeita loopback, redes privadas, link-local, carrier-grade NAT e endereços não públicos;
3. limita resposta a 2 MiB, timeout a 15 segundos e redirecionamentos a três;
4. exige HTML e marcadores reconhecidos pelo parser LigaMagic;
5. normaliza e persiste somente a origem canônica;
6. rejeita duplicidade de origem por índice único case-insensitive que ignora a barra final.

As mesmas regras de resolução pública são aplicadas durante o scraping. Links de produto fora da origem cadastrada são ignorados e redirecionamentos para outra origem fazem a coleta falhar isoladamente.

## Consequências

- Um site indisponível ou lento não bloqueia os demais.
- Inserir ou desabilitar um site é refletido sem reiniciar o Worker, com atraso máximo igual ao intervalo de sincronização.
- Cada coleta ativa mantém uma conexão PostgreSQL reservada para a trava consultiva; a hipótese atual de até dez lojas comporta esse custo.
- Sites válidos, mas temporariamente sem nenhum produto reconhecível, serão rejeitados e precisarão ser cadastrados quando voltarem a expor uma página compatível.
- A validação reduz entradas inválidas e SSRF, mas não garante estabilidade futura do HTML; falhas continuam isoladas e observáveis por site.
