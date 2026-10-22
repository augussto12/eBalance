import { useEffect, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import QRCode from 'qrcode';
import { ApiError, api, resetCsrf } from '../api/client';
import type { Me } from '../api/types';
import { RecoveryCodes } from '../pages/SettingsPage';

function AuthCard({ eyebrow, title, lead, children }: { eyebrow: string; title: string; lead: ReactNode; children: ReactNode }) {
  useEffect(() => {
    document.title = 'Ingresar — Álvarez Finanzas';
  }, []);
  return (
    <main className="auth">
      <div className="card">
        <span className="brand"><b>á<span>•</span></b> ÁLVAREZ <small>FINANZAS PERSONALES</small></span>
        <div className="eyebrow">{eyebrow}</div>
        <h1>{title}<span>.</span></h1>
        <p className="lead">{lead}</p>
        {children}
        <p className="foot">Espacio privado. Sin registro público ni conexiones bancarias.</p>
      </div>
    </main>
  );
}

function useAuthRefresh() {
  const qc = useQueryClient();
  return async () => {
    resetCsrf();
    qc.removeQueries({ predicate: (q) => q.queryKey[0] !== 'me' });
    await qc.invalidateQueries({ queryKey: ['me'] });
  };
}

export function LoginPage() {
  const refresh = useAuthRefresh();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api('POST', '/auth/login', { username: username.trim(), password }, { quiet401: true });
      setPassword('');
      await refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'No se pudo ingresar.');
      setPassword('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthCard eyebrow="MENOS RUIDO. MÁS CLARIDAD." title="Ingresá a tus finanzas" lead="Usá tu usuario y contraseña. Después te vamos a pedir el código de tu app autenticadora.">
      <form onSubmit={submit} noValidate>
        <label>
          Usuario
          <input autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required maxLength={64} />
        </label>
        <label>
          Contraseña
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required maxLength={128} />
        </label>
        {error && <p className="formerror" role="alert">{error}</p>}
        <button className="primary" disabled={busy}>{busy ? 'Ingresando…' : 'Ingresar'}</button>
      </form>
    </AuthCard>
  );
}

export function MfaPage() {
  const refresh = useAuthRefresh();
  const qc = useQueryClient();
  const [code, setCode] = useState('');
  const [useRecovery, setUseRecovery] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ usedRecoveryCode: boolean; recoveryCodesRemaining: number }>(
        'POST', '/auth/mfa/verify', useRecovery ? { recoveryCode: code.trim() } : { code: code.replace(/\s/g, '') }, { quiet401: true });
      if (r.usedRecoveryCode) {
        setNotice(`Ingresaste con un código de recuperación. Te quedan ${r.recoveryCodesRemaining}. Cerramos tus otras sesiones; si perdiste el autenticador, reconfiguralo en Configuración → Seguridad.`);
        window.setTimeout(() => void refresh(), 4500);
      } else {
        await refresh();
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401 && err.title.startsWith('Tu sesión')) {
        qc.setQueryData<Me>(['me'], { state: 'anonymous' });
        return;
      }
      setError(err instanceof ApiError ? err.title : 'No se pudo verificar.');
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  const cancel = async () => {
    await api('POST', '/auth/logout').catch(() => undefined);
    resetCsrf();
    qc.setQueryData<Me>(['me'], { state: 'anonymous' });
  };

  return (
    <AuthCard eyebrow="SEGUNDO FACTOR" title="Confirmá que sos vos" lead={useRecovery ? 'Ingresá uno de tus códigos de recuperación. Cada uno sirve una sola vez.' : 'Ingresá el código de 6 dígitos de tu app autenticadora.'}>
      {notice ? <div className="notice ok" role="status">{notice}</div> : (
        <form onSubmit={submit} noValidate>
          <label>
            {useRecovery ? 'Código de recuperación' : 'Código'}
            <input
              className={useRecovery ? undefined : 'otp'}
              inputMode={useRecovery ? 'text' : 'numeric'}
              autoComplete="one-time-code"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              maxLength={useRecovery ? 40 : 7}
              required
              autoFocus
            />
          </label>
          {error && <p className="formerror" role="alert">{error}</p>}
          <button className="primary" disabled={busy}>{busy ? 'Verificando…' : 'Verificar'}</button>
          <div className="actions">
            <button type="button" className="linkbtn" onClick={() => { setUseRecovery(!useRecovery); setCode(''); setError(null); }}>
              {useRecovery ? 'Usar la app autenticadora' : 'Usar un código de recuperación'}
            </button>
            <button type="button" className="linkbtn" onClick={cancel}>Volver</button>
          </div>
        </form>
      )}
    </AuthCard>
  );
}

/** Alta obligatoria del autenticador antes de cualquier acceso financiero. QR generado localmente (sin servicios externos). */
export function EnrollPage() {
  const refresh = useAuthRefresh();
  const qc = useQueryClient();
  const [setup, setSetup] = useState<{ uri: string; secret: string; qr: string } | null>(null);
  const [code, setCode] = useState('');
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ otpauthUri: string; secret: string }>('POST', '/auth/mfa/enroll/start', undefined, { quiet401: true });
      const qr = await QRCode.toDataURL(r.otpauthUri, { margin: 1, width: 300, errorCorrectionLevel: 'M' });
      setSetup({ uri: r.otpauthUri, secret: r.secret, qr });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) qc.setQueryData<Me>(['me'], { state: 'anonymous' });
      setError(err instanceof ApiError ? err.title : 'No se pudo iniciar la configuración.');
    } finally {
      setBusy(false);
    }
  };

  const confirm = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ recoveryCodes: string[] }>('POST', '/auth/mfa/enroll/confirm', { code: code.replace(/\s/g, '') }, { quiet401: true });
      setSetup(null);
      setCodes(r.recoveryCodes);
      resetCsrf();
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'No se pudo confirmar.');
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  if (codes) {
    return (
      <AuthCard eyebrow="SEGUNDO FACTOR ACTIVO" title="Guardá tus códigos" lead="Si perdés el celular, cada uno de estos códigos te deja entrar una vez.">
        <RecoveryCodes codes={codes} onDone={() => void refresh()} />
      </AuthCard>
    );
  }

  return (
    <AuthCard eyebrow="PRIMER INGRESO" title="Configurá tu autenticador" lead="Para ver tus finanzas hace falta un segundo factor. Usá una app como Aegis, 2FAS, Google Authenticator o tu gestor de contraseñas.">
      {!setup ? (
        <>
          {error && <p className="formerror" role="alert">{error}</p>}
          <button className="primary" onClick={start} disabled={busy}>{busy ? 'Generando…' : 'Generar código QR'}</button>
        </>
      ) : (
        <form onSubmit={confirm} noValidate>
          <div className="qr">
            <img src={setup.qr} alt="Código QR para la app autenticadora" />
            <div>
              <p className="hint">Escaneá el código o cargá esta clave a mano:</p>
              <div className="secret">{setup.secret}</div>
            </div>
          </div>
          <label>
            Código de 6 dígitos que muestra la app
            <input className="otp" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} maxLength={7} required autoFocus />
          </label>
          {error && <p className="formerror" role="alert">{error}</p>}
          <button className="primary" disabled={busy}>{busy ? 'Confirmando…' : 'Activar segundo factor'}</button>
        </form>
      )}
    </AuthCard>
  );
}
