# DomusFlow

Sistema mobile-first para planear, delegar, monitorizar e recompensar tarefas domésticas. A dona de casa atua como gestora; a empregada valida a chegada antes de executar a agenda; os filhos recebem tarefas próprias, inclusive aos domingos e feriados.

## Executar com Docker

Pré-requisito: Docker Desktop ou Docker Engine com Compose.

```bash
cp .env.example .env
docker compose up --build -d
```

Abra **http://localhost:4200**. Para acompanhar os serviços:

```bash
docker compose logs -f
```

Para eliminar todos os dados e recriar a demonstração:

```bash
docker compose down -v
docker compose up --build -d
```

## Contas de demonstração

| Perfil | E-mail | Palavra-passe |
|---|---|---|
| Gestora | `ana@demo.local` | `Demo123!` |
| Empregada | `rosa@demo.local` | `Demo123!` |
| Filho | `mateus@demo.local` | `Demo123!` |

## Regras funcionais

- A gestora cria, delega e elimina tarefas ainda não iniciadas.
- A empregada precisa validar o início da jornada antes de iniciar tarefas.
- A agenda da empregada funciona de segunda-feira a sábado.
- Aos domingos e feriados cadastrados, o expediente da empregada é bloqueado; filhos continuam a consultar e executar tarefas próprias.
- XP por conclusão: **75 XP** até 80% do tempo previsto, **50 XP** dentro do prazo e **20 XP** após o prazo.
- O mês libera recompensa de bónus a 1.000 XP e dia de folga a 1.500 XP.
- O navegador pode emitir uma notificação dez minutos antes de uma tarefa do dia, mediante autorização.
- As datas e regras usam `Africa/Luanda` por padrão; o país da casa e os feriados ficam persistidos no PostgreSQL.

## Arquitetura

```text
Angular 20 SPA
    │ REST + JWT
    ▼
Go 1.23 API (net/http)
    │ pgx / transações
    ▼
PostgreSQL 16
```

O isolamento é feito por `household_id`. A API aplica autorização por papel (`OWNER`, `EMPLOYEE`, `CHILD`) e nunca confia apenas no filtro da interface. A atribuição de XP usa transação e bloqueio da tarefa para evitar pontuação duplicada.

## Estrutura

```text
domusflow/
├── backend/
│   ├── cmd/api/                 # inicialização da API
│   ├── internal/auth/           # JWT e palavra-passe
│   ├── internal/config/         # configuração por ambiente
│   ├── internal/database/       # pool e migrações
│   ├── internal/httpapi/        # endpoints e regras de negócio
│   └── migrations/              # schema e demonstração
├── frontend/
│   ├── src/app/                 # UI, sessão e integração REST
│   ├── Dockerfile
│   └── nginx.conf               # SPA e proxy /api
├── docker-compose.yml
└── Makefile
```

## Endpoints principais

- `POST /api/auth/login`
- `GET /api/dashboard?date=YYYY-MM-DD`
- `GET|POST /api/tasks`
- `PATCH|DELETE /api/tasks/{id}`
- `POST /api/work/check-in`
- `POST /api/work/check-out`
- `POST /api/tasks/{id}/start`
- `POST /api/tasks/{id}/complete`
- `GET|POST /api/holidays`
- `GET /api/rewards`
- `POST /api/rewards/{id}/claim`

## Desenvolvimento sem Docker

Frontend:

```bash
cd frontend
npm install
npm start
```

Backend (requer PostgreSQL e `DATABASE_URL`):

```bash
cd backend
go mod tidy
go run ./cmd/api
```

## Próximas extensões recomendadas

A base está preparada para separar notificações num worker, integrar calendário oficial de feriados por fornecedor externo, adicionar Web Push, anexos, recorrência de tarefas, aprovação de recompensas e auditoria detalhada.
