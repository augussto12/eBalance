import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import type { Entry, Plan } from '../api/types';
import { danglingDescribedBy, render, text } from '../test/render';
import { RECORD_FIELD_MAP, RecordFormBody, empty, saveFailure, type FormState } from './RecordForm';

const PAYMENTS_APPLIED_TITLE = 'Este gasto tiene pagos aplicados. Revertí los pagos antes de cambiar importe, moneda, tipo o tarjeta.';
const paymentsApplied = new ApiError(409, { title: PAYMENTS_APPLIED_TITLE, code: 'payments_applied' });
const versionConflict = new ApiError(409, { title: 'El registro cambió desde que lo abriste. Revisá los datos actuales antes de guardar.' });

const entry: Entry = {
  id: 'e-1', kind: 'expense', concept: 'Cena', note: null, amountMinor: '1000000', paidMinor: '1000000', currency: 'ARS', date: '2026-10-05',
  accountId: 'visa', accountName: 'Visa', categoryId: null, categoryName: null, version: 2, deletedAt: null,
};
const form: FormState = { ...empty('expense', 'ARS'), concept: 'Cena', amount: '10000', date: '2026-10-05', accountId: 'visa', version: 2 };
const noop = () => {};

const formErrorHtml = (html: string) => /<p class="formerror" role="alert">(.*?)<\/p>/.exec(html)?.[1] ?? '';
const selectFor = (html: string, label: string) => new RegExp(`${label}<select[^>]*>`).exec(html)?.[0] ?? '';

describe('guardar un registro con pagos aplicados', () => {
  it('409 payments_applied no se trata como cambio concurrente; 409 de versión sí', () => {
    expect(saveFailure(paymentsApplied, RECORD_FIELD_MAP)).toEqual({ fields: {}, form: PAYMENTS_APPLIED_TITLE, conflict: false, paymentsApplied: true });
    expect(saveFailure(versionConflict, RECORD_FIELD_MAP)).toMatchObject({ conflict: true, paymentsApplied: false });
    const invalid = saveFailure(new ApiError(400, { title: 'Revisá los datos ingresados.', errors: { amountMinor: ['Importe no válido.'], version: ['Falta la versión.'] } }), RECORD_FIELD_MAP);
    expect(invalid).toEqual({ fields: { amount: ['Importe no válido.'] }, form: 'Revisá los datos ingresados. Falta la versión.', conflict: false, paymentsApplied: false });
    expect(saveFailure(new Error('x'), RECORD_FIELD_MAP)).toEqual({ fields: {}, form: 'No se pudo guardar. Probá de nuevo.', conflict: false, paymentsApplied: false });
  });

  it('muestra el motivo del servidor y «Ver pagos», no «Este registro cambió»', () => {
    const html = render(<RecordFormBody initial={form} editId="e-1" onClose={noop} onShowPayments={noop} initialFailure={saveFailure(paymentsApplied, RECORD_FIELD_MAP)} />);
    expect(text(formErrorHtml(html))).toContain(PAYMENTS_APPLIED_TITLE);
    expect(formErrorHtml(html)).toContain('<button type="button" class="linkbtn">Ver pagos</button>');
    expect(text(html)).not.toContain('Este registro cambió desde que lo abriste');
  });

  it('un cambio concurrente sigue mostrando el aviso con «Cargar la versión actual» y sin «Ver pagos»', () => {
    const html = render(<RecordFormBody initial={form} editId="e-1" onClose={noop} reload={noop} onShowPayments={noop} initialFailure={saveFailure(versionConflict, RECORD_FIELD_MAP)} />);
    expect(text(html)).toContain('Este registro cambió desde que lo abriste');
    expect(html).toContain('Cargar la versión actual');
    expect(html).not.toContain('Ver pagos');
  });

  it('gasto con pagos: moneda y tarjeta bloqueadas, aviso con lo pagado y acceso a los pagos', () => {
    const html = render(<RecordFormBody initial={form} editId="e-1" entry={entry} onClose={noop} onShowPayments={noop} />);
    const t = text(html);
    expect(t).toContain('Este gasto tiene pagos aplicados por');
    expect(t).toContain('la moneda y la tarjeta quedan fijas');
    expect(html).toContain('<button type="button" class="linkbtn">Ver pagos</button>');
    expect(selectFor(html, 'Moneda')).toContain('disabled=""');
    expect(selectFor(html, 'Cuenta / tarjeta')).toContain('disabled=""');
    expect(danglingDescribedBy(html)).toEqual([]);
  });

  it('gasto sin pagos: nada bloqueado ni avisos de pagos', () => {
    const html = render(<RecordFormBody initial={form} editId="e-1" entry={{ ...entry, paidMinor: '0' }} onClose={noop} onShowPayments={noop} />);
    expect(text(html)).not.toContain('pagos aplicados');
    expect(html).not.toContain('Ver pagos');
    expect(selectFor(html, 'Moneda')).toMatch(/^Moneda<select/);
    expect(selectFor(html, 'Moneda')).not.toContain('disabled');
    expect(selectFor(html, 'Cuenta / tarjeta')).not.toContain('disabled');
  });

  it('plan con cuotas pagadas: el aviso de bloqueo también lleva a los pagos', () => {
    const plan = {
      id: 'p-1', kind: 'card', concept: 'Monitor', note: null, totalMinor: '48000000', count: 6, currency: 'ARS', firstDueDate: '2026-10-05',
      hasPayments: true, version: 1, installments: [{ id: 'i-2', number: 2, amountMinor: '8000000', paidMinor: '0', pendingMinor: '8000000', dueDate: '2026-11-05', declaredPaid: false, status: 'pending' }],
    } as Plan;
    const html = render(<RecordFormBody initial={{ ...empty('card', 'ARS'), concept: 'Monitor', version: 1 }} editId="p-1" plan={plan} onClose={noop} onShowPayments={noop} />);
    expect(text(html)).toContain('para cambiarlos, primero revertí los pagos.');
    expect(html).toContain('<button type="button" class="linkbtn">Ver pagos</button>');
  });
});
