import { afterEach, describe, expect, it } from 'vitest';
import { QueryClient, QueryObserver, keepPreviousData, type QueryKey } from '@tanstack/react-query';
import { clampedPage, keepPreviousPage, onlyPageChanged, pageCount } from './paging';

describe('pageCount y clampedPage', () => {
  it('al menos una página; la actual fuera de rango vuelve a la última', () => {
    expect(pageCount(0, 25)).toBe(1);
    expect(pageCount(25, 25)).toBe(1);
    expect(pageCount(26, 25)).toBe(2);
    // 26 cuotas, se borra la única de la página 2: total 25 → volver a la 1.
    expect(clampedPage(2, pageCount(25, 25))).toBe(1);
    expect(clampedPage(2, 2)).toBeNull();
    expect(clampedPage(1, 1)).toBeNull();
  });
});

describe('onlyPageChanged', () => {
  const key = (currency: string, page: number, kind = 'card'): QueryKey => ['movements', '2026-10', currency, '', kind, page, 25];
  it('solo cambia la página', () => {
    expect(onlyPageChanged(key('ARS', 1), key('ARS', 2), 5)).toBe(true);
    expect(onlyPageChanged(key('ARS', 1), key('USD', 1), 5)).toBe(false);
    expect(onlyPageChanged(key('ARS', 1), key('ARS', 2, 'loan'), 5)).toBe(false);
    expect(onlyPageChanged(['movements', 1], ['movements', 1, 25], 1)).toBe(false);
  });
});

/** Con el QueryObserver real de TanStack: qué ve la pantalla mientras carga la consulta nueva. */
describe('datos de la consulta anterior mientras carga (placeholderData)', () => {
  const page = (currency: string) => ({ items: [{ concept: `Cuota en ${currency}`, amountMinor: '8000000' }], page: 1, pageSize: 25, total: 1 });
  const key = (currency: string, p = 1, kind = 'card'): QueryKey => ['movements', '2026-10', currency, '', kind, p, 25];
  const pending = () => new Promise<never>(() => {});
  let cleanup: (() => void) | null = null;
  afterEach(() => cleanup?.());

  function observe(placeholder: (k: QueryKey) => unknown) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    qc.setQueryData(key('ARS'), page('ARS'));
    const options = (k: QueryKey) => ({ queryKey: k, queryFn: pending, placeholderData: placeholder(k) as never });
    const observer = new QueryObserver(qc, options(key('ARS')));
    const unsubscribe = observer.subscribe(() => {});
    cleanup = () => { unsubscribe(); qc.clear(); };
    return (k: QueryKey) => {
      observer.setOptions(options(k));
      return observer.getCurrentResult();
    };
  }

  it('keepPreviousData mostraba las cuotas en ARS al pasar a USD (se formateaban como US$)', () => {
    const go = observe(() => keepPreviousData);
    expect(go(key('USD')).data).toEqual(page('ARS'));
  });

  it('keepPreviousPage: con otra moneda o tipo, nada (Cargando…); con otra página, la anterior', () => {
    const go = observe((k) => keepPreviousPage(k, 5));
    const usd = go(key('USD'));
    expect(usd.data).toBeUndefined();
    expect(usd.isPending).toBe(true);
    expect(go(key('ARS', 1, 'loan')).data).toBeUndefined();
    const next = go(key('ARS', 2));
    expect(next.data).toEqual(page('ARS'));
    expect(next.isPlaceholderData).toBe(true);
  });
});
