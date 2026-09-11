# ADR-001: iniciar como monólito modular

- Status: aceito
- Data: 2026-08-27

## Contexto

O TCGLooker precisa expor busca HTTP, coletar lojas periodicamente e entregar notificações. O volume, a equipe e os requisitos de disponibilidade ainda não demonstram necessidade de distribuição. As partes compartilham o mesmo modelo de catálogo e exigem consistência entre alteração de disponibilidade e criação de uma notificação.

## Decisão

Manter um único repositório e módulos com dependências direcionadas. API e Worker podem ser processos separados usando os mesmos projetos Application, Domain e Infrastructure e o mesmo PostgreSQL. Eventos duráveis usam uma outbox no banco.

## Consequências

- implantação e observabilidade iniciais mais simples;
- transações locais para oferta e outbox;
- conectores continuam isolados por interfaces e podem ser extraídos depois;
- uma falha pesada de scraping pode competir por recursos se API e Worker forem executados no mesmo processo; por isso o desenho permite separá-los sem mudar o domínio;
- broker dedicado, cache distribuído e descoberta de serviços ficam adiados até existirem métricas que os justifiquem.

## Alternativas consideradas

- **Microsserviço por loja:** rejeitado porque multiplica deploys, segredos, telemetria e contratos para poucos conectores.
- **Funções serverless por scraping:** possível no futuro para execuções esparsas, mas browsers headless, limites de duração e observabilidade variam por provedor.
- **Apenas uma Web API com timers em memória:** rejeitado como estado final porque reinícios podem interromper/agrupar execuções e dificultam reprocessamento; jobs e cursores devem ser persistidos.
