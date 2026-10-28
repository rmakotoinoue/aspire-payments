import { RenderMode, ServerRoute } from '@angular/ssr';

export const serverRoutes: ServerRoute[] = [
  {
    // Painel de pagamentos depende de dados vivos (API + stream SSE), então é
    // renderizado no cliente. Prerenderizar esta rota faria o build tentar
    // chamar a API em tempo de build e falhar.
    path: 'payments',
    renderMode: RenderMode.Client
  },
  {
    path: '**',
    renderMode: RenderMode.Prerender
  }
];
