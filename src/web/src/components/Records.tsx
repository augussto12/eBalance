import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, api } from '../api/client';
import type { Occurrence } from '../api/types';
import { formatDate } from '../lib/dates';
import { formatMoney, type Currency } from '../lib/money';
import { isPaymentsApplied, occurrenceHasPayments, paymentsOfOccurrence, paymentsOfPlan, type PaymentHistoryTarget } from '../lib/payments';
import { invalidateFinancial } from '../pages/queries';
import { PaymentHistoryDialog } from './Payments';
import { useUi } from './Ui';

/**
 * Eliminación reversible con confirmación (modal, no confirm()) y «Deshacer». Restaurar exige la versión que devolvió
 * el borrado. Si el registro tiene pagos aplicados, el aviso ofrece la lista de pagos para revertirlos.
 */
export function useDeleteOccurrence(showPayments?: (target: PaymentHistoryTarget) => void) {
  const qc = useQueryClient();
  const { confirm, toast } = useUi();
  return async (o: Occurrence) => {
    const isInstallment = o.source === 'installment';
    const ok = await confirm({
      title: isInstallment ? 'Eliminar plan de cuotas' : 'Eliminar registro',
      message: isInstallment
        ? <>Se eliminará el plan completo «{o.concept}» con todas sus cuotas. Podés restaurarlo desde la papelera en Configuración.</>
        : <>Se eliminará «{o.concept}». Podés restaurarlo desde la papelera en Configuración.</>,
      confirmLabel: 'Eliminar',
      danger: true,
    });
    if (!ok) return;
    const base = isInstallment ? `/plans/${o.planId}` : `/entries/${o.id}`;
    try {
      const deleted = await api<{ version: number }>('DELETE', `${base}?version=${o.version}`);
      await invalidateFinancial(qc);
      toast('Registro eliminado', {
        action: {
          label: 'Deshacer',
          run: async () => {
            try {
              await api('POST', `${base}/restore?version=${deleted.version}`);
              await invalidateFinancial(qc);
              toast('Registro restaurado');
            } catch (e) {
              toast(e instanceof ApiError ? e.title : 'No se pudo restaurar.', { error: true });
            }
          },
        },
      });
    } catch (e) {
      if (isPaymentsApplied(e) && showPayments) {
        const target = isInstallment ? paymentsOfPlan(o.planId!, o.concept) : paymentsOfOccurrence(o);
        toast(e.title, { error: true, action: { label: 'Ver pagos', run: () => showPayments(target) } });
      } else {
        toast(e instanceof ApiError ? e.title : 'No se pudo eliminar.', { error: true });
      }
      await invalidateFinancial(qc);
    }
  };
}

export function EmptyState() {
  const { openRecord } = useUi();
  return (
    <div className="empty">
      <strong>Empezá por tu primer movimiento</strong>
      Agregá un ingreso, un gasto o una compra en cuotas.
      <br />
      Tus números van a aparecer acá.
      <br />
      <button onClick={() => openRecord({ mode: 'create' })}>＋ Agregar registro</button>
    </div>
  );
}

/** Tabla de movimientos con la misma estructura que el prototipo; los textos se renderizan siempre como texto. */
export function OccurrenceTable({ rows, currency }: { rows: Occurrence[]; currency: Currency }) {
  const { openRecord } = useUi();
  const [payments, setPayments] = useState<PaymentHistoryTarget | null>(null);
  const remove = useDeleteOccurrence(setPayments);
  if (!rows.length) return <EmptyState />;
  return (
    <div className="tablewrap">
      <PaymentHistoryDialog target={payments} onClose={() => setPayments(null)} />
      <table>
        <thead>
          <tr>
            <th>Concepto</th>
            <th>Categoría</th>
            <th>Cuenta / tarjeta</th>
            <th>Importe</th>
            <th><span className="sr-only">Acciones</span></th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={`${r.source}-${r.id}`}>
              <td>
                {r.concept}
                <small>
                  {r.installmentNumber ? `Cuota ${r.installmentNumber} de ${r.installmentCount} · desde ${formatDate(r.planStart ?? r.date)}` : formatDate(r.date)}
                  {r.source === 'installment' && <InstallmentStatus o={r} currency={currency} />}
                </small>
              </td>
              <td><span className="badge">{r.categoryName ?? 'Sin categoría'}</span></td>
              <td>{r.accountName ?? 'Sin especificar'}</td>
              <td className={r.kind === 'income' ? 'green' : undefined}>{r.kind === 'income' ? '+' : '−'} {formatMoney(r.amountMinor, currency)}</td>
              <td>
                <div className="rowactions">
                  {occurrenceHasPayments(r) && (
                    <button className="delete" aria-label={`Ver pagos de ${r.concept}`} title="Ver pagos" onClick={() => setPayments(paymentsOfOccurrence(r))}>≡</button>
                  )}
                  <button
                    className="delete"
                    aria-label={`Editar ${r.concept}`}
                    title="Editar"
                    onClick={() => openRecord(r.source === 'installment' ? { mode: 'edit-plan', id: r.planId! } : { mode: 'edit-entry', id: r.id })}
                  >✎</button>
                  <button className="delete" aria-label={`Eliminar ${r.concept}`} title="Eliminar" onClick={() => remove(r)}>×</button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function InstallmentStatus({ o, currency }: { o: Occurrence; currency: Currency }) {
  if (o.declaredPaid) return <span className="status paid"> · pagada antes de registrar</span>;
  if (o.pendingMinor === '0') return <span className="status paid"> · pagada</span>;
  if (o.pendingMinor !== o.amountMinor) return <span className="status partial"> · pendiente {formatMoney(o.pendingMinor, currency)}</span>;
  return <span className="status"> · pendiente</span>;
}

export function QueryState({ isPending, error, onRetry }: { isPending: boolean; error: unknown; onRetry: () => void }) {
  if (isPending) return <div className="loading">Cargando…</div>;
  if (error)
    return (
      <div className="errorbox" role="alert">
        {error instanceof ApiError ? error.title : 'No se pudieron cargar los datos.'}
        <br />
        <button onClick={onRetry}>Reintentar</button>
      </div>
    );
  return null;
}
