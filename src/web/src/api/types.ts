import type { Currency } from '../lib/money';

export type AuthState = 'anonymous' | 'mfa_required' | 'enroll_required' | 'authenticated';

export interface Me {
  state: AuthState;
  user?: { id: string; username: string; displayName: string; initials: string; timeZone: string; version: number };
  recoveryCodesRemaining?: number;
  elevatedUntil?: string | null;
  demoEnabled?: boolean;
}

export type AccountKind = 'cash' | 'bank' | 'wallet' | 'credit_card';

export interface Account {
  id: string;
  name: string;
  kind: AccountKind;
  currency: Currency;
  last4: string | null;
  openingBalanceMinor: string | null;
  openingDate: string | null;
  version: number;
  deletedAt: string | null;
  cashBalanceMinor: string | null;
  /** Día (del calendario del propietario) hasta el que se sumaron movimientos al saldo: los de fecha futura no cuentan. */
  cashBalanceAsOf: string | null;
  pendingObligationsMinor: string | null;
}

export interface Category {
  id: string;
  name: string;
  kind: 'expense' | 'income' | 'any';
  version: number;
  deletedAt: string | null;
}

export interface Entry {
  id: string;
  kind: 'income' | 'expense';
  concept: string;
  note: string | null;
  amountMinor: string;
  paidMinor: string;
  currency: Currency;
  date: string;
  accountId: string | null;
  accountName: string | null;
  categoryId: string | null;
  categoryName: string | null;
  version: number;
  deletedAt: string | null;
}

export interface Installment {
  id: string;
  number: number;
  amountMinor: string;
  paidMinor: string;
  pendingMinor: string;
  dueDate: string;
  declaredPaid: boolean;
  status: 'pending' | 'partial' | 'paid' | 'declared_paid';
}

export interface Plan {
  id: string;
  kind: 'card' | 'loan';
  concept: string;
  note: string | null;
  totalMinor: string;
  count: number;
  currency: Currency;
  firstDueDate: string;
  lastDueDate: string | null;
  accountId: string | null;
  accountName: string | null;
  categoryId: string | null;
  categoryName: string | null;
  paidBeforeCount: number;
  remainingOnly: boolean;
  disbursementMinor: string | null;
  disbursementDate: string | null;
  disbursementAccountId: string | null;
  paidMinor: string;
  pendingMinor: string;
  currentInstallment: number;
  remainingInstallments: number;
  nextDue: { number: number; dueDate: string; pendingMinor: string } | null;
  hasPayments: boolean;
  finished: boolean;
  version: number;
  deletedAt: string | null;
  installments: Installment[] | null;
}

export interface Occurrence {
  source: 'entry' | 'installment';
  id: string;
  planId: string | null;
  kind: 'income' | 'expense';
  planKind: 'card' | 'loan' | null;
  concept: string;
  amountMinor: string;
  pendingMinor: string;
  date: string;
  planStart: string | null;
  categoryName: string | null;
  accountName: string | null;
  installmentNumber: number | null;
  installmentCount: number | null;
  declaredPaid: boolean;
  version: number;
}

export interface MonthTotals {
  month: string;
  incomeMinor: string;
  expenseMinor: string;
  installmentsMinor: string;
  installmentsPendingMinor: string;
  installmentsPaidMinor: string;
  balanceMinor: string;
}

export interface Dashboard {
  month: string;
  currency: Currency;
  totals: MonthTotals;
  series: MonthTotals[];
  categories: { name: string; amountMinor: string }[];
  recent: Occurrence[];
}

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface Obligation {
  type: 'installment' | 'entry';
  id: string;
  concept: string;
  dueDate: string;
  amountMinor: string;
  pendingMinor: string;
  planId: string | null;
  number: number | null;
  count: number | null;
}

export interface Transfer {
  id: string;
  fromAccountId: string;
  fromAccountName: string | null;
  toAccountId: string;
  toAccountName: string | null;
  currency: Currency;
  amountMinor: string;
  date: string;
  note: string | null;
  version: number;
  deletedAt: string | null;
}

/** Qué canceló un pago: una cuota de un plan o un consumo cargado a tarjeta. */
export interface PaymentAllocation {
  installmentId: string | null;
  entryId: string | null;
  amountMinor: string;
  planId: string | null;
  planKind: 'card' | 'loan' | null;
  concept: string | null;
  installmentNumber: number | null;
  installmentCount: number | null;
  dueDate: string | null;
}

export interface Payment {
  id: string;
  sourceAccountId: string;
  sourceAccountName: string | null;
  date: string;
  currency: Currency;
  amountMinor: string;
  note: string | null;
  allocations: PaymentAllocation[];
  version: number;
  createdAt: string;
  /** Fecha de reversión; el pago revertido queda en el historial. */
  reversedAt: string | null;
  deletedAt: string | null;
}

/** Filtros de GET /payments. */
export interface PaymentFilter {
  planId?: string;
  installmentId?: string;
  entryId?: string;
  targetAccountId?: string;
  kind?: 'card' | 'loan';
  currency?: Currency;
  includeReversed?: boolean;
}

export interface SessionInfo {
  id: string;
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string;
  userAgent: string | null;
  ipPrefix: string | null;
  current: boolean;
}

export interface TrashItem {
  type: 'entry' | 'card' | 'loan' | 'transfer';
  id: string;
  concept: string;
  currency: Currency;
  amountMinor: string;
  date: string;
  deletedAt: string;
  /** Versión actual: la restauración la exige igual que el borrado. */
  version: number;
}

export interface ImportPreview {
  batchId: string;
  expiresAt: string;
  summary: { format: string; total: number; new: number; duplicates: number; conflicts: number; byTarget: Record<string, number>; byCurrency: Record<string, number> };
  conflicts: { target: string; sourceId: string; concept: string | null }[];
  warnings: string[];
  accountsToCreate: { key: string; name: string; currency: Currency; usedByPlans: boolean }[];
  categoriesToCreate: string[];
  sample: { target: string; type: string | null; concept: string | null; amountMinor: string | null; currency: string | null; date: string | null; count: string | null; category: string | null; account: string | null }[];
}
