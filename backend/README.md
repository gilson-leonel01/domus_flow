# DomusFlow API — ASP.NET Core

API REST em ASP.NET Core 8, PostgreSQL, Npgsql e autenticação JWT.

## Variáveis de ambiente

- `DATABASE_URL`: URL PostgreSQL ou connection string Npgsql.
- `JWT_SECRET`: segredo JWT; obrigatório em produção.
- `PORT`: porta HTTP, por omissão `8080`.
- `APP_TIMEZONE`: timezone da aplicação, por omissão `Africa/Luanda`.
- `CORS_ORIGINS`: origens permitidas, separadas por vírgula. Vazio permite qualquer origem.

## Execução local

```bash
dotnet restore
dotnet run
```

As migrações em `migrations/` são aplicadas automaticamente no arranque.
