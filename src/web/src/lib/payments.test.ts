import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import type { Occurrence, PaymentAllocation } from '../api/types';
import { describeAllocation, isPaymentsApplied, occurrenceHasPayments, paymentsOfOccurrence, paymentsOfPlan, paymentsPath } from './payments';

const allocation = (over: Partial<PaymentAllocation>): PaymentAllocation => ({
  installmentId: null, entryId: null, amountMinor: '100', planId: null, planKind: null, concept: 'Monitor',
  installmentNumber: null, installmentCount: null, dueDate: null, ...over,
});

const occurrence = (over: Partial<Occurrence>): Occurrence => ({
  source: 'installment', id: 'i-2', planId: 'p-1', kind: 'expense', planKind: 'card', concept: 'Monitor', amountMinor: '8000000',
  pendingMinor: '8000000', date: '2026-11-05', planStart: '2026-10-05', categoryName: null, accountName: 'Visa',
  installmentNumber: 2, installmentCount: 6, declaredPaid: false, version: 1, ...over,
});

describe('paymentsPath', () => {
  it('envía solo los filtros presentes', () => {
    expect(paymentsPath({})).toBe('/payments');
    expect(paymentsPath({ planId: 'p-1', includeReversed: true })).toBe('/payments?planId=p-1&includeReversed=true');
    expect(paymentsPath({ kind: 'card', currency: 'USD', includeReversed: false })).toBe('/payments?kind=card&currency=USD');
    expect(paymentsPath({ entryId: 'a&b' })).toBe('/payments?entryId=a%26b');
  });
});

describe('describeAllocation', () => {
  it('describe cuotas y consumos', () => {
    expect(describeAllocation(allocation({ installmentId: 'i', installmentNumber: 2, installmentCount: 6 }))).toBe('Monitor · cuota 2 de 6');
    expect(describeAllocation(allocation({ entryId: 'e', concept: 'Cena' }))).toBe('Cena · consumo con tarjeta');
    expect(describeAllocation(allocation({ concept: null }))).toBe('Obligación');
  });
});

describe('pagos de una fila de movimientos', () => {
  it('detecta pagos vigentes, no cuotas declaradas pagadas', () => {
    expect(occurrenceHasPayments(occurrence({}))).toBe(false);
    expect(occurrenceHasPayments(occurrence({ pendingMinor: '3000000' }))).toBe(true);
    expect(occurrenceHasPayments(occurrence({ pendingMinor: '0' }))).toBe(true);
    expect(occurrenceHasPayments(occurrence({ pendingMinor: '0', declaredPaid: true }))).toBe(false);
  });

  it('filtra por cuota, por consumo o por plan', () => {
    expect(paymentsOfOccurrence(occurrence({}))).toEqual({ title: 'Pagos de la cuota 2 de 6 · Monitor', filter: { installmentId: 'i-2' } });
    expect(paymentsOfOccurrence(occurrence({ source: 'entry', id: 'e-1', concept: 'Cena' }))).toEqual({ title: 'Pagos de «Cena»', filter: { entryId: 'e-1' } });
    expect(paymentsOfPlan('p-1', 'Monitor')).toEqual({ title: 'Pagos de «Monitor»', filter: { planId: 'p-1' } });
  });
});

describe('isPaymentsApplied', () => {
  it('reconoce solo el 409 con código payments_applied', () => {
    expect(isPaymentsApplied(new ApiError(409, { title: 'Revertí los pagos', code: 'payments_applied' }))).toBe(true);
    expect(isPaymentsApplied(new ApiError(409, { title: 'El registro cambió' }))).toBe(false);
    expect(isPaymentsApplied(new ApiError(422, { title: 'x', code: 'payments_applied' }))).toBe(false);
    expect(isPaymentsApplied(new Error('x'))).toBe(false);
  });
});
