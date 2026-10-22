import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { Me } from '../api/types';
import type { Currency } from '../lib/money';
import { currentMonth } from '../lib/dates';
import { Modal } from './Modal';
import { RecordDialog, type RecordTarget } from './RecordForm';
import { ApiError, api, resetCsrf } from '../api/client';

interface ToastOptions {
  error?: boolean;
  action?: { label: string; run: () => void };
}

interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  danger?: boolean;
}

interface UiValue {
  me: Me;
  month: string;
  setMonth: (m: string) => void;
  currency: Currency;
  setCurrency: (c: Currency) => void;
  toast: (text: string, options?: ToastOptions) => void;
  openRecord: (target: RecordTarget) => void;
  confirm: (options: ConfirmOptions) => Promise<boolean>;
  /** Pide contraseña + código y eleva la sesión unos minutos. Resuelve true si se completó. */
  reauth: () => Promise<boolean>;
}

const UiContext = createContext<UiValue | null>(null);

export function useUi(): UiValue {
  const v = useContext(UiContext);
  if (!v) throw new Error('UiProvider faltante');
  return v;
}

export function UiProvider({ me, children }: { me: Me; children: ReactNode }) {
  const [month, setMonth] = useState(currentMonth);
  const [currency, setCurrency] = useState<Currency>('ARS');
  const [toastState, setToastState] = useState<{ text: string; options?: ToastOptions; key: number } | null>(null);
  const [record, setRecord] = useState<RecordTarget | null>(null);
  const [confirmState, setConfirmState] = useState<(ConfirmOptions & { resolve: (v: boolean) => void }) | null>(null);
  const [reauthState, setReauthState] = useState<{ resolve: (v: boolean) => void } | null>(null);
  const timer = useRef<number | undefined>(undefined);

  const toast = useCallback((text: string, options?: ToastOptions) => {
    window.clearTimeout(timer.current);
    setToastState({ text, options, key: Date.now() });
    timer.current = window.setTimeout(() => setToastState(null), options?.action ? 7000 : 3500);
  }, []);

  const confirm = useCallback((options: ConfirmOptions) => new Promise<boolean>((resolve) => setConfirmState({ ...options, resolve })), []);
  const reauth = useCallback(() => new Promise<boolean>((resolve) => setReauthState({ resolve })), []);

  const value = useMemo<UiValue>(
    () => ({ me, month, setMonth, currency, setCurrency, toast, openRecord: setRecord, confirm, reauth }),
    [me, month, currency, toast, confirm, reauth],
  );

  const closeConfirm = (result: boolean) => {
    confirmState?.resolve(result);
    setConfirmState(null);
  };

  return (
    <UiContext.Provider value={value}>
      {children}
      <RecordDialog target={record} onClose={() => setRecord(null)} />
      <Modal open={confirmState !== null} title={confirmState?.title ?? ''} onClose={() => closeConfirm(false)}>
        {confirmState && (
          <>
            <div className="hint">{confirmState.message}</div>
            <div className="actions">
              <button type="button" onClick={() => closeConfirm(false)}>Cancelar</button>
              <button type="button" data-autofocus className={confirmState.danger ? 'danger-btn' : 'primary'} onClick={() => closeConfirm(true)}>
                {confirmState.confirmLabel}
              </button>
            </div>
          </>
        )}
      </Modal>
      <ReauthDialog
        open={reauthState !== null}
        onDone={(ok) => {
          reauthState?.resolve(ok);
          setReauthState(null);
        }}
      />
      <div
        id="toast"
        role="status"
        aria-live="polite"
        className={[toastState ? 'show' : '', toastState?.options?.error ? 'error' : ''].join(' ').trim() || undefined}
      >
        {toastState?.text}
        {toastState?.options?.action && (
          <button
            type="button"
            onClick={() => {
              toastState.options?.action?.run();
              setToastState(null);
            }}
          >
            {toastState.options.action.label}
          </button>
        )}
      </div>
    </UiContext.Provider>
  );
}

function ReauthDialog({ open, onDone }: { open: boolean; onDone: (ok: boolean) => void }) {
  const qc = useQueryClient();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) {
      setPassword('');
      setCode('');
      setError(null);
    }
  }, [open]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api('POST', '/auth/reauth', { password, code: code.replace(/\s/g, '') }, { quiet401: true });
      resetCsrf();
      await qc.invalidateQueries({ queryKey: ['me'] });
      onDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'No se pudo verificar.');
    } finally {
      setBusy(false);
      setPassword('');
    }
  };

  return (
    <Modal open={open} title="Confirmá que sos vos" onClose={() => onDone(false)} busy={busy}>
      <form onSubmit={submit} noValidate>
        <p className="hint">Para esta acción pedimos tu contraseña y un código de tu app autenticadora. La confirmación dura unos minutos.</p>
        <label>
          Contraseña
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required maxLength={128} />
        </label>
        <label>
          Código de 6 dígitos
          <input inputMode="numeric" autoComplete="one-time-code" className="otp" value={code} onChange={(e) => setCode(e.target.value)} required maxLength={7} />
        </label>
        {error && <p className="formerror" role="alert">{error}</p>}
        <button className="primary full" disabled={busy}>{busy ? 'Verificando…' : 'Confirmar'}</button>
      </form>
    </Modal>
  );
}
