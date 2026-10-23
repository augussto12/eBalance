import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, download, idempotencyKey, resetCsrf } from '../api/client';
import type { Account, AccountKind, Category, ImportPreview, Me, SessionInfo, Transfer, TrashItem } from '../api/types';
import { Modal, useFieldErrors } from '../components/Modal';
import { PasswordField } from '../components/PasswordField';
import { QueryState, RefetchError } from '../components/Records';
import { useUi } from '../components/Ui';
import { formatDate, formatDateTime, todayIso } from '../lib/dates';
import { formatMoney, minorToInput, parseAmountInput, type Currency } from '../lib/money';
import { hasErrors, newPasswordErrors, passwordTooLong, problemErrors, type FieldErrors } from '../lib/forms';
import { invalidateFinancial, signOutLocally, useAccounts, useCategories } from './queries';

const KIND_LABEL: Record<AccountKind, string> = { cash: 'Efectivo', bank: 'Banco', wallet: 'Billetera virtual', credit_card: 'Tarjeta de crédito' };

function errorText(e: unknown, fallback: string): string {
  return e instanceof ApiError ? e.title : fallback;
}

export function SettingsPage({ me }: { me: Me }) {
  const location = useLocation();
  useEffect(() => {
    if (!location.hash) return;
    document.getElementById(location.hash.slice(1))?.scrollIntoView({ block: 'start' });
  }, [location.hash]);

  return (
    <div className="settings">
      <Profile me={me} />
      <Security me={me} />
      <Accounts />
      <Categories />
      <Transfers />
      <Backup />
      <Trash />
    </div>
  );
}

function Profile({ me }: { me: Me }) {
  const qc = useQueryClient();
  const { toast } = useUi();
  const [name, setName] = useState(me.user?.displayName ?? '');
  const [error, setError] = useState<string | null>(null);
  const [errors, setErrors] = useState<FieldErrors<'displayName'>>({});
  const fe = useFieldErrors(errors);
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setErrors({});
    try {
      await api('PATCH', '/auth/me', { displayName: name, version: me.user?.version });
      await qc.invalidateQueries({ queryKey: ['me'] });
      toast('Perfil actualizado');
    } catch (err) {
      const r = problemErrors(err, { displayName: 'displayName' }, 'No se pudo guardar.');
      setErrors(r.fields);
      setError(r.form);
    }
  };
  return (
    <section className="panel" id="perfil">
      <h2>Perfil</h2>
      <p>Tu nombre define las iniciales del avatar. Usuario: {me.user?.username}.</p>
      <form className="inline" onSubmit={save}>
        <label>
          Nombre para mostrar
          <input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} required {...fe.props('displayName')} />
          {fe.error('displayName')}
        </label>
        <button className="primary">Guardar</button>
      </form>
      {error && <p className="formerror" role="alert">{error}</p>}
    </section>
  );
}

function Accounts() {
  const qc = useQueryClient();
  const { toast, confirm } = useUi();
  const accounts = useAccounts();
  const [name, setName] = useState('');
  const [kind, setKind] = useState<AccountKind>('bank');
  const [currency, setCurrency] = useState<Currency>('ARS');
  const [errors, setErrors] = useState<FieldErrors<'name' | 'kind' | 'currency'>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const fe = useFieldErrors(errors);
  const [editing, setEditing] = useState<Account | null>(null);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors({});
    setFormError(null);
    try {
      await api('POST', '/accounts', { name: name.trim(), kind, currency });
      setName('');
      await invalidateFinancial(qc);
      toast('Cuenta creada');
    } catch (err) {
      const r = problemErrors(err, { name: 'name', kind: 'kind', currency: 'currency' }, 'No se pudo crear.');
      setErrors(r.fields);
      setFormError(r.form);
    }
  };

  const archive = async (a: Account) => {
    if (!(await confirm({ title: 'Archivar cuenta', message: <>«{a.name}» deja de aparecer al cargar movimientos. Los registros existentes la conservan.</>, confirmLabel: 'Archivar', danger: true }))) return;
    try {
      await api('DELETE', `/accounts/${a.id}?version=${a.version}`);
      await invalidateFinancial(qc);
      toast('Cuenta archivada', { action: { label: 'Deshacer', run: async () => { await api('POST', `/accounts/${a.id}/restore`); await invalidateFinancial(qc); } } });
    } catch (err) {
      toast(errorText(err, 'No se pudo archivar.'), { error: true });
    }
  };

  return (
    <section className="panel span" id="cuentas">
      <h2>Cuentas y tarjetas</h2>
      <p>Sin números completos de tarjeta: solo un nombre y, si querés, los últimos cuatro dígitos. El saldo se calcula solo si cargás un saldo inicial.</p>
      {accounts.data ? (
        accounts.data.length === 0 ? <div className="empty">Todavía no hay cuentas.</div> : (
          <div className="tablewrap">
            <table>
              <thead><tr><th>Cuenta</th><th>Tipo</th><th>Moneda</th><th className="num">Saldo / pendiente</th><th><span className="sr-only">Acciones</span></th></tr></thead>
              <tbody>
                {accounts.data.map((a) => (
                  <tr key={a.id}>
                    <td>{a.name}{a.last4 && <small>•••• {a.last4}</small>}</td>
                    <td>{KIND_LABEL[a.kind]}</td>
                    <td>{a.currency}</td>
                    <td className="num">
                      {a.kind === 'credit_card'
                        ? <>{formatMoney(a.pendingObligationsMinor ?? '0', a.currency)}<small>pendiente total</small></>
                        : a.cashBalanceMinor !== null
                          ? <>{formatMoney(a.cashBalanceMinor, a.currency)}<small>desde {formatDate(a.openingDate!)} · solo movimientos registrados</small></>
                          : <small>Sin saldo inicial</small>}
                    </td>
                    <td><div className="rowactions">
                      <button className="delete" title="Editar" aria-label={`Editar ${a.name}`} onClick={() => setEditing(a)}>✎</button>
                      <button className="delete" title="Archivar" aria-label={`Archivar ${a.name}`} onClick={() => archive(a)}>×</button>
                    </div></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      ) : <QueryState isPending={accounts.isPending} error={accounts.error} onRetry={() => accounts.refetch()} />}
      {accounts.data && <RefetchError error={accounts.error} retrying={accounts.isFetching} onRetry={() => accounts.refetch()} />}
      {editing && <AccountEditor account={editing} onDone={() => setEditing(null)} />}
      <form className="inline" onSubmit={create}>
        <label>Nombre<input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="Ej. Mercado Pago" {...fe.props('name')} />{fe.error('name')}</label>
        <label>Tipo<select value={kind} onChange={(e) => setKind(e.target.value as AccountKind)} {...fe.props('kind')}>{Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>{fe.error('kind')}</label>
        <label>Moneda<select value={currency} onChange={(e) => setCurrency(e.target.value as Currency)} {...fe.props('currency')}><option>ARS</option><option>USD</option></select>{fe.error('currency')}</label>
        <button>＋ Agregar cuenta</button>
      </form>
      {formError && <p className="formerror" role="alert">{formError}</p>}
    </section>
  );
}

function AccountEditor({ account, onDone }: { account: Account; onDone: () => void }) {
  const qc = useQueryClient();
  const { toast } = useUi();
  const [name, setName] = useState(account.name);
  const [last4, setLast4] = useState(account.last4 ?? '');
  const [opening, setOpening] = useState(account.openingBalanceMinor ? minorToInput(account.openingBalanceMinor.replace('-', '')) : '');
  const [negative, setNegative] = useState(account.openingBalanceMinor?.startsWith('-') ?? false);
  const [openingDate, setOpeningDate] = useState(account.openingDate ?? todayIso());
  const [error, setError] = useState<string | null>(null);
  const [errors, setErrors] = useState<FieldErrors<'name' | 'last4' | 'opening' | 'openingDate'>>({});
  const fe = useFieldErrors(errors);
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setErrors({});
    let openingMinor: string | null = null;
    if (opening.trim() && account.kind !== 'credit_card') {
      if (opening.trim() === '0') openingMinor = '0';
      else {
        const p = parseAmountInput(opening);
        if (!p.ok) return setErrors({ opening: [p.error] });
        openingMinor = (negative ? '-' : '') + p.minor;
      }
    }
    try {
      await api('PATCH', `/accounts/${account.id}`, {
        name: name.trim(), last4: last4.trim() || null,
        openingBalanceMinor: openingMinor, openingDate: openingMinor !== null ? openingDate : null, version: account.version,
      });
      await invalidateFinancial(qc);
      toast('Cuenta actualizada');
      onDone();
    } catch (err) {
      const r = problemErrors(err, { name: 'name', last4: 'last4', openingBalanceMinor: 'opening', openingDate: 'openingDate' }, 'No se pudo guardar.');
      setErrors(r.fields);
      setError(r.form);
    }
  };
  return (
    <form className="preview" onSubmit={save}>
      <strong>Editar «{account.name}»</strong>
      <div className="inline">
        <label>Nombre<input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} {...fe.props('name')} />{fe.error('name')}</label>
        <label>Últimos 4 (opcional)<input value={last4} inputMode="numeric" maxLength={4} onChange={(e) => setLast4(e.target.value)} {...fe.props('last4')} />{fe.error('last4')}</label>
      </div>
      {account.kind !== 'credit_card' && (
        <div className="inline">
          <label>Saldo inicial<input inputMode="decimal" value={opening} onChange={(e) => setOpening(e.target.value)} placeholder="Dejar vacío si no lo sabés" {...fe.props('opening')} />{fe.error('opening')}</label>
          <label className="check"><input type="checkbox" checked={negative} onChange={(e) => setNegative(e.target.checked)} />Negativo</label>
          <label>A la fecha<input type="date" value={openingDate} onChange={(e) => setOpeningDate(e.target.value)} {...fe.props('openingDate')} />{fe.error('openingDate')}</label>
        </div>
      )}
      {error && <p className="formerror" role="alert">{error}</p>}
      <div className="actions"><button type="button" onClick={onDone}>Cancelar</button><button className="primary">Guardar</button></div>
    </form>
  );
}

function Categories() {
  const qc = useQueryClient();
  const { toast } = useUi();
  const categories = useCategories();
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [errors, setErrors] = useState<FieldErrors<'name'>>({});
  const fe = useFieldErrors(errors);
  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setErrors({});
    try {
      await api('POST', '/categories', { name: name.trim() });
      setName('');
      await invalidateFinancial(qc);
    } catch (err) {
      const r = problemErrors(err, { name: 'name' }, 'No se pudo crear.');
      setErrors(r.fields);
      setError(r.form);
    }
  };
  const archive = async (c: Category) => {
    try {
      await api('DELETE', `/categories/${c.id}?version=${c.version}`);
      await invalidateFinancial(qc);
      toast('Categoría archivada', { action: { label: 'Deshacer', run: async () => { await api('POST', `/categories/${c.id}/restore`); await invalidateFinancial(qc); } } });
    } catch (err) {
      toast(errorText(err, 'No se pudo archivar.'), { error: true });
    }
  };
  return (
    <section className="panel" id="categorias">
      <h2>Categorías</h2>
      <p>Las categorías archivadas se conservan en los registros históricos.</p>
      {categories.data ? categories.data.map((c) => (
        <div className="kv" key={c.id}><span className="badge">{c.name}</span><button className="delete" aria-label={`Archivar ${c.name}`} onClick={() => archive(c)}>×</button></div>
      )) : <QueryState isPending={categories.isPending} error={categories.error} onRetry={() => categories.refetch()} />}
      {categories.data && <RefetchError error={categories.error} retrying={categories.isFetching} onRetry={() => categories.refetch()} />}
      <form className="inline" onSubmit={create}>
        <label>Nueva categoría<input value={name} maxLength={40} onChange={(e) => setName(e.target.value)} {...fe.props('name')} />{fe.error('name')}</label>
        <button>＋ Agregar</button>
      </form>
      {error && <p className="formerror" role="alert">{error}</p>}
    </section>
  );
}

function Transfers() {
  const qc = useQueryClient();
  const { toast } = useUi();
  const accounts = useAccounts();
  const transfers = useQuery({ queryKey: ['transfers'], queryFn: () => api<Transfer[]>('GET', '/transfers') });
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(todayIso());
  const [errors, setErrors] = useState<FieldErrors<'from' | 'to' | 'amount' | 'date'>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const fe = useFieldErrors(errors);
  const [busy, setBusy] = useState(false);
  const cash = (accounts.data ?? []).filter((a) => a.kind !== 'credit_card');
  const fromAccount = cash.find((a) => a.id === from);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setFormError(null);
    const p = parseAmountInput(amount);
    const errs: FieldErrors<'amount' | 'date'> = {};
    if (!p.ok) errs.amount = [p.error];
    if (!date) errs.date = ['La fecha no es válida.'];
    if (!p.ok || hasErrors(errs)) return setErrors(errs);
    setBusy(true);
    setErrors({});
    try {
      await api('POST', '/transfers', { fromAccountId: from || null, toAccountId: to || null, currency: fromAccount?.currency ?? 'ARS', amountMinor: p.minor, date });
      setAmount('');
      await invalidateFinancial(qc);
      toast('Transferencia registrada');
    } catch (err) {
      // La moneda sale de la cuenta de origen: su error se muestra en «Desde».
      const r = problemErrors(err, { fromAccountId: 'from', currency: 'from', toAccountId: 'to', amountMinor: 'amount', date: 'date' }, 'No se pudo registrar.');
      setErrors(r.fields);
      setFormError(r.form);
    } finally {
      setBusy(false);
    }
  };
  const remove = async (t: Transfer) => {
    try {
      await api('DELETE', `/transfers/${t.id}?version=${t.version}`);
      await invalidateFinancial(qc);
      toast('Transferencia eliminada', { action: { label: 'Deshacer', run: async () => { await api('POST', `/transfers/${t.id}/restore`); await invalidateFinancial(qc); } } });
    } catch (err) {
      toast(errorText(err, 'No se pudo eliminar.'), { error: true });
    }
  };
  return (
    <section className="panel" id="transferencias">
      <h2>Transferencias entre tus cuentas</h2>
      <p>No son ingresos ni gastos: solo mueven saldo entre cuentas propias de la misma moneda.</p>
      <form onSubmit={submit} noValidate>
        <div className="fields">
          <label>Desde<select value={from} onChange={(e) => setFrom(e.target.value)} {...fe.props('from')}><option value="">Elegí…</option>{cash.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.currency})</option>)}</select>{fe.error('from')}</label>
          <label>Hacia<select value={to} onChange={(e) => setTo(e.target.value)} {...fe.props('to')}><option value="">Elegí…</option>{cash.filter((a) => !fromAccount || a.currency === fromAccount.currency).map((a) => <option key={a.id} value={a.id}>{a.name} ({a.currency})</option>)}</select>{fe.error('to')}</label>
        </div>
        <div className="fields">
          <label>Importe<input inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} {...fe.props('amount')} />{fe.error('amount')}</label>
          <label>Fecha<input type="date" value={date} onChange={(e) => setDate(e.target.value)} {...fe.props('date')} />{fe.error('date')}</label>
        </div>
        {formError && <p className="formerror" role="alert">{formError}</p>}
        <button disabled={busy}>Registrar transferencia</button>
      </form>
      {transfers.data
        ? <RefetchError error={transfers.error} retrying={transfers.isFetching} onRetry={() => transfers.refetch()} />
        : <QueryState isPending={transfers.isPending} error={transfers.error} onRetry={() => transfers.refetch()} />}
      {transfers.data?.slice(0, 8).map((t) => (
        <div className="kv" key={t.id}>
          <span>{formatDate(t.date)} · {t.fromAccountName} → {t.toAccountName}</span>
          <span>{formatMoney(t.amountMinor, t.currency)} <button className="delete" aria-label="Eliminar transferencia" onClick={() => remove(t)}>×</button></span>
        </div>
      ))}
    </section>
  );
}

function Security({ me }: { me: Me }) {
  const qc = useQueryClient();
  const { toast, confirm, reauth } = useUi();
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => api<SessionInfo[]>('GET', '/auth/sessions') });
  const [codes, setCodes] = useState<string[] | null>(null);
  const [pwd, setPwd] = useState({ current: '', next: '', repeat: '' });
  const [pwdErrors, setPwdErrors] = useState<FieldErrors<'current' | 'next' | 'repeat'>>({});
  const [pwdError, setPwdError] = useState<string | null>(null);
  /** Al editar un campo se borra su error anterior (el de longitud máxima lo recalcula PasswordField en el momento). */
  const setPwdField = (k: 'current' | 'next' | 'repeat') => (v: string) => {
    setPwd((p) => ({ ...p, [k]: v }));
    setPwdErrors((e) => ({ ...e, [k]: undefined }));
  };

  const elevated = () => me.elevatedUntil && new Date(me.elevatedUntil).getTime() > Date.now() + 15_000;
  const withReauth = async (fn: () => Promise<void>) => {
    if (!elevated() && !(await reauth())) return;
    try {
      await fn();
    } catch (err) {
      if (err instanceof ApiError && err.status === 403 && (await reauth())) await fn();
      else throw err;
    }
  };
  const goToLogin = () => {
    resetCsrf();
    signOutLocally(qc);
  };

  const revoke = async (s: SessionInfo) => {
    try {
      await api('DELETE', `/auth/sessions/${s.id}`);
      await qc.invalidateQueries({ queryKey: ['sessions'] });
      toast('Sesión cerrada');
    } catch (err) {
      toast(errorText(err, 'No se pudo cerrar.'), { error: true });
    }
  };
  const logoutAll = async () => {
    if (!(await confirm({ title: 'Cerrar todas las sesiones', message: 'Se cierran todas las sesiones, incluida esta.', confirmLabel: 'Cerrar todas', danger: true }))) return;
    await api('POST', '/auth/logout-all');
    goToLogin();
  };
  const changePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setPwdError(null);
    // Sin maxLength en los campos (no se recorta lo pegado): las longitudes se validan acá y junto a cada campo.
    const currentError = !pwd.current ? 'Ingresá tu contraseña actual.' : passwordTooLong(pwd.current);
    const errs: FieldErrors<'current' | 'next' | 'repeat'> = { ...newPasswordErrors(pwd.next, pwd.repeat), ...(currentError ? { current: [currentError] } : {}) };
    setPwdErrors(errs);
    if (hasErrors(errs)) return;
    try {
      await withReauth(async () => {
        await api('POST', '/auth/password/change', { currentPassword: pwd.current, newPassword: pwd.next });
      });
      setPwd({ current: '', next: '', repeat: '' });
      goToLogin();
    } catch (err) {
      const r = problemErrors(err, { currentPassword: 'current', newPassword: 'next' }, 'No se pudo cambiar la contraseña.');
      setPwdErrors(r.fields);
      setPwdError(r.form);
    }
  };
  const regenerate = async () => {
    if (!(await confirm({ title: 'Generar códigos nuevos', message: 'Los códigos de recuperación anteriores dejan de funcionar.', confirmLabel: 'Generar' }))) return;
    try {
      await withReauth(async () => {
        const r = await api<{ recoveryCodes: string[] }>('POST', '/auth/recovery-codes/regenerate');
        setCodes(r.recoveryCodes);
      });
      await qc.invalidateQueries({ queryKey: ['me'] });
    } catch (err) {
      toast(errorText(err, 'No se pudieron generar.'), { error: true });
    }
  };
  const resetMfa = async () => {
    if (!(await confirm({ title: 'Reconfigurar autenticador', message: 'Se desactiva el autenticador actual, se borran los códigos de recuperación y se cierran todas las sesiones. Al volver a entrar vas a configurar uno nuevo.', confirmLabel: 'Reconfigurar', danger: true }))) return;
    try {
      await withReauth(async () => { await api('POST', '/auth/mfa/reset'); });
      goToLogin();
    } catch (err) {
      toast(errorText(err, 'No se pudo reconfigurar.'), { error: true });
    }
  };

  return (
    <section className="panel" id="seguridad">
      <h2>Seguridad</h2>
      <p>Cambiar la contraseña o el autenticador pide confirmar tu identidad y cierra todas las sesiones.</p>
      <form onSubmit={changePassword} noValidate>
        <PasswordField label="Contraseña actual" autoComplete="current-password" value={pwd.current} onChange={setPwdField('current')} errors={pwdErrors.current} />
        <div className="fields">
          <PasswordField label="Nueva (15 a 128 caracteres)" autoComplete="new-password" value={pwd.next} onChange={setPwdField('next')} errors={pwdErrors.next} />
          <PasswordField label="Repetila" autoComplete="new-password" value={pwd.repeat} onChange={setPwdField('repeat')} errors={pwdErrors.repeat} />
        </div>
        {pwdError && <p className="formerror" role="alert">{pwdError}</p>}
        <button>Cambiar contraseña</button>
      </form>
      <div className="kv"><span>Códigos de recuperación sin usar</span><span>{me.recoveryCodesRemaining ?? '—'}</span></div>
      {codes && <RecoveryCodes codes={codes} onDone={() => setCodes(null)} />}
      <div className="actions">
        <button onClick={regenerate}>Generar códigos nuevos</button>
        <button onClick={resetMfa}>Reconfigurar autenticador</button>
      </div>
      <h2 className="wide">Sesiones activas</h2>
      {sessions.data ? sessions.data.map((s) => (
        <div className="kv" key={s.id}>
          <span>{s.current ? 'Esta sesión' : (s.userAgent ?? 'Navegador').slice(0, 60)}<small> · última actividad {formatDateTime(s.lastSeenAt)} · {s.ipPrefix ?? ''}</small></span>
          {!s.current && <button className="delete" onClick={() => revoke(s)}>Cerrar</button>}
        </div>
      )) : <QueryState isPending={sessions.isPending} error={sessions.error} onRetry={() => sessions.refetch()} />}
      {sessions.data && <RefetchError error={sessions.error} retrying={sessions.isFetching} onRetry={() => sessions.refetch()} />}
      <div className="actions"><button onClick={logoutAll}>Cerrar todas las sesiones</button></div>
    </section>
  );
}

export function RecoveryCodes({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const save = () => {
    const blob = new Blob([`Álvarez Finanzas — códigos de recuperación\nCada código sirve una sola vez.\n\n${codes.join('\n')}\n`], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'alvarez-finanzas-codigos-recuperacion.txt';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  return (
    <div className="notice ok" role="alert">
      Guardá estos códigos en tu gestor de contraseñas. Se muestran una sola vez y cada uno sirve una vez.
      <div className="codes">{codes.map((c) => <span key={c}>{c}</span>)}</div>
      <div className="actions">
        <button type="button" onClick={() => navigator.clipboard?.writeText(codes.join('\n'))}>Copiar</button>
        <button type="button" onClick={save}>Descargar .txt</button>
        <button type="button" className="primary" onClick={onDone}>Ya los guardé</button>
      </div>
    </div>
  );
}

/** Errores de lectura o de importación (los primeros 12). */
function ImportErrors({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  return <div className="notice danger" role="alert">{errors.slice(0, 12).map((e, i) => <div key={i}>{e}</div>)}</div>;
}

/**
 * Confirmación de importación en el modal accesible de la app (foco atrapado, Escape, foco devuelto al cerrar):
 * nada se guarda hasta «Confirmar importación».
 */
export function ImportConfirmDialog({ preview, kinds, onKindsChange, busy, errors, onCancel, onConfirm }: {
  preview: ImportPreview | null;
  kinds: Record<string, AccountKind>;
  onKindsChange: (kinds: Record<string, AccountKind>) => void;
  busy: boolean;
  errors: string[];
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Modal open={preview !== null} title="Confirmar importación" onClose={onCancel} busy={busy}>
      {preview && (
        <div className="preview importpreview">
          <strong>Vista previa ({preview.summary.format === 'legacy-v1' ? 'respaldo del prototipo' : 'exportación de esta app'})</strong>
          <div>{preview.summary.total} registros: {preview.summary.new} nuevos, {preview.summary.duplicates} ya importados, {preview.summary.conflicts} en conflicto.</div>
          <div>Por moneda: {Object.entries(preview.summary.byCurrency).map(([c, n]) => `${c} ${n}`).join(' · ')}. Las monedas no se suman entre sí.</div>
          {preview.warnings.map((w) => <div key={w} className="notice warn">{w}</div>)}
          {preview.conflicts.length > 0 && <div className="notice warn">Con contenido distinto al ya importado (no se sobrescriben): {preview.conflicts.map((c) => c.concept ?? c.sourceId).join(', ')}</div>}
          {preview.accountsToCreate.length > 0 && (
            <>
              <div>Cuentas nuevas a crear (elegí el tipo):</div>
              {preview.accountsToCreate.map((a) => (
                <div className="inline" key={a.key}>
                  <label>{a.name} ({a.currency})
                    <select value={kinds[a.key] ?? 'bank'} onChange={(e) => onKindsChange({ ...kinds, [a.key]: e.target.value as AccountKind })}>
                      {Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                    </select>
                  </label>
                </div>
              ))}
            </>
          )}
          {preview.categoriesToCreate.length > 0 && <div>Categorías nuevas: {preview.categoriesToCreate.join(', ')}</div>}
          <div className="tablewrap">
            <table>
              <thead><tr><th>Concepto</th><th>Tipo</th><th>Fecha</th><th>Cuenta</th><th className="num">Importe total</th></tr></thead>
              <tbody>
                {preview.sample.map((s, i) => (
                  <tr key={i}>
                    <td>{s.concept}</td>
                    <td>{s.type}{s.count && s.count !== '1' ? ` · ${s.count} cuotas` : ''}</td>
                    <td>{s.date ? formatDate(s.date) : ''}</td>
                    <td>{s.account ?? 'Sin especificar'}</td>
                    <td className="num">{s.amountMinor && s.currency ? formatMoney(s.amountMinor, s.currency as Currency) : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="hint">
            {preview.summary.new === 0
              ? 'No hay registros nuevos para importar: todo ya estaba importado o está en conflicto.'
              : `Al confirmar se crean ${preview.summary.new} registros nuevos en una sola operación. Los ya importados y los que están en conflicto no se modifican.`}
          </p>
          <ImportErrors errors={errors} />
          <div className="actions">
            <button type="button" onClick={onCancel} disabled={busy} data-autofocus={preview.summary.new === 0 ? true : undefined}>Cancelar</button>
            <button type="button" className="primary" onClick={onConfirm} disabled={busy || preview.summary.new === 0} data-autofocus={preview.summary.new === 0 ? undefined : true}>
              {busy ? 'Importando…' : 'Confirmar importación'}
            </button>
          </div>
        </div>
      )}
    </Modal>
  );
}

function Backup() {
  const qc = useQueryClient();
  const { toast, reauth, me } = useUi();
  const fileRef = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [kinds, setKinds] = useState<Record<string, AccountKind>>({});
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [commitKey, setCommitKey] = useState(idempotencyKey);
  const location = useLocation();
  const importButton = useRef<HTMLButtonElement>(null);
  const hadPreview = useRef(false);

  useEffect(() => {
    if (location.hash === '#importar') fileRef.current?.focus();
  }, [location.hash]);

  // Al cerrar el modal de confirmación el foco vuelve a «Importar respaldo» (mientras se leía el archivo estaba
  // deshabilitado, así que el modal no tiene a quién devolverlo).
  useEffect(() => {
    if (preview) hadPreview.current = true;
    else if (hadPreview.current) {
      hadPreview.current = false;
      importButton.current?.focus();
    }
  }, [preview]);

  const closePreview = () => {
    setPreview(null);
    setErrors([]);
  };

  const exportAll = async () => {
    const elevated = me.elevatedUntil && new Date(me.elevatedUntil).getTime() > Date.now() + 15_000;
    if (!elevated && !(await reauth())) return;
    try {
      await download('/exports', `alvarez-finanzas-export-${todayIso()}.json`);
      toast('Respaldo descargado. Guardalo en un lugar seguro: está en texto claro.');
    } catch (err) {
      if (err instanceof ApiError && err.status === 403 && (await reauth())) {
        await download('/exports', `alvarez-finanzas-export-${todayIso()}.json`);
      } else toast(errorText(err, 'No se pudo exportar.'), { error: true });
    }
  };

  const onFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setErrors([]);
    setPreview(null);
    if (file.size > 5 * 1024 * 1024) return setErrors(['El archivo supera 5 MiB.']);
    setBusy(true);
    try {
      const text = await file.text();
      const p = await api<ImportPreview>('POST', '/imports/preview', undefined, { raw: text, contentType: 'application/json' });
      setPreview(p);
      setCommitKey(idempotencyKey());
      setKinds(Object.fromEntries(p.accountsToCreate.map((a) => [a.key, a.usedByPlans && /visa|master|amex|tarjeta|naranja|cabal/i.test(a.name) ? 'credit_card' : 'bank'])));
    } catch (err) {
      if (err instanceof ApiError) setErrors(err.details.length ? err.details : [err.title]);
      else setErrors(['No se pudo leer el archivo.']);
    } finally {
      setBusy(false);
    }
  };

  const commit = async () => {
    if (!preview || busy) return;
    setBusy(true);
    try {
      const r = await api<{ result: { created: number; duplicates: number; conflicts: number } }>(
        'POST', `/imports/${preview.batchId}/commit`, { confirm: true, accountKinds: kinds }, { headers: { 'Idempotency-Key': commitKey } });
      await invalidateFinancial(qc);
      setPreview(null);
      toast(`Importación lista: ${r.result.created} nuevos, ${r.result.duplicates} ya existían${r.result.conflicts ? `, ${r.result.conflicts} en conflicto sin cambios` : ''}.`);
    } catch (err) {
      setErrors([errorText(err, 'No se pudo importar.')]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="panel span" id="respaldo">
      <h2>Respaldo y restauración</h2>
      <p>La exportación incluye cuentas, categorías, movimientos, planes, pagos y transferencias. No incluye contraseñas, autenticador, sesiones ni claves. El archivo queda en texto claro bajo tu control.</p>
      <div className="actions">
        <button onClick={exportAll}>↓ Exportar respaldo completo</button>
        <label className="sr-only" htmlFor="importfile">Archivo de respaldo</label>
        <input ref={fileRef} id="importfile" type="file" accept="application/json,.json" hidden onChange={onFile} />
        <button ref={importButton} id="importar" onClick={() => fileRef.current?.click()} disabled={busy}>↑ Importar respaldo (JSON)</button>
      </div>
      <p className="hint">Acepta el respaldo del prototipo anterior (version 1) o una exportación de esta app. Primero vas a ver una vista previa; nada se guarda hasta que confirmes. Repetir la misma importación no duplica registros.</p>
      {busy && !preview && <div className="loading">Procesando…</div>}
      {!preview && <ImportErrors errors={errors} />}
      <ImportConfirmDialog preview={preview} kinds={kinds} onKindsChange={setKinds} busy={busy} errors={errors} onCancel={closePreview} onConfirm={commit} />
    </section>
  );
}

function Trash() {
  const qc = useQueryClient();
  const { toast } = useUi();
  const trash = useQuery({ queryKey: ['trash'], queryFn: () => api<TrashItem[]>('GET', '/trash') });
  const restore = async (t: TrashItem) => {
    const base = t.type === 'entry' ? 'entries' : t.type === 'transfer' ? 'transfers' : 'plans';
    try {
      await api('POST', `/${base}/${t.id}/restore`);
      await invalidateFinancial(qc);
      toast('Registro restaurado');
    } catch (err) {
      toast(errorText(err, 'No se pudo restaurar.'), { error: true });
    }
  };
  return (
    <section className="panel span" id="papelera">
      <h2>Papelera</h2>
      <p>Los registros eliminados se pueden restaurar.</p>
      {trash.data ? (trash.data.length === 0 ? <div className="empty">La papelera está vacía.</div> : trash.data.map((t) => (
        <div className="kv" key={`${t.type}-${t.id}`}>
          <span>{t.concept}<small> · {formatDate(t.date)} · eliminado {formatDateTime(t.deletedAt)}</small></span>
          <span>{formatMoney(t.amountMinor, t.currency)} <button className="delete" onClick={() => restore(t)}>Restaurar</button></span>
        </div>
      ))) : <QueryState isPending={trash.isPending} error={trash.error} onRetry={() => trash.refetch()} />}
    </section>
  );
}
