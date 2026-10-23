import { useEffect, useMemo, useState } from 'react';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, idempotencyKey } from '../api/client';
import type { Obligation, Occurrence, Page, Payment, Plan } from '../api/types';
import { FieldError, Modal } from '../components/Modal';
import { PaymentHistoryDialog, PaymentList } from '../components/Payments';
import { OccurrenceTable, QueryState } from '../components/Records';
import { useUi } from '../components/Ui';
import { formatDate, todayIso } from '../lib/dates';
import { formatMoney, minorToInput, parseAmountInput, type Currency } from '../lib/money';
import { isPaymentsApplied, paymentsOfPlan, type PaymentHistoryTarget } from '../lib/payments';
import { invalidateFinancial, useAccounts } from './queries';

type PayTarget = { planId?: string; accountId?: string } | null;

/** Igual que Movimientos: las cuotas del mes se paginan en el servidor (antes se pedían 100 y el resto se perdía). */
const PAGE_SIZE = 25;
const RECENT_PAYMENTS = 8;

export function PlansPage({ kind }: { kind: 'card' | 'loan' }) {
  const { month, currency, openRecord, confirm, toast } = useUi();
  const qc = useQueryClient();
  const [payTarget, setPayTarget] = useState<PayTarget>(null);
  const [history, setHistory] = useState<PaymentHistoryTarget | null>(null);
  const [page, setPage] = useState(1);
  const title = kind === 'card' ? 'Tarjetas y cuotas' : 'Préstamos';
  useEffect(() => setPage(1), [kind, month, currency]);

  const monthRows = useQuery({
    queryKey: ['movements', month, currency, '', kind, page, PAGE_SIZE],
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ month, currency, type: kind, page: String(page), pageSize: String(PAGE_SIZE) });
      return api<Page<Occurrence>>('GET', `/movements?${params}`, undefined, { signal });
    },
    placeholderData: keepPreviousData,
  });
  const pages = monthRows.data ? Math.max(1, Math.ceil(monthRows.data.total / PAGE_SIZE)) : 1;
  const plans = useQuery({
    queryKey: ['plans', kind, currency],
    queryFn: ({ signal }) => api<Plan[]>('GET', `/plans?kind=${kind}&currency=${currency}`, undefined, { signal }),
  });

  const removePlan = async (p: Plan) => {
    const ok = await confirm({
      title: 'Eliminar plan',
      message: <>Se eliminará «{p.concept}» con sus {p.count} cuotas. Podés restaurarlo desde la papelera en Configuración.</>,
      confirmLabel: 'Eliminar',
      danger: true,
    });
    if (!ok) return;
    try {
      const deleted = await api<Plan>('DELETE', `/plans/${p.id}?version=${p.version}`);
      await invalidateFinancial(qc);
      toast('Plan eliminado', {
        action: {
          label: 'Deshacer',
          run: async () => {
            try {
              await api('POST', `/plans/${p.id}/restore?version=${deleted.version}`);
              await invalidateFinancial(qc);
              toast('Plan restaurado');
            } catch (err) {
              toast(err instanceof ApiError ? err.title : 'No se pudo restaurar.', { error: true });
            }
          },
        },
      });
    } catch (e) {
      // Con pagos aplicados el aviso lleva a esos pagos, donde se pueden revertir.
      if (isPaymentsApplied(e)) toast(e.title, { error: true, action: { label: 'Ver pagos', run: () => setHistory(paymentsOfPlan(p.id, p.concept)) } });
      else toast(e instanceof ApiError ? e.title : 'No se pudo eliminar.', { error: true });
      await invalidateFinancial(qc);
    }
  };

  return (
    <>
      <div className="notice">
        Se muestran las cuotas correspondientes al mes seleccionado. Usá Proyección para ver los meses siguientes. La primera cuota determina el mes inicial; no se calcula el cierre de tarjeta automáticamente.
        {kind === 'card' && ' Pagar el resumen cancela cuotas pendientes: no se carga como otro gasto.'}
      </div>
      <section className="panel">
        <div className="panelhead">
          <h2>{title}</h2>
          <div className="tools">
            <button onClick={() => setPayTarget({})}>Registrar pago</button>
            <button onClick={() => openRecord({ mode: 'create', type: kind })}>＋ Agregar</button>
          </div>
        </div>
        {monthRows.data ? (
          <>
            <OccurrenceTable rows={monthRows.data.items} currency={currency} />
            {monthRows.data.total > PAGE_SIZE && (
              <div className="pager">
                <span>{monthRows.data.total} cuotas · página {page} de {pages}</span>
                <div>
                  <button disabled={page <= 1} onClick={() => setPage((n) => n - 1)}>‹ Anterior</button>
                  <button disabled={page >= pages} onClick={() => setPage((n) => n + 1)}>Siguiente ›</button>
                </div>
              </div>
            )}
          </>
        ) : (
          <QueryState isPending={monthRows.isPending} error={monthRows.error} onRetry={() => monthRows.refetch()} />
        )}
      </section>
      <section className="panel wide">
        <div className="panelhead">
          <div>
            <h2>{kind === 'card' ? 'Planes de cuotas' : 'Planes de préstamos'}</h2>
            <p>Cuotas actuales y restantes, próxima fecha, pagado y pendiente ({currency}).</p>
          </div>
        </div>
        {plans.data ? (
          plans.data.length === 0 ? (
            <div className="empty">Todavía no hay planes en {currency}.</div>
          ) : (
            <div className="tablewrap">
              <table>
                <thead>
                  <tr>
                    <th>Concepto</th>
                    <th>Cuenta / tarjeta</th>
                    <th>Cuota actual</th>
                    <th>Próximo vencimiento</th>
                    <th className="num">Pagado</th>
                    <th className="num">Pendiente</th>
                    <th className="num">Total</th>
                    <th><span className="sr-only">Acciones</span></th>
                  </tr>
                </thead>
                <tbody>
                  {plans.data.map((p) => (
                    <tr key={p.id}>
                      <td>
                        {p.concept}
                        <small>
                          {p.count} cuotas · desde {formatDate(p.firstDueDate)}
                          {p.paidBeforeCount > 0 && ` · ${p.paidBeforeCount} pagadas antes de registrar`}
                          {p.remainingOnly && ' · solo cuotas restantes'}
                          {p.disbursementMinor && ` · capital recibido ${formatMoney(p.disbursementMinor, p.currency)}`}
                        </small>
                      </td>
                      <td>{p.accountName ?? 'Sin especificar'}</td>
                      <td>{p.currentInstallment > 0 ? `${p.currentInstallment} de ${p.count}` : 'Aún no empezó'}<small>{p.remainingInstallments} restantes</small></td>
                      <td>{p.nextDue ? <>{formatDate(p.nextDue.dueDate)}<small>Cuota {p.nextDue.number} · {formatMoney(p.nextDue.pendingMinor, p.currency)}</small></> : <span className="green">Finalizado</span>}</td>
                      <td className="num">{formatMoney(p.paidMinor, p.currency)}</td>
                      <td className="num">{formatMoney(p.pendingMinor, p.currency)}</td>
                      <td className="num">{formatMoney(p.totalMinor, p.currency)}</td>
                      <td>
                        <div className="rowactions">
                          {!p.finished && <button className="delete" title="Registrar pago" aria-label={`Registrar pago de ${p.concept}`} onClick={() => setPayTarget({ planId: p.id })}>$</button>}
                          {p.hasPayments && <button className="delete" title="Ver pagos" aria-label={`Ver pagos de ${p.concept}`} onClick={() => setHistory(paymentsOfPlan(p.id, p.concept))}>≡</button>}
                          <button className="delete" title="Editar" aria-label={`Editar ${p.concept}`} onClick={() => openRecord({ mode: 'edit-plan', id: p.id })}>✎</button>
                          <button className="delete" title="Eliminar" aria-label={`Eliminar ${p.concept}`} onClick={() => removePlan(p)}>×</button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        ) : (
          <QueryState isPending={plans.isPending} error={plans.error} onRetry={() => plans.refetch()} />
        )}
      </section>
      <section className="panel stacked" id="pagos">
        <div className="panelhead">
          <div>
            <h2>Pagos registrados</h2>
            <p>{kind === 'card' ? 'Pagos de resúmenes, cuotas y consumos con tarjeta' : 'Pagos de cuotas de préstamos'} en {currency}. Revertir un pago devuelve lo que canceló a pendiente.</p>
          </div>
          <div className="tools">
            <button onClick={() => setHistory({ title: kind === 'card' ? 'Historial de pagos de tarjetas' : 'Historial de pagos de préstamos', filter: { kind, currency } })}>Ver historial</button>
          </div>
        </div>
        <PaymentList filter={{ kind, currency }} limit={RECENT_PAYMENTS} emptyText={`Todavía no hay pagos registrados en ${currency}.`} />
      </section>
      <PaymentDialog kind={kind} currency={currency} target={payTarget} plans={plans.data ?? []} onClose={() => setPayTarget(null)} />
      <PaymentHistoryDialog target={history} onClose={() => setHistory(null)} />
    </>
  );
}

/**
 * Pago de tarjeta o cuota: mueve caja y cancela obligaciones, sin crear otro gasto.
 * La clave de idempotencia se genera al abrir: reintentos o doble clic no duplican el pago.
 */
function PaymentDialog({ kind, currency, target, plans, onClose }: { kind: 'card' | 'loan'; currency: Currency; target: PayTarget; plans: Plan[]; onClose: () => void }) {
  const qc = useQueryClient();
  const { toast } = useUi();
  const accounts = useAccounts();
  const open = target !== null;
  const [key, setKey] = useState(idempotencyKey);
  const [source, setSource] = useState('');
  const [what, setWhat] = useState('');
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(todayIso());
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const cards = (accounts.data ?? []).filter((a) => a.kind === 'credit_card' && a.currency === currency);
  const sources = (accounts.data ?? []).filter((a) => a.kind !== 'credit_card' && a.currency === currency);
  const activePlans = plans.filter((p) => !p.finished);

  useEffect(() => {
    if (!open) return;
    setKey(idempotencyKey());
    setWhat(target?.planId ? `plan:${target.planId}` : target?.accountId ? `card:${target.accountId}` : '');
    setAmount('');
    setDate(todayIso());
    setErrors({});
    setFormError(null);
  }, [open, target]);

  const [whatType, whatId] = what.split(':') as ['plan' | 'card' | '', string | undefined];
  const obligations = useQuery({
    queryKey: ['obligations', what, currency],
    queryFn: () => api<Obligation[]>('GET', `/obligations?${whatType === 'plan' ? 'planId' : 'accountId'}=${whatId}&currency=${currency}`),
    enabled: open && !!whatId,
  });
  const pendingTotal = useMemo(() => (obligations.data ?? []).reduce((s, o) => s + BigInt(o.pendingMinor), 0n), [obligations.data]);
  useEffect(() => {
    const first = obligations.data?.[0];
    if (first && !amount) setAmount(minorToInput(first.pendingMinor));
  }, [obligations.data]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const parsed = parseAmountInput(amount);
    const errs: Record<string, string[]> = {};
    if (!source) errs.sourceAccountId = ['Elegí la cuenta desde la que pagás.'];
    if (!whatId) errs.targetAccountId = ['Elegí qué pagás.'];
    if (!parsed.ok) errs.amountMinor = [parsed.error];
    else if (BigInt(parsed.minor) > pendingTotal) errs.amountMinor = ['El importe supera lo pendiente.'];
    setErrors(errs);
    if (Object.keys(errs).length || !parsed.ok) return;
    setBusy(true);
    setFormError(null);
    try {
      const created = await api<Payment>('POST', '/payments', {
        sourceAccountId: source,
        date,
        currency,
        amountMinor: parsed.minor,
        ...(whatType === 'plan' ? { planId: whatId } : { targetAccountId: whatId }),
      }, { headers: { 'Idempotency-Key': key } });
      await invalidateFinancial(qc);
      toast('Pago registrado', {
        action: {
          label: 'Deshacer',
          run: async () => {
            try {
              await api('DELETE', `/payments/${created.id}?version=${created.version}`);
              await invalidateFinancial(qc);
              toast('Pago revertido');
            } catch (err) {
              await invalidateFinancial(qc);
              toast(err instanceof ApiError ? err.title : 'No se pudo revertir el pago.', { error: true });
            }
          },
        },
      });
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(err.fieldErrors);
        setFormError(err.title);
      } else setFormError('No se pudo registrar el pago.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open={open} title="Registrar pago" onClose={onClose} busy={busy}>
      <form onSubmit={submit} noValidate>
        <p className="hint">El pago reduce el saldo de la cuenta y lo pendiente de las cuotas. No es un gasto nuevo: las compras ya se cuentan en su mes.</p>
        <label>
          Qué pagás
          <select value={what} onChange={(e) => { setWhat(e.target.value); setAmount(''); }} aria-invalid={errors.targetAccountId ? true : undefined}>
            <option value="">Elegí…</option>
            {kind === 'card' && cards.length > 0 && (
              <optgroup label="Resumen de tarjeta (cuotas y consumos)">
                {cards.map((c) => <option key={c.id} value={`card:${c.id}`}>{c.name}</option>)}
              </optgroup>
            )}
            <optgroup label="Un plan puntual">
              {activePlans.map((p) => <option key={p.id} value={`plan:${p.id}`}>{p.concept}</option>)}
            </optgroup>
          </select>
          <FieldError id="err-target" errors={errors.targetAccountId ?? errors.planId} />
        </label>
        {whatId && obligations.data && (
          <div className="preview">
            Pendiente total: <strong>{formatMoney(pendingTotal, currency)}</strong>
            {obligations.data[0] && <> · próxima: {obligations.data[0].concept} {obligations.data[0].number ? `(cuota ${obligations.data[0].number} de ${obligations.data[0].count})` : ''} vence {formatDate(obligations.data[0].dueDate)}</>}
            <br />Se aplica primero a lo más antiguo. Un pago parcial deja el resto pendiente.
          </div>
        )}
        <label>
          Desde la cuenta
          <select value={source} onChange={(e) => setSource(e.target.value)} aria-invalid={errors.sourceAccountId ? true : undefined}>
            <option value="">Elegí…</option>
            {sources.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
          <FieldError id="err-source" errors={errors.sourceAccountId} />
        </label>
        <div className="fields">
          <label>
            Importe ({currency})
            <input inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={errors.amountMinor ? true : undefined} />
            <FieldError id="err-amount" errors={errors.amountMinor} />
          </label>
          <label>
            Fecha
            <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </label>
        </div>
        {formError && <p className="formerror" role="alert">{formError}</p>}
        <button className="primary full" disabled={busy}>{busy ? 'Registrando…' : 'Registrar pago'}</button>
      </form>
    </Modal>
  );
}
