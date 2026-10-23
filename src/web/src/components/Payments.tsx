import { useState } from 'react';
import { useInfiniteQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { ApiError, api } from '../api/client';
import type { Page, Payment, PaymentFilter } from '../api/types';
import { formatDate, formatDateTime } from '../lib/dates';
import { formatMoney } from '../lib/money';
import { describeAllocation, loadedPayments, nextPaymentsPage, paymentsPath, type PaymentHistoryTarget } from '../lib/payments';
import { invalidateFinancial } from '../pages/queries';
import { Modal } from './Modal';
import { useUi, type UiValue } from './Ui';

/** Pagos por página en el historial (GET /payments acepta hasta 200; 50 es su valor por omisión). */
export const PAYMENTS_PAGE_SIZE = 50;

/** Cómo se informa el resultado de revertir `p`: toast en la página; dentro de un modal, un mensaje en el modal. */
export type PaymentNotice = (p: Payment, text: string, error?: boolean) => void;

function revertError(e: unknown): string {
  if (e instanceof ApiError && e.status === 404) return 'Ese pago ya fue revertido o no existe.';
  return e instanceof ApiError ? e.title : 'No se pudo revertir el pago.';
}

/** Lo que necesita una reversión: confirmación, caché a recargar y dónde informar el resultado. */
export interface RevertDeps {
  confirm: UiValue['confirm'];
  qc: QueryClient;
  report: PaymentNotice;
}

/**
 * Reversión explícita (DELETE con la versión que se ve en pantalla): lo cancelado vuelve a quedar pendiente y la cuenta
 * de origen recupera el importe. El servidor conserva el pago como revertido, con su auditoría.
 */
export async function revertPayment(p: Payment, { confirm, qc, report }: RevertDeps): Promise<void> {
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
    report(p, 'Pago revertido. Lo que canceló volvió a quedar pendiente.');
  } catch (e) {
    // 409 (versión vieja) o 404 (ya revertido): se recarga todo desde el servidor antes de reintentar.
    await invalidateFinancial(qc);
    report(p, revertError(e), true);
  }
}

/**
 * `notify` reemplaza al toast: con un modal abierto el toast queda detrás del fondo del modal y fuera de él todo es
 * inerte (no se ve ni se anuncia), así que el historial muestra el resultado dentro del propio modal.
 */
export function useRevertPayment(notify?: PaymentNotice) {
  const qc = useQueryClient();
  const { confirm, toast } = useUi();
  const report: PaymentNotice = notify ?? ((_p, text, error) => toast(text, error ? { error: true } : undefined));
  return (p: Payment): Promise<void> => revertPayment(p, { confirm, qc, report });
}

/**
 * Pagos con fecha, cuenta de origen, importe y lo que cancelaron; cada pago vigente se puede revertir.
 * GET /payments es paginado: con `limit` se pide solo esa cantidad (los más recientes) y se avisa cuántos hay en total;
 * sin `limit` se cargan de a PAYMENTS_PAGE_SIZE con «Ver más», así se llega a todos.
 * `inline`: el resultado de revertir se muestra en la lista, junto al pago (para usarla dentro de un modal).
 */
export function PaymentList({ filter, limit, emptyText = 'No hay pagos registrados.', inline = false }: {
  filter: PaymentFilter;
  limit?: number;
  emptyText?: string;
  inline?: boolean;
}) {
  const pageSize = limit ?? PAYMENTS_PAGE_SIZE;
  const q = useInfiniteQuery({
    queryKey: ['payments', filter, pageSize],
    queryFn: ({ pageParam, signal }) => api<Page<Payment>>('GET', paymentsPath(filter, { page: pageParam, pageSize }), undefined, { signal }),
    initialPageParam: 1,
    getNextPageParam: nextPaymentsPage,
  });
  const [notice, setNotice] = useState<RevertNotice | null>(null);
  const revert = useRevertPayment(
    inline ? (p, text, error) => setNotice((prev) => ({ paymentId: p.id, text, error: !!error, n: (prev?.n ?? 0) + 1 })) : undefined,
  );

  if (!q.data) {
    if (q.isPending) return <div className="loading">Cargando…</div>;
    return (
      <div className="errorbox" role="alert">
        {q.error instanceof ApiError ? q.error.title : 'No se pudieron cargar los pagos.'}
        <br />
        <button type="button" onClick={() => q.refetch()}>Reintentar</button>
      </div>
    );
  }

  const rows = loadedPayments(q.data.pages);
  const total = q.data.pages[q.data.pages.length - 1]?.total ?? rows.length;
  // El pedido de la página siguiente falló: lo cargado sigue bien. Si falló una recarga, lo que se ve puede estar viejo.
  const moreFailed = q.isFetchNextPageError;
  // Junto al pago revertido; si ya no está en la lista, arriba de todo. Nunca se pierde.
  const orphanNotice = notice && !rows.some((p) => p.id === notice.paymentId) ? notice : null;
  return (
    <>
      <RevertNoticeText notice={orphanNotice} />
      {q.error && (
        <div className="notice danger" role="alert">
          {q.error instanceof ApiError ? q.error.title : 'No se pudieron cargar los pagos.'}{' '}
          {moreFailed ? 'No se cargaron más pagos.' : 'Lo que ves puede estar desactualizado.'}{' '}
          <button type="button" className="linkbtn" disabled={q.isFetching} onClick={() => (moreFailed ? q.fetchNextPage() : q.refetch())}>
            {q.isFetching ? 'Reintentando…' : 'Reintentar'}
          </button>
        </div>
      )}
      {rows.length === 0 ? (
        <div className="empty">{emptyText}</div>
      ) : (
        // Lista y no tabla: entra completa en el modal de 480 px y en el celular, con la acción siempre visible.
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
                    type="button"
                    className="delete"
                    title="Revertir pago"
                    aria-label={`Revertir pago del ${formatDate(p.date)} por ${formatMoney(p.amountMinor, p.currency)}`}
                    onClick={() => revert(p)}
                  >Revertir</button>
                )}
              </div>
              <RevertNoticeText notice={notice?.paymentId === p.id ? notice : null} />
            </li>
          ))}
        </ul>
      )}
      {limit !== undefined
        ? total > rows.length && <p className="hint">Se muestran los {rows.length} más recientes de {total}. En «Ver historial» se pueden recorrer todos, también los revertidos.</p>
        : rows.length > 0 && total > rows.length && (
          <div className="pager">
            <span>{rows.length} de {total} pagos</span>
            <div>
              <button type="button" disabled={!q.hasNextPage || q.isFetchingNextPage} onClick={() => q.fetchNextPage()}>
                {q.isFetchingNextPage ? 'Cargando…' : 'Ver más'}
              </button>
            </div>
          </div>
        )}
    </>
  );
}

interface RevertNotice {
  paymentId: string;
  text: string;
  error: boolean;
  /** Cambia en cada aviso: el elemento se vuelve a insertar y el lector de pantalla lo anuncia aunque el texto se repita. */
  n: number;
}

function RevertNoticeText({ notice }: { notice: RevertNotice | null }) {
  if (!notice) return null;
  return <p key={notice.n} className={notice.error ? 'formerror' : 'hint'} role={notice.error ? 'alert' : 'status'}>{notice.text}</p>;
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
          <PaymentList filter={{ ...target.filter, includeReversed: true }} emptyText="No hay pagos para mostrar." inline />
        </>
      )}
    </Modal>
  );
}
