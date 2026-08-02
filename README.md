# DomusFlow

Aplicação full-stack para planeamento e acompanhamento de rotinas domésticas.

## Stack

- **Frontend:** Angular 20 standalone, SCSS e Nginx.
- **Backend:** ASP.NET Core 8 Minimal API, JWT e Npgsql.
- **Base de dados:** PostgreSQL 16, com migrações SQL automáticas.
- **Identidade visual:** pacote oficial DomusFlow integrado em `frontend/public/assets/brand/`.

## Executar com Docker Compose

```bash
docker compose up --build
```

A aplicação fica disponível em `http://localhost:8080`.

### Perfis de demonstração

| Perfil | E-mail | Palavra-passe |
|---|---|---|
| Gestora | `ana@demo.local` | `Demo123!` |
| Colaboradora | `rosa@demo.local` | `Demo123!` |
| Filho | `mateus@demo.local` | `Demo123!` |

## Desenvolvimento local

### Backend

Requisitos: .NET SDK 8 e PostgreSQL.

```bash
cd backend
export DATABASE_URL='postgres://domusflow:domusflow@localhost:5432/domusflow?sslmode=disable'
export JWT_SECRET='replace-with-at-least-32-random-characters'
dotnet restore
dotnet run
```

A API inicia em `http://localhost:8080`. As migrações da pasta `backend/migrations` são aplicadas no arranque.

### Frontend

Requisitos: Node.js 22 e npm.

```bash
cd frontend
npm ci
npm start
```

O Angular inicia em `http://localhost:4200` e encaminha `/api` para o backend local através de `proxy.conf.json`.

## Endpoints principais

- `POST /api/auth/login`
- `GET /api/me`
- `GET /api/dashboard?date=YYYY-MM-DD`
- `GET|POST|PATCH|DELETE /api/tasks`
- `POST /api/work/check-in`
- `POST /api/work/check-out`
- `GET|POST /api/holidays`
- `GET /api/rewards`
- `POST /api/rewards/{id}/claim`
