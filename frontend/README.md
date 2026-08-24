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


## Vercel deployment

This project is configured as an Angular SPA. Build with `npm run build`; the Vercel output directory is `dist/frontend/browser`. Angular routes are handled by the SPA rewrite in `vercel.json`.

The `/api/*` paths are intentionally excluded from the SPA fallback. Configure the production backend separately (or add a Vercel rewrite to the real backend URL) so API requests do not receive `index.html`.
