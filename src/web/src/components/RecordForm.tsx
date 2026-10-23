import { useEffect, useId, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api } from '../api/client';
import type { Account, AccountKind, Category, Entry, Plan } from '../api/types';
import { distribute, formatMoney, minorToInput, parseAmountInput, type Currency } from '../lib/money';
import { formatDate, todayIso } from '../lib/dates';
import { mapFieldErrors, type FieldErrors } from '../lib/forms';
import { Modal, useFieldErrors } from './Modal';
import { useUi } from './Ui';
import { invalidateFinancial, useAccounts, useCategories } from '../pages/queries';

export type RecordType = 'expense' | 'income' | 'card' | 'loan';
export type RecordTarget =
  | { mode: 'create'; type?: RecordType }
  | { mode: 'edit-entry'; id: string }
  | { mode: 'edit-plan'; id: string };

const NEW_ACCOUNT = '__new__';
const isPlan = (t: RecordType) => t === 'card' || t === 'loan';

export interface FormState {
  type: RecordType;
  concept: string;
  amount: string;
  amountMode: 'total' | 'installment';
  currency: Currency;
  date: string;
  categoryId: string;
  accountId: string;
  newAccountName: string;
  newAccountKind: AccountKind;
  count: string;
  ongoing: 'none' | 'paid' | 'remaining';
  paidBefore: string;
  disbursement: string;
  disbursementDate: string;
  disbursementAccountId: string;
  note: string;
  version: number | null;
}

export const empty = (type: RecordType, currency: Currency): FormState => ({
  type,
  concept: '',
  amount: '',
  amountMode: 'total',
  currency,
  date: todayIso(),
  categoryId: '',
  accountId: '',
  newAccountName: '',
  newAccountKind: type === 'card' ? 'credit_card' : 'bank',
  count: type === 'card' || type === 'loan' ? '6' : '1',
  ongoing: 'none',
  paidBefore: '',
  disbursement: '',
  disbursementDate: todayIso(),
  disbursementAccountId: '',
  note: '',
  version: null,
});

const fromEntry = (e: Entry): FormState => ({
  ...empty(e.kind, e.currency),
  concept: e.concept,
  amount: minorToInput(e.amountMinor),
  date: e.date,
  categoryId: e.categoryId ?? '',
  accountId: e.accountId ?? '',
  note: e.note ?? '',
  version: e.version,
});

const fromPlan = (p: Plan): FormState => ({
  ...empty(p.kind, p.currency),
  concept: p.concept,
  amount: minorToInput(p.totalMinor),
  count: String(p.count),
  date: p.firstDueDate,
  categoryId: p.categoryId ?? '',
  accountId: p.accountId ?? '',
  ongoing: p.paidBeforeCount > 0 ? 'paid' : p.remainingOnly ? 'remaining' : 'none',
  paidBefore: p.paidBeforeCount > 0 ? String(p.paidBeforeCount) : '',
  disbursement: p.disbursementMinor ? minorToInput(p.disbursementMinor) : '',
  disbursementDate: p.disbursementDate ?? todayIso(),
  disbursementAccountId: p.disbursementAccountId ?? '',
  note: p.note ?? '',
  version: p.version,
});

/** Claves de validación del servidor (POST/PATCH /entries y /plans) → campo del formulario. */
export const RECORD_FIELD_MAP: Readonly<Partial<Record<string, keyof FormState>>> = {
  kind: 'type', concept: 'concept', amountMinor: 'amount', currency: 'currency', date: 'date', firstDueDate: 'date',
  accountId: 'accountId', categoryId: 'categoryId', count: 'count', amountMode: 'amountMode', paidBeforeCount: 'paidBefore',
  remainingOnly: 'ongoing', disbursementMinor: 'disbursement', disbursementDate: 'disbursementDate', disbursementAccountId: 'disbursementAccountId',
  note: 'note',
};

/** Alta de la cuenta nueva desde el formulario (POST /accounts): sus claves son de la cuenta, no del registro. */
export const NEW_ACCOUNT_FIELD_MAP: Readonly<Partial<Record<string, keyof FormState>>> = {
  name: 'newAccountName', kind: 'newAccountKind', currency: 'currency',
};

/** Campos que el formulario muestra en este estado: un error de un campo oculto se muestra como error general, nunca se pierde. */
export function visibleFields(f: FormState): Set<keyof FormState> {
  const v = new Set<keyof FormState>(['type', 'concept', 'amount', 'currency', 'date', 'categoryId', 'accountId', 'note']);
  if (f.accountId === NEW_ACCOUNT) v.add('newAccountName').add('newAccountKind');
  if (isPlan(f.type)) {
    v.add('amountMode').add('count').add('ongoing');
    if (f.ongoing === 'paid') v.add('paidBefore');
    if (f.type === 'loan') v.add('disbursement').add('disbursementDate').add('disbursementAccountId');
  }
  return v;
}

const ONGOING_FIELDS: (keyof FormState)[] = ['ongoing', 'paidBefore', 'disbursement', 'disbursementDate', 'disbursementAccountId'];

export function RecordDialog({ target, onClose }: { target: RecordTarget | null; onClose: () => void }) {
  const { currency } = useUi();
  const open = target !== null;
  const editId = target && target.mode !== 'create' ? target.id : null;
  const entryQuery = useQuery({
    queryKey: ['entry', editId],
    queryFn: () => api<Entry>('GET', `/entries/${editId}`),
    enabled: target?.mode === 'edit-entry',
    staleTime: 0,
  });
  const planQuery = useQuery({
    queryKey: ['plan', editId],
    queryFn: () => api<Plan>('GET', `/plans/${editId}`),
    enabled: target?.mode === 'edit-plan',
    staleTime: 0,
  });

  const title = target?.mode === 'create' ? 'Nuevo registro' : 'Editar registro';
  let body: React.ReactNode = null;
  if (target?.mode === 'create') {
    body = <RecordFormBody key={`new-${target.type ?? 'expense'}`} initial={empty(target.type ?? 'expense', currency)} onClose={onClose} />;
  } else if (target?.mode === 'edit-entry') {
    body = entryQuery.isPending ? <div className="loading">Cargando…</div>
      : entryQuery.isError ? <div className="errorbox">{(entryQuery.error as ApiError).title}</div>
      : <RecordFormBody key={`e-${entryQuery.data.id}-${entryQuery.data.version}`} initial={fromEntry(entryQuery.data)} editId={entryQuery.data.id} onClose={onClose} reload={() => entryQuery.refetch()} />;
  } else if (target?.mode === 'edit-plan') {
    body = planQuery.isPending ? <div className="loading">Cargando…</div>
      : planQuery.isError ? <div className="errorbox">{(planQuery.error as ApiError).title}</div>
      : <RecordFormBody key={`p-${planQuery.data.id}-${planQuery.data.version}`} initial={fromPlan(planQuery.data)} editId={planQuery.data.id} plan={planQuery.data} onClose={onClose} reload={() => planQuery.refetch()} />;
  }

  return (
    <Modal open={open} title={title} onClose={onClose}>
      {body}
    </Modal>
  );
}

export function RecordFormBody({ initial, editId, plan, onClose, reload, initialErrors = {} }: {
  initial: FormState;
  editId?: string;
  plan?: Plan;
  onClose: () => void;
  reload?: () => void;
  /** Errores de campo con los que arranca el formulario (se usa en las pruebas de accesibilidad). */
  initialErrors?: FieldErrors<keyof FormState>;
}) {
  const qc = useQueryClient();
  const { toast } = useUi();
  const [f, setF] = useState<FormState>(initial);
  const [errors, setErrors] = useState<FieldErrors<keyof FormState>>(initialErrors);
  const [formError, setFormError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const accounts = useAccounts();
  const categories = useCategories();
  const plan_ = isPlan(f.type);
  const scheduleLocked = !!plan?.hasPayments;

  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setF((s) => ({ ...s, [k]: v }));

  const accountOptions = (accounts.data ?? []).filter((a) => a.currency === f.currency && !(f.type === 'income' && a.kind === 'credit_card'));
  const cashAccounts = (accounts.data ?? []).filter((a) => a.currency === f.currency && a.kind !== 'credit_card');

  // Vista previa de la distribución: mismo algoritmo exacto que el servidor (que igual valida).
  const preview = useMemo(() => {
    if (!plan_) return null;
    const amount = parseAmountInput(f.amount);
    const count = Number(f.count);
    if (!amount.ok || !Number.isInteger(count) || count < 1 || count > 360) return null;
    const total = f.amountMode === 'installment' ? BigInt(amount.minor) * BigInt(count) : BigInt(amount.minor);
    if (BigInt(count) > total) return { error: 'Hay más cuotas que centavos en el total.' };
    const parts = distribute(total, count);
    return { total, parts, rest: total % BigInt(count) };
  }, [plan_, f.amount, f.amountMode, f.count]);

  const validate = (): boolean => {
    const e: FieldErrors<keyof FormState> = {};
    if (!f.concept.trim()) e.concept = ['El concepto es obligatorio.'];
    const amount = parseAmountInput(f.amount);
    if (!amount.ok) e.amount = [amount.error];
    if (!f.date) e.date = ['La fecha no es válida.'];
    if (f.accountId === NEW_ACCOUNT && !f.newAccountName.trim()) e.newAccountName = ['Poné un nombre a la cuenta.'];
    if (plan_) {
      const count = Number(f.count);
      if (!Number.isInteger(count) || count < 1 || count > 360) e.count = ['Entre 1 y 360 cuotas.'];
      if (preview && 'error' in preview) e.count = [preview.error ?? ''];
      if (f.ongoing === 'paid') {
        const pb = Number(f.paidBefore);
        if (!Number.isInteger(pb) || pb < 1 || pb >= count) e.paidBefore = ['Debe ser menor que la cantidad de cuotas.'];
      }
      if (f.type === 'loan' && f.disbursement) {
        const d = parseAmountInput(f.disbursement);
        if (!d.ok) e.disbursement = [d.error];
        if (!f.disbursementDate) e.disbursementDate = ['Indicá la fecha de acreditación.'];
      }
    }
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const submit = async (ev: React.FormEvent) => {
    ev.preventDefault();
    if (saving) return;
    setFormError(null);
    setConflict(false);
    if (!validate()) return;
    setSaving(true);
    let fieldMap = RECORD_FIELD_MAP;
    try {
      let accountId: string | null = f.accountId || null;
      if (accountId === NEW_ACCOUNT) {
        fieldMap = NEW_ACCOUNT_FIELD_MAP;
        const created = await api<Account>('POST', '/accounts', { name: f.newAccountName.trim(), kind: f.newAccountKind, currency: f.currency });
        fieldMap = RECORD_FIELD_MAP;
        accountId = created.id;
        await qc.invalidateQueries({ queryKey: ['accounts'] });
        setF((s) => ({ ...s, accountId: created.id }));
      }
      const amountMinor = (parseAmountInput(f.amount) as { minor: string }).minor;
      const common = {
        concept: f.concept.trim(),
        currency: f.currency,
        accountId,
        categoryId: f.categoryId || null,
        note: f.note.trim() || null,
      };
      if (plan_) {
        const body = {
          ...common,
          kind: f.type,
          amountMode: f.amountMode,
          amountMinor,
          count: Number(f.count),
          firstDueDate: f.date,
          paidBeforeCount: f.ongoing === 'paid' ? Number(f.paidBefore) : 0,
          remainingOnly: f.ongoing === 'remaining',
          disbursementMinor: f.type === 'loan' && f.disbursement ? (parseAmountInput(f.disbursement) as { minor: string }).minor : null,
          disbursementDate: f.type === 'loan' && f.disbursement ? f.disbursementDate : null,
          disbursementAccountId: f.type === 'loan' && f.disbursement ? f.disbursementAccountId || null : null,
          ...(editId ? { version: f.version } : {}),
        };
        if (editId) await api('PATCH', `/plans/${editId}`, body);
        else await api('POST', '/plans', body);
      } else {
        const body = { ...common, kind: f.type, amountMinor, date: f.date, ...(editId ? { version: f.version } : {}) };
        if (editId) await api('PATCH', `/entries/${editId}`, body);
        else await api('POST', '/entries', body);
      }
      await invalidateFinancial(qc);
      toast(editId ? 'Cambios guardados' : 'Registro guardado');
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.status === 409) {
          setConflict(true);
          // Las listas pueden tener la versión vieja: se recargan desde el servidor.
          // El formulario abierto NO se recarga solo: la persona decide si carga la versión actual.
          void qc.invalidateQueries({ predicate: (q) => !['me', 'entry', 'plan'].includes(String(q.queryKey[0])) });
        }
        // Cada mensaje va junto a su campo; las claves sin campo (p. ej. version) se suman al mensaje general.
        const { fields, other } = mapFieldErrors(err.fieldErrors, fieldMap);
        setErrors(fields);
        setFormError([err.title, ...other].join(' '));
      } else {
        setFormError('No se pudo guardar. Probá de nuevo.');
      }
    } finally {
      setSaving(false);
    }
  };

  const fe = useFieldErrors(errors);
  const fieldProps = fe.props;
  const visible = visibleFields(f);
  const hiddenErrors = Object.entries(errors).filter(([k]) => !visible.has(k as keyof FormState)).flatMap(([, v]) => v ?? []);
  const generalError = [formError, ...hiddenErrors].filter(Boolean).join(' ');

  const hint = plan_
    ? 'El total se divide en cuotas mensuales exactas. Para préstamos, incluí los intereses en el total. El desembolso recibido se registra aparte y no cuenta como ingreso del mes.'
    : 'No vuelvas a cargar el pago de la tarjeta si ya registraste sus compras. Las transferencias entre cuentas propias no son ingresos ni gastos.';

  return (
    <form onSubmit={submit} noValidate>
      {conflict && (
        <div className="notice warn" role="alert">
          Este registro cambió desde que lo abriste (otra pestaña u otro dispositivo). No se guardó nada.
          {reload && <> <button type="button" className="linkbtn" onClick={reload}>Cargar la versión actual</button></>}
        </div>
      )}
      <label>
        Tipo
        <select value={f.type} disabled={!!editId} onChange={(e) => setF((s) => ({ ...empty(e.target.value as RecordType, s.currency), concept: s.concept, amount: s.amount, date: s.date, categoryId: s.categoryId }))} {...fieldProps('type')}>
          <option value="expense">Gasto</option>
          <option value="income">Ingreso</option>
          <option value="card">Compra en cuotas</option>
          <option value="loan">Plan de cuotas de préstamo</option>
        </select>
        {fe.error('type')}
      </label>
      <label>
        Concepto
        <input value={f.concept} onChange={(e) => set('concept', e.target.value)} required maxLength={90} placeholder="Ej. Supermercado, sueldo, monitor…" {...fieldProps('concept')} />
        {fe.error('concept')}
      </label>
      {plan_ && (
        <>
          <div className="choice" role="radiogroup" aria-label="El importe ingresado es" {...fieldProps('amountMode')}>
            <label><input type="radio" name="amountMode" checked={f.amountMode === 'total'} disabled={scheduleLocked} onChange={() => set('amountMode', 'total')} />Total de todas las cuotas</label>
            <label><input type="radio" name="amountMode" checked={f.amountMode === 'installment'} disabled={scheduleLocked} onChange={() => set('amountMode', 'installment')} />Cuota mensual</label>
          </div>
          {fe.error('amountMode')}
        </>
      )}
      <div className="fields">
        <label>
          <span>{plan_ ? (f.amountMode === 'total' ? 'Total de todas las cuotas' : 'Importe de cada cuota') : 'Importe'}</span>
          <input inputMode="decimal" autoComplete="off" value={f.amount} disabled={scheduleLocked} onChange={(e) => set('amount', e.target.value)} required placeholder="0,00" {...fieldProps('amount')} />
          {fe.error('amount')}
        </label>
        <label>
          Moneda
          <select value={f.currency} disabled={scheduleLocked} onChange={(e) => setF((s) => ({ ...s, currency: e.target.value as Currency, accountId: '', disbursementAccountId: '' }))} {...fieldProps('currency')}>
            <option>ARS</option>
            <option>USD</option>
          </select>
          {fe.error('currency')}
        </label>
      </div>
      <div className="fields">
        <label>
          <span>{plan_ ? 'Fecha de primera cuota' : 'Fecha'}</span>
          <input type="date" value={f.date} disabled={scheduleLocked} onChange={(e) => set('date', e.target.value)} required {...fieldProps('date')} />
          {fe.error('date')}
        </label>
        <label>
          Categoría
          <select value={f.categoryId} onChange={(e) => set('categoryId', e.target.value)} {...fieldProps('categoryId')}>
            <option value="">Sin categoría</option>
            {(categories.data ?? []).map((c: Category) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
          {fe.error('categoryId')}
        </label>
      </div>
      <label>
        Cuenta / tarjeta
        <select value={f.accountId} onChange={(e) => set('accountId', e.target.value)} {...fieldProps('accountId')}>
          <option value="">Sin especificar</option>
          {accountOptions.map((a) => <option key={a.id} value={a.id}>{a.name}{a.kind === 'credit_card' ? ' (tarjeta)' : ''}</option>)}
          <option value={NEW_ACCOUNT}>＋ Nueva cuenta…</option>
        </select>
        {fe.error('accountId')}
      </label>
      {f.accountId === NEW_ACCOUNT && (
        <div className="fields">
          <label>
            Nombre de la cuenta
            <input value={f.newAccountName} maxLength={60} onChange={(e) => set('newAccountName', e.target.value)} placeholder="Ej. Efectivo, Mercado Pago, Visa" {...fieldProps('newAccountName')} />
            {fe.error('newAccountName')}
          </label>
          <label>
            Tipo de cuenta
            <select value={f.newAccountKind} onChange={(e) => set('newAccountKind', e.target.value as AccountKind)} {...fieldProps('newAccountKind')}>
              <option value="cash">Efectivo</option>
              <option value="bank">Banco</option>
              <option value="wallet">Billetera virtual</option>
              {f.type !== 'income' && <option value="credit_card">Tarjeta de crédito</option>}
            </select>
            {fe.error('newAccountKind')}
          </label>
        </div>
      )}
      {plan_ && (
        <label>
          Cantidad de cuotas
          <input type="number" min={1} max={360} value={f.count} disabled={scheduleLocked} onChange={(e) => set('count', e.target.value)} {...fieldProps('count')} />
          {fe.error('count')}
        </label>
      )}
      {plan_ && preview && !('error' in preview) && preview.parts && (
        <div className="preview" aria-live="polite">
          <strong>Total calculado: {formatMoney(preview.total, f.currency)}</strong> en {preview.parts.length} cuota{preview.parts.length === 1 ? '' : 's'}.
          <br />
          {preview.rest > 0n
            ? <>Las primeras {preview.rest.toString()} cuotas son de {formatMoney(preview.parts[0]!, f.currency)} y el resto de {formatMoney(preview.parts[preview.parts.length - 1]!, f.currency)}: así la suma da exacto, sin perder centavos.</>
            : <>Cada cuota es de {formatMoney(preview.parts[0]!, f.currency)}. Sin diferencia de redondeo.</>}
        </div>
      )}
      {scheduleLocked && plan && <AdjustPending plan={plan} onDone={onClose} />}
      {plan_ && (
        <details className="more" open={f.ongoing !== 'none' || !!f.disbursement || ONGOING_FIELDS.some((k) => errors[k]?.length)}>
          <summary>{f.type === 'loan' ? 'Crédito ya en curso o capital recibido' : 'Compra ya en curso'}</summary>
          <label className="check"><input type="radio" name="ongoing" checked={f.ongoing === 'none'} disabled={scheduleLocked} onChange={() => set('ongoing', 'none')} {...fieldProps('ongoing')} />Empieza ahora (sin cuotas pagadas)</label>
          <label className="check"><input type="radio" name="ongoing" checked={f.ongoing === 'paid'} disabled={scheduleLocked} onChange={() => set('ongoing', 'paid')} {...fieldProps('ongoing')} />Fecha original y cuotas que ya pagué</label>
          {f.ongoing === 'paid' && (
            <label>
              Cuotas ya pagadas antes de registrarlo
              <input type="number" min={1} value={f.paidBefore} disabled={scheduleLocked} onChange={(e) => set('paidBefore', e.target.value)} {...fieldProps('paidBefore')} />
              {fe.error('paidBefore')}
            </label>
          )}
          <label className="check"><input type="radio" name="ongoing" checked={f.ongoing === 'remaining'} disabled={scheduleLocked} onChange={() => set('ongoing', 'remaining')} {...fieldProps('ongoing')} />Registro solo las cuotas restantes</label>
          {fe.error('ongoing')}
          {f.ongoing !== 'none' && <p className="hint">Las cuotas declaradas pagadas no generan pagos ni historial inventado: quedan marcadas como «pagadas antes de registrar».</p>}
          {f.type === 'loan' && (
            <>
              <div className="fields">
                <label>
                  Capital recibido (opcional)
                  <input inputMode="decimal" value={f.disbursement} onChange={(e) => set('disbursement', e.target.value)} placeholder="0,00" {...fieldProps('disbursement')} />
                  {fe.error('disbursement')}
                </label>
                <label>
                  Fecha de acreditación
                  <input type="date" value={f.disbursementDate} onChange={(e) => set('disbursementDate', e.target.value)} {...fieldProps('disbursementDate')} />
                  {fe.error('disbursementDate')}
                </label>
              </div>
              <label>
                Cuenta donde se acreditó
                <select value={f.disbursementAccountId} onChange={(e) => set('disbursementAccountId', e.target.value)} {...fieldProps('disbursementAccountId')}>
                  <option value="">Sin especificar</option>
                  {cashAccounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
                </select>
                {fe.error('disbursementAccountId')}
              </label>
              <p className="hint">El capital es una entrada de caja separada: no se suma a los ingresos del mes ni al balance presupuestario.</p>
            </>
          )}
        </details>
      )}
      <details className="more" open={!!f.note || !!errors.note?.length}>
        <summary>Nota (opcional)</summary>
        <label>
          Nota
          <input value={f.note} maxLength={500} onChange={(e) => set('note', e.target.value)} {...fieldProps('note')} />
          {fe.error('note')}
        </label>
      </details>
      <p className="hint">{hint}</p>
      {generalError && !conflict && <p className="formerror" role="alert">{generalError}</p>}
      <button className="primary full" disabled={saving}>{saving ? 'Guardando…' : 'Guardar registro'}</button>
    </form>
  );
}

/** Con cuotas pagadas no se reescribe la historia: se ajusta explícitamente el total pendiente de las cuotas abiertas. */
function AdjustPending({ plan, onDone }: { plan: Plan; onDone: () => void }) {
  const qc = useQueryClient();
  const { toast } = useUi();
  const open = (plan.installments ?? []).filter((i) => i.status === 'pending');
  const currentOpen = open.reduce((s, i) => s + BigInt(i.amountMinor), 0n);
  const [value, setValue] = useState(minorToInput(currentOpen.toString() || '0'));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const errorId = `${useId()}err-pending`;
  useEffect(() => setValue(minorToInput(currentOpen.toString())), [currentOpen]);
  if (open.length === 0) return <div className="notice">Todas las cuotas tienen pagos: no hay pendientes para ajustar.</div>;
  const apply = async () => {
    const parsed = parseAmountInput(value);
    if (!parsed.ok) return setError(parsed.error);
    setBusy(true);
    setError(null);
    try {
      await api('POST', `/plans/${plan.id}/adjust-pending`, { pendingTotalMinor: parsed.minor, version: plan.version });
      await invalidateFinancial(qc);
      toast('Cuotas pendientes ajustadas');
      onDone();
    } catch (e) {
      setError(e instanceof ApiError ? e.title : 'No se pudo ajustar.');
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="notice warn">
      Este plan tiene cuotas pagadas: el total, la cantidad y las fechas quedan bloqueados para no reescribir la historia.
      Podés ajustar el total de las {open.length} cuotas sin pagos (desde el {formatDate(open[0]!.dueDate)}):
      <div className="inline">
        <label>
          Nuevo total pendiente
          <input inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
        </label>
        <button type="button" onClick={apply} disabled={busy}>{busy ? 'Ajustando…' : 'Ajustar pendientes'}</button>
      </div>
      {error && <p className="formerror" id={errorId} role="alert">{error}</p>}
    </div>
  );
}
