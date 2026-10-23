import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { ApiError } from '../api/client';
import type { Dashboard, MonthTotals, Occurrence, Page } from '../api/types';
import { QueryState, RefetchError } from '../components/Records';
import { currentMonth } from '../lib/dates';
import { render, seed, seedError, testClient, text } from '../test/render';
import { MovementsPage } from './MovementsPage';
import { ProjectionPage } from './ProjectionPage';
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
