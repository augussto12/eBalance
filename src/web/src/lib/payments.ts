import { ApiError } from '../api/client';
import type { Occurrence, PaymentAllocation, PaymentFilter } from '../api/types';

/** Lista de pagos a mostrar: título del panel/modal y filtros de GET /payments. */
export interface PaymentHistoryTarget {
  title: string;
  filter: PaymentFilter;
}

const FILTER_KEYS = ['planId', 'installmentId', 'entryId', 'targetAccountId', 'kind', 'currency'] as const;

/** Ruta de GET /payments con los filtros indicados (los vacíos no se envían). */
export function paymentsPath(filter: PaymentFilter): string {
  const params = new URLSearchParams();
  for (const key of FILTER_KEYS) {
    const value = filter[key];
    if (value) params.set(key, value);
  }
  if (filter.includeReversed) params.set('includeReversed', 'true');
  const query = params.toString();
  return query ? `/payments?${query}` : '/payments';
}

/** «Monitor · cuota 2 de 6» o «Cena · consumo con tarjeta». Siempre texto plano. */
export function describeAllocation(a: PaymentAllocation): string {
  const concept = a.concept ?? 'Obligación';
  if (a.installmentNumber !== null) return `${concept} · cuota ${a.installmentNumber}${a.installmentCount ? ` de ${a.installmentCount}` : ''}`;
  if (a.entryId) return `${concept} · consumo con tarjeta`;
  return concept;
}

/** Una cuota o un consumo con tarjeta que tiene pagos vigentes aplicados (las declaradas pagadas no tienen pagos). */
export function occurrenceHasPayments(o: Occurrence): boolean {
  return !o.declaredPaid && o.pendingMinor !== o.amountMinor;
}

/** Pagos que tocan una fila de movimientos: los de esa cuota o los de ese consumo con tarjeta. */
export function paymentsOfOccurrence(o: Occurrence): PaymentHistoryTarget {
  return o.source === 'installment'
    ? { title: `Pagos de la cuota ${o.installmentNumber} de ${o.installmentCount} · ${o.concept}`, filter: { installmentId: o.id } }
    : { title: `Pagos de «${o.concept}»`, filter: { entryId: o.id } };
}

/** Pagos de un plan completo (al eliminarlo hay que revertir todos). */
export function paymentsOfPlan(planId: string, concept: string): PaymentHistoryTarget {
  return { title: `Pagos de «${concept}»`, filter: { planId } };
}

/** 409 del servidor porque el registro tiene pagos aplicados: la UI ofrece ver y revertir esos pagos. */
export function isPaymentsApplied(e: unknown): e is ApiError {
  return e instanceof ApiError && e.status === 409 && e.code === 'payments_applied';
}
