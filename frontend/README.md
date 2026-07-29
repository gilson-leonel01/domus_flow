# DomusFlow Web — Angular

Frontend standalone em Angular 20 para a API DomusFlow.

## Desenvolvimento

```bash
npm ci
npm start
```

O servidor de desenvolvimento usa `proxy.conf.json` para encaminhar `/api` e `/health` para `http://localhost:8080`.

## Build de produção

```bash
npm run build
```

O container Nginx encaminha as chamadas da API para o serviço Docker `api:8080`.

## Identidade visual

Os ficheiros do pacote de logótipos estão em `public/assets/brand/` e são usados no acesso, navegação, favicon e notificações.
