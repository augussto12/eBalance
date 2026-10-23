import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';
import type { Page, Payment, PaymentFilter } from '../api/types';
import { render, seed, seedError, testClient, text } from '../test/render';
import { PAYMENTS_PAGE_SIZE, PaymentHistoryDialog, PaymentList, revertPayment } from './Payments';

const payment = (id: string, over: Partial<Payment> = {}): Payment => ({
  id, sourceAccountId: 'a1', sourceAccountName: 'Banco de prueba', date: '2026-10-05', currency: 'ARS', amountMinor: '8000000', note: null,
  allocations: [{ installmentId: 'i1', entryId: null, amountMinor: '8000000', planId: 'p1', planKind: 'card', concept: 'Monitor', installmentNumber: 1, installmentCount: 6, dueDate: '2026-10-05' }],
  version: 3, createdAt: '2026-10-05T12:00:00Z', reversedAt: null, deletedAt: null, ...over,
});
const ids = (n: number, prefix = 'p') => Array.from({ length: n }, (_, i) => `${prefix}${i + 1}`);
const infinite = (total: number, pageSize: number, ...pages: string[][]) => ({
  pages: pages.map((items, i): Page<Payment> => ({ items: items.map((id) => payment(id)), page: i + 1, pageSize, total })),
  pageParams: pages.map((_, i) => i + 1),
});
const refetchFailed = new ApiError(503, { title: 'Ocurrió un error inesperado. Probá de nuevo.' });

describe('lista de pagos paginada (GET /payments devuelve items, page, pageSize y total)', () => {
  const panel: PaymentFilter = { kind: 'card', currency: 'ARS' };
  const history: PaymentFilter = { kind: 'card', currency: 'ARS', includeReversed: true };

  it('panel con los más recientes: avisa cuántos hay en total y no promete mostrar todos', () => {
    const qc = testClient();
    seed(qc, ['payments', panel, 8], infinite(20, 8, ids(8)));
    const t = text(render(<PaymentList filter={panel} limit={8} />, qc));
    expect(t).toContain('Se muestran los 8 más recientes de 20.');
    expect(t).not.toContain('muestra todos');
    expect(t).not.toContain('Ver más');
  });

  it('panel sin más pagos que el límite: sin aviso', () => {
    const qc = testClient();
    seed(qc, ['payments', panel, 8], infinite(3, 8, ids(3)));
    expect(text(render(<PaymentList filter={panel} limit={8} />, qc))).not.toContain('más recientes');
  });

  it('historial: muestra cuántos de cuántos y ofrece «Ver más» hasta llegar a todos', () => {
    const qc = testClient();
    seed(qc, ['payments', history, PAYMENTS_PAGE_SIZE], infinite(120, PAYMENTS_PAGE_SIZE, ids(50)));
    const html = render(<PaymentHistoryDialog target={{ title: 'Historial de pagos de tarjetas', filter: panel }} onClose={() => {}} />, qc);
    expect(text(html)).toContain('50 de 120 pagos');
    expect(html).toMatch(/<button type="button">Ver más<\/button>/);
    // Las acciones de la lista nunca envían un formulario que la contenga.
    expect(html.match(/<button[^>]*>Revertir<\/button>/g)).toHaveLength(50);
    expect(html).not.toMatch(/<button(?![^>]*type="button")[^>]*>Revertir</);
  });

  it('historial con varias páginas cargadas: las une sin repetir y al final no ofrece más', () => {
    const qc = testClient();
    // «b» vuelve en la segunda página porque entre una y otra se registró un pago: se muestra una sola vez.
    seed(qc, ['payments', history, PAYMENTS_PAGE_SIZE], infinite(4, PAYMENTS_PAGE_SIZE, ['a', 'b'], ['b', 'c', 'd']));
    const all = render(<PaymentList filter={history} />, qc);
    expect(all.match(/>Revertir<\/button>/g)).toHaveLength(4);
    expect(text(all)).not.toContain('Ver más');
    expect(text(all)).not.toContain(' de 4 pagos');
  });

  it('si falla una recarga se avisa que puede estar desactualizado, sin ocultar los pagos', () => {
    const qc = testClient();
    seed(qc, ['payments', panel, 8], infinite(1, 8, ['p1']), refetchFailed);
    const html = render(<PaymentList filter={panel} limit={8} />, qc);
    expect(text(html)).toContain('Ocurrió un error inesperado. Probá de nuevo. Lo que ves puede estar desactualizado.');
    expect(html).toContain('>Revertir</button>');
  });

  it('si falla la primera carga: error con Reintentar', () => {
    const qc = testClient();
    seedError(qc, ['payments', panel, 8], new ApiError(0, null));
    const html = render(<PaymentList filter={panel} limit={8} />, qc);
    expect(html).toContain('No hay conexión con el servidor.');
    expect(html).toContain('<button type="button">Reintentar</button>');
  });
});

describe('revertir un pago informa el resultado por el canal indicado (en el historial, dentro del modal)', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } });

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => vi.unstubAllGlobals());

  const respond = (response: () => Response | Promise<Response>) =>
    fetchMock.mockImplementation(async (input) => (String(input).endsWith('/auth/csrf') ? json(200, { token: 'csrf-de-prueba' }) : response()));
  const run = async (confirmed = true) => {
    const report = vi.fn();
    const p = payment('pay-1');
    await revertPayment(p, { confirm: async () => confirmed, qc: new QueryClient(), report });
    return { report, p };
  };
  const deletes = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE');

  it('cancelado en la confirmación: no se envía nada', async () => {
    respond(() => new Response(null, { status: 204 }));
    const { report } = await run(false);
    expect(deletes()).toHaveLength(0);
    expect(report).not.toHaveBeenCalled();
  });

  it('éxito: DELETE con la versión vista y aviso de éxito', async () => {
    respond(() => new Response(null, { status: 204 }));
    const { report, p } = await run();
    expect(deletes().map(([url]) => String(url))).toEqual(['/api/v1/payments/pay-1?version=3']);
    expect(report).toHaveBeenCalledWith(p, 'Pago revertido. Lo que canceló volvió a quedar pendiente.');
  });

  it('409, 404 y sin conexión: el motivo llega como error', async () => {
    respond(() => json(409, { title: 'El pago cambió desde que lo abriste.' }));
    let r = await run();
    expect(r.report).toHaveBeenCalledWith(r.p, 'El pago cambió desde que lo abriste.', true);

    respond(() => json(404, { title: 'No encontrado.' }));
    r = await run();
    expect(r.report).toHaveBeenCalledWith(r.p, 'Ese pago ya fue revertido o no existe.', true);

    respond(() => Promise.reject(new TypeError('Failed to fetch')));
    r = await run();
    expect(r.report).toHaveBeenCalledWith(r.p, 'No hay conexión con el servidor.', true);
  });
});
