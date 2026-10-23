import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { ApiError } from '../api/client';
import type { Dashboard, MonthTotals, Occurrence, Page, Plan, TrashItem } from '../api/types';
import { Pager, QueryState, RefetchError } from '../components/Records';
import { currentMonth } from '../lib/dates';
import { render, seed, seedError, testClient, TEST_ME, text } from '../test/render';
import { MovementsPage } from './MovementsPage';
import { PlansPage } from './PlansPage';
import { ProjectionPage } from './ProjectionPage';
import { SettingsPage } from './SettingsPage';
import { SummaryPage } from './SummaryPage';

const month = currentMonth();
const occurrence: Occurrence = {
  source: 'entry', id: 'e1', planId: null, kind: 'expense', planKind: null, concept: 'Supermercado de prueba', amountMinor: '8650000',
  pendingMinor: '8650000', date: `${month}-05`, planStart: null, categoryName: 'Alimentación', accountName: 'Débito', installmentNumber: null,
  installmentCount: null, declaredPaid: false, version: 1,
};
const page: Page<Occurrence> = { items: [occurrence], page: 1, pageSize: 25, total: 1 };
const totals = (m: string): MonthTotals => ({
  month: m, incomeMinor: '210000000', expenseMinor: '8650000', installmentsMinor: '0', installmentsPendingMinor: '0', installmentsPaidMinor: '0', balanceMinor: '201350000',
});
const dashboard: Dashboard = { month, currency: 'ARS', totals: totals(month), series: [totals(month)], categories: [{ name: 'Alimentación', amountMinor: '8650000' }], recent: [occurrence] };
const refetchFailed = new ApiError(503, { title: 'Ocurrió un error inesperado. Probá de nuevo.' });

describe('QueryState y RefetchError', () => {
  it('carga, error con Reintentar y nada cuando hay datos', () => {
    expect(renderToStaticMarkup(<QueryState isPending error={null} onRetry={() => {}} />)).toContain('Cargando…');
    const failed = renderToStaticMarkup(<QueryState isPending={false} error={new ApiError(0, null)} onRetry={() => {}} />);
    expect(failed).toContain('role="alert"');
    expect(failed).toContain('No hay conexión con el servidor.');
    expect(failed).toContain('Reintentar');
    expect(renderToStaticMarkup(<QueryState isPending={false} error={null} onRetry={() => {}} />)).toBe('');
  });

  it('recarga fallida con datos previos: aviso de datos desactualizados y reintento', () => {
    const html = renderToStaticMarkup(<RefetchError error={refetchFailed} onRetry={() => {}} />);
    expect(html).toContain('role="alert"');
    expect(text(html)).toContain('puede estar desactualizado');
    expect(html).toContain('>Reintentar</button>');
    expect(renderToStaticMarkup(<RefetchError error={refetchFailed} retrying onRetry={() => {}} />)).toMatch(/disabled="">Reintentando…/);
    expect(renderToStaticMarkup(<RefetchError error={null} onRetry={() => {}} />)).toBe('');
  });
});

describe('Movimientos (keepPreviousData)', () => {
  const key = ['movements', month, 'ARS', '', 'all', 1];

  it('con datos y sin error: tabla, sin avisos', () => {
    const qc = testClient();
    seed(qc, key, page);
    const html = render(<MovementsPage />, qc);
    expect(html).toContain('Supermercado de prueba');
    expect(html).not.toContain('desactualizado');
  });

  it('si falla una recarga se ve el error y Reintentar, no solo las filas viejas', () => {
    const qc = testClient();
    seed(qc, key, page, refetchFailed);
    const html = render(<MovementsPage />, qc);
    expect(html).toContain('Supermercado de prueba');
    expect(text(html)).toContain('Ocurrió un error inesperado. Probá de nuevo. Lo que ves puede estar desactualizado.');
    expect(html).toContain('>Reintentar</button>');
  });

  it('si falla la primera carga: error con Reintentar en lugar de la tabla', () => {
    const qc = testClient();
    seedError(qc, key, new ApiError(0, null));
    const html = render(<MovementsPage />, qc);
    expect(html).toContain('No hay conexión con el servidor.');
    expect(html).toContain('Reintentar');
    expect(html).not.toContain('<table');
  });

  it('mientras carga por primera vez: Cargando…', () => {
    expect(render(<MovementsPage />)).toContain('Cargando…');
  });
});

describe('Resumen y Proyección también avisan una recarga fallida', () => {
  it('Resumen', () => {
    const qc = testClient();
    seed(qc, ['dashboard', month, 'ARS'], dashboard, refetchFailed);
    const html = render(<SummaryPage />, qc);
    expect(html).toContain('Balance del mes');
    expect(text(html)).toContain('puede estar desactualizado');
  });

  it('Proyección', () => {
    const qc = testClient();
    seed(qc, ['projection', month, 'ARS'], { months: [totals(month)] }, refetchFailed);
    const html = render(<ProjectionPage />, qc);
    expect(html).toContain('<table');
    expect(text(html)).toContain('puede estar desactualizado');
  });
});

const STALE = 'Lo que ves puede estar desactualizado.';
const count = (haystack: string, needle: string) => haystack.split(needle).length - 1;

describe('Tarjetas/Préstamos y Papelera avisan una recarga fallida (después de pagar, revertir o restaurar)', () => {
  const installment: Occurrence = {
    ...occurrence, source: 'installment', id: 'i1', planId: 'p1', planKind: 'card', concept: 'Monitor de prueba', installmentNumber: 2,
    installmentCount: 6, planStart: `${month}-05`,
  };
  const plan = {
    id: 'p1', kind: 'card', concept: 'Monitor de prueba', note: null, totalMinor: '48000000', count: 6, currency: 'ARS', firstDueDate: `${month}-05`,
    lastDueDate: null, accountId: null, accountName: 'Visa', categoryId: null, categoryName: null, paidBeforeCount: 0, remainingOnly: false,
    disbursementMinor: null, disbursementDate: null, disbursementAccountId: null, paidMinor: '8000000', pendingMinor: '40000000',
    currentInstallment: 2, remainingInstallments: 4, nextDue: { number: 2, dueDate: `${month}-05`, pendingMinor: '8000000' }, hasPayments: true,
    finished: false, version: 1, deletedAt: null, installments: null,
  } satisfies Plan;
  const monthKey = ['movements', month, 'ARS', '', 'card', 1, 25];

  it('cuotas del mes y planes: datos previos con el aviso y Reintentar en cada lista', () => {
    const qc = testClient();
    seed(qc, monthKey, { items: [installment], page: 1, pageSize: 25, total: 1 } satisfies Page<Occurrence>, refetchFailed);
    seed(qc, ['plans', 'card', 'ARS'], [plan], refetchFailed);
    const html = render(<PlansPage kind="card" />, qc);
    expect(html).toContain('Monitor de prueba');
    expect(count(text(html), STALE)).toBe(2);
  });

  it('sin errores: sin avisos', () => {
    const qc = testClient();
    seed(qc, monthKey, { items: [installment], page: 1, pageSize: 25, total: 1 } satisfies Page<Occurrence>);
    seed(qc, ['plans', 'card', 'ARS'], [plan]);
    expect(text(render(<PlansPage kind="card" />, qc))).not.toContain(STALE);
  });

  it('Papelera', () => {
    const qc = testClient();
    const item: TrashItem = { type: 'entry', id: 't1', concept: 'Borrado de prueba', currency: 'ARS', amountMinor: '100', date: '2026-10-01', deletedAt: '2026-10-05T03:00:00Z', version: 2 };
    seed(qc, ['trash'], [item], refetchFailed);
    const html = text(render(<SettingsPage me={TEST_ME} />, qc));
    expect(html).toContain('Borrado de prueba');
    expect(count(html, STALE)).toBe(1);
  });
});

describe('paginador', () => {
  const pager = (page: number, pages: number, total: number) => renderToStaticMarkup(<Pager page={page} pages={pages} total={total} noun="cuotas" onPage={() => {}} />);

  it('una sola página: no se muestra', () => {
    expect(pager(1, 1, 3)).toBe('');
  });

  it('varias páginas: total, página actual y botones', () => {
    const html = pager(1, 2, 26);
    expect(text(html)).toContain('26 cuotas · página 1 de 2');
    expect(html).toMatch(/disabled="">‹ Anterior/);
    expect(html).not.toMatch(/disabled="">Siguiente/);
  });

  it('página fuera de rango (se borró lo último de la última página): sigue visible para volver', () => {
    const html = pager(2, 1, 25);
    expect(text(html)).toContain('25 cuotas · página 2 de 1');
    expect(html).not.toMatch(/disabled="">‹ Anterior/);
    expect(html).toMatch(/disabled="">Siguiente/);
  });
});
