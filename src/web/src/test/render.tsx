/**
 * Utilidades de prueba sin DOM: render a HTML estático (react-dom/server) con los mismos proveedores que la app.
 * Sirven para verificar estados y accesibilidad del marcado; no se usan en producción.
 */
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { QueryClient, QueryClientProvider, type QueryKey } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import type { Me } from '../api/types';
import { UiProvider } from '../components/Ui';

export const TEST_ME: Me = {
  state: 'authenticated',
  user: { id: '00000000-0000-0000-0000-000000000001', username: 'prueba', displayName: 'Persona de prueba', initials: 'PP', timeZone: 'America/Argentina/Buenos_Aires', version: 1 },
  recoveryCodesRemaining: 10,
  elevatedUntil: null,
  demoEnabled: false,
};

/** Sin reintentos ni recargas al montar: el render muestra exactamente el estado sembrado en la caché. */
export const testClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false, retryOnMount: false, staleTime: Infinity, refetchOnMount: false } } });

export function render(ui: ReactNode, qc: QueryClient = testClient()): string {
  return renderToStaticMarkup(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <UiProvider me={TEST_ME}>{ui}</UiProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** Datos ya cargados y, si se indica, el error de una recarga posterior (TanStack Query conserva los datos). */
export function seed(qc: QueryClient, key: QueryKey, data: unknown, refetchError?: unknown): void {
  qc.setQueryData(key, data);
  if (refetchError) qc.getQueryCache().find({ queryKey: key, exact: true })!.setState({ status: 'error', error: refetchError as Error, errorUpdatedAt: Date.now() });
}

/** La primera carga falló: error sin datos. */
export function seedError(qc: QueryClient, key: QueryKey, error: unknown): void {
  qc.getQueryCache().build(qc, { queryKey: key }).setState({ status: 'error', error: error as Error, errorUpdatedAt: Date.now() });
}

/** Ids referenciados por aria-describedby que no existen en el HTML (deben ser cero). */
export function danglingDescribedBy(html: string): string[] {
  const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]!));
  return [...html.matchAll(/aria-describedby="([^"]+)"/g)].flatMap((m) => m[1]!.split(/\s+/)).filter((id) => !ids.has(id));
}

/** Texto visible aproximado (sin etiquetas) para buscar mensajes. */
export const text = (html: string) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ');
