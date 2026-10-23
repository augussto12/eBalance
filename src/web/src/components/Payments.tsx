import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api } from '../api/client';
import type { Payment, PaymentFilter } from '../api/types';
import { formatDate, formatDateTime } from '../lib/dates';
import { formatMoney } from '../lib/money';
import { describeAllocation, paymentsPath, type PaymentHistoryTarget } from '../lib/payments';
import { invalidateFinancial } from '../pages/queries';
import { Modal } from './Modal';
import { useUi } from './Ui';

function revertError(e: unknown): string {
  if (e instanceof ApiError && e.status === 404) return 'Ese pago ya fue revertido o no existe.';
  return e instanceof ApiError ? e.title : 'No se pudo revertir el pago.';
}

/**
 * Reversión explícita (DELETE con la versión que se ve en pantalla): lo cancelado vuelve a quedar pendiente y la cuenta
 * de origen recupera el importe. El servidor conserva el pago como revertido, con su auditoría.
 */
export function useRevertPayment() {
  const qc = useQueryClient();
  const { confirm, toast } = useUi();
  return async (p: Payment): Promise<void> => {
    const ok = await confirm({
      title: 'Revertir pago',
      message: (
        <>
          Se revierte el pago de {formatMoney(p.amountMinor, p.currency)} del {formatDate(p.date)}
          {p.sourceAccountName && <> desde {p.sourceAccountName}</>}. Lo que canceló vuelve a quedar pendiente y la cuenta recupera el importe.
          El pago queda en el historial como revertido.
        </>
      ),
      confirmLabel: 'Revertir pago',
      danger: true,
    });
    if (!ok) return;
    try {
      await api('DELETE', `/payments/${p.id}?version=${p.version}`);
      await invalidateFinancial(qc);
      toast('Pago revertido. Lo que canceló volvió a quedar pendiente.');
    } catch (e) {
      // 409 (versión vieja) o 404 (ya revertido): se recarga todo desde el servidor antes de reintentar.
      await invalidateFinancial(qc);
      toast(revertError(e), { error: true });
    }
  };
}

/** Pagos con fecha, cuenta de origen, importe y lo que cancelaron; cada pago vigente se puede revertir. */
export function PaymentList({ filter, limit, emptyText = 'No hay pagos registrados.' }: { filter: PaymentFilter; limit?: number; emptyText?: string }) {
  const q = useQuery({
    queryKey: ['payments', filter],
    queryFn: ({ signal }) => api<Payment[]>('GET', paymentsPath(filter), undefined, { signal }),
  });
  const revert = useRevertPayment();
  if (q.isPending) return <div className="loading">Cargando…</div>;
  if (q.error)
    return (
      <div className="errorbox" role="alert">
        {q.error instanceof ApiError ? q.error.title : 'No se pudieron cargar los pagos.'}
        <br />
        <button onClick={() => q.refetch()}>Reintentar</button>
      </div>
    );
  const rows = limit ? q.data.slice(0, limit) : q.data;
  if (rows.length === 0) return <div className="empty">{emptyText}</div>;
  // Lista y no tabla: entra completa en el modal de 480 px y en el celular, con la acción siempre visible.
  return (
    <>
      <ul className="paylist">
        {rows.map((p) => (
          <li key={p.id} className={p.reversedAt ? 'reversed' : undefined}>
            <div className="payrow">
              <span>
                <strong>{formatDate(p.date)}</strong> · desde {p.sourceAccountName ?? 'una cuenta archivada'}
              </span>
              <strong className="num">{formatMoney(p.amountMinor, p.currency)}</strong>
            </div>
            {p.allocations.map((a, i) => (
              <small key={`${a.installmentId ?? a.entryId ?? ''}-${i}`}>
                {describeAllocation(a)} · {formatMoney(a.amountMinor, p.currency)}
              </small>
            ))}
            <div className="payrow">
              <small>{p.note}</small>
              {p.reversedAt ? (
                <span className="status">Revertido el {formatDateTime(p.reversedAt)}</span>
              ) : (
                <button
                  className="delete"
                  title="Revertir pago"
                  aria-label={`Revertir pago del ${formatDate(p.date)} por ${formatMoney(p.amountMinor, p.currency)}`}
                  onClick={() => revert(p)}
                >Revertir</button>
              )}
            </div>
          </li>
        ))}
      </ul>
      {limit !== undefined && q.data.length > limit && <p className="hint">Se muestran los {limit} más recientes. «Ver historial» muestra todos, también los revertidos.</p>}
    </>
  );
}

/** Historial completo (incluye revertidos) de un plan, una cuota, un consumo con tarjeta o un tipo de pago. */
export function PaymentHistoryDialog({ target, onClose }: { target: PaymentHistoryTarget | null; onClose: () => void }) {
  return (
    <Modal open={target !== null} title={target?.title ?? ''} onClose={onClose}>
      {target && (
        <>
          <p className="hint">
            Revertir un pago devuelve lo que canceló a pendiente y el importe a la cuenta de origen. No se borra: queda en el historial como revertido.
            Para eliminar un plan o un gasto con pagos, primero revertí sus pagos.
          </p>
          <PaymentList filter={{ ...target.filter, includeReversed: true }} emptyText="No hay pagos para mostrar." />
        </>
      )}
    </Modal>
  );
}
