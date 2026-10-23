import { useEffect, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router';
import { useIsMutating, useQueryClient } from '@tanstack/react-query';
import type { Entry, Me, Page, Plan } from '../api/types';
import { ApiError, api, resetCsrf } from '../api/client';
import { useUi } from '../components/Ui';
import { addMonths, isValidMonth } from '../lib/dates';
import type { Currency } from '../lib/money';
import { invalidateFinancial, signOutLocally } from '../pages/queries';

interface PageDef { path: string; name: string; icon: string; subtitle: string }

export const PAGES: PageDef[] = [
  { path: '/', name: 'Resumen', icon: '◈', subtitle: 'Un lugar para entender dónde estás y planificar lo que viene.' },
  { path: '/movimientos', name: 'Movimientos', icon: '↗', subtitle: 'Cada ingreso y cada gasto, en un mismo lugar.' },
  { path: '/tarjetas', name: 'Tarjetas y cuotas', icon: '▤', subtitle: 'Tus compras distribuidas mes a mes.' },
  { path: '/prestamos', name: 'Préstamos', icon: '◎', subtitle: 'Seguí tus cuotas pendientes sin perder de vista el total.' },
  { path: '/proyeccion', name: 'Proyección', icon: '▥', subtitle: 'Una mirada a los compromisos que vienen.' },
];
const SETTINGS: PageDef = { path: '/configuracion', name: 'Configuración', icon: '⚙', subtitle: 'Cuentas, categorías, seguridad y respaldos.' };

/** ¿Ya hay registros propios (movimientos o planes, de cualquier mes y moneda)? */
async function hasRecords(): Promise<boolean> {
  const [entries, plans] = await Promise.all([api<Page<Entry>>('GET', '/entries?pageSize=1'), api<Plan[]>('GET', '/plans')]);
  return entries.total > 0 || plans.length > 0;
}

/**
 * Datos de ejemplo (solo desarrollo, opt-in): nunca se mezclan en silencio con los reales (docs/01).
 * Si ya hay registros, o no se puede saber, se pide confirmación con el modal de la app.
 */
export async function confirmDemoLoad(existing: () => Promise<boolean>, confirm: ReturnType<typeof useUi>['confirm']): Promise<boolean> {
  let hasData = true;
  try {
    hasData = await existing();
  } catch {
    // Ante la duda se pregunta.
  }
  if (!hasData) return true;
  return confirm({
    title: 'Agregar datos de ejemplo',
    message: 'Ya tenés registros. Esto agregará movimientos ficticios (marcados «· ejemplo») junto a los tuyos. ¿Continuar?',
    confirmLabel: 'Agregar ejemplos',
  });
}

export function Shell({ me }: { me: Me }) {
  const { month, setMonth, currency, setCurrency, openRecord, toast, confirm } = useUi();
  const location = useLocation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const page = [...PAGES, SETTINGS].find((p) => p.path === location.pathname) ?? PAGES[0]!;
  const isSettings = page === SETTINGS;

  useEffect(() => {
    document.title = `${page.name} — Álvarez Finanzas`;
  }, [page]);

  const logout = async () => {
    try {
      await api('POST', '/auth/logout');
    } finally {
      resetCsrf();
      signOutLocally(qc);
    }
  };

  const loadDemo = async () => {
    if (!(await confirmDemoLoad(hasRecords, confirm))) return;
    try {
      await api('POST', '/dev/demo', { month, currency });
      await invalidateFinancial(qc);
      toast('Ejemplos agregados: podés eliminarlos en Movimientos.');
    } catch (e) {
      toast(e instanceof ApiError ? e.title : 'No se pudieron agregar los ejemplos.', { error: true });
    }
  };

  return (
    <>
      <aside>
        <a className="brand" href="/" onClick={(e) => { e.preventDefault(); navigate('/'); }}>
          <b>á<span>•</span></b> ÁLVAREZ <small>FINANZAS PERSONALES</small>
        </a>
        <div className="navlabel">TU ESPACIO</div>
        <nav aria-label="Secciones">
          {PAGES.map((p) => (
            <button key={p.path} className={p === page ? 'active' : undefined} aria-current={p === page ? 'page' : undefined} onClick={() => navigate(p.path)}>
              {p.icon} <span>{p.name}</span>
            </button>
          ))}
        </nav>
        <div className="sidebottom">
          <SaveStatus />
          <p>Tu espacio, tus números.<br />Sin conexiones bancarias.</p>
          <button className={isSettings ? 'settings-link active' : 'settings-link'} onClick={() => navigate('/configuracion')}>⚙ Configuración</button>
          <button onClick={() => navigate('/configuracion#respaldo')}>↓ Exportar respaldo</button>
          <button onClick={() => navigate('/configuracion#importar')}>↑ Restaurar respaldo</button>
          <button onClick={logout}>⎋ Cerrar sesión</button>
        </div>
      </aside>
      <main>
        <header>
          <div className="breadcrumb">Mi espacio <span>/</span> <span>{page.name}</span></div>
          <button className="avatar" title={`${me.user?.displayName ?? ''} — editar perfil`} aria-label="Perfil y configuración" onClick={() => navigate('/configuracion#perfil')}>
            {me.user?.initials}
          </button>
        </header>
        <section className="heading">
          <div>
            <div className="eyebrow">MENOS RUIDO. MÁS CLARIDAD.</div>
            <h1>{page === PAGES[0] ? 'Tus finanzas, en orden' : page.name}<span>.</span></h1>
            <p>{page.subtitle}</p>
          </div>
          <button className="primary" onClick={() => openRecord({ mode: 'create', type: location.pathname === '/tarjetas' ? 'card' : location.pathname === '/prestamos' ? 'loan' : 'expense' })}>
            ＋ Agregar movimiento
          </button>
        </section>
        {!isSettings && (
          <div className="toolbar">
            <div className="seg">
              <button aria-label="Mes anterior" onClick={() => setMonth(addMonths(month, -1))}>‹</button>
              <input type="month" aria-label="Mes" value={month} onChange={(e) => isValidMonth(e.target.value) && setMonth(e.target.value)} />
              <button aria-label="Mes siguiente" onClick={() => setMonth(addMonths(month, 1))}>›</button>
            </div>
            <select aria-label="Moneda" value={currency} onChange={(e) => setCurrency(e.target.value as Currency)}>
              <option>ARS</option>
              <option>USD</option>
            </select>
            <span className="privacy">◌ Espacio privado</span>
          </div>
        )}
        <div id="content">
          <Outlet />
        </div>
        <footer>
          <span>ÁLVAREZ / Finanzas</span>
          <span>{me.demoEnabled ? 'Los importes de ejemplo no representan tus finanzas reales.' : 'Tus datos viven en tu servidor. Sin conexiones bancarias ni terceros.'}</span>
          {me.demoEnabled ? <button onClick={loadDemo}>Cargar ejemplo</button> : <span>{currency}</span>}
        </footer>
      </main>
    </>
  );
}

/** Estado real de conexión y guardado contra el servidor (reemplaza «Guardado en este navegador»). */
function SaveStatus() {
  const mutating = useIsMutating();
  const [online, setOnline] = useState(navigator.onLine);
  const [serverOk, setServerOk] = useState(true);

  useEffect(() => {
    const on = () => setOnline(true);
    const off = () => setOnline(false);
    window.addEventListener('online', on);
    window.addEventListener('offline', off);
    let alive = true;
    const ping = async () => {
      try {
        const r = await fetch('/api/health', { cache: 'no-store' });
        if (alive) setServerOk(r.ok);
      } catch {
        if (alive) setServerOk(false);
      }
    };
    void ping();
    const id = window.setInterval(ping, 60_000);
    return () => {
      alive = false;
      window.clearInterval(id);
      window.removeEventListener('online', on);
      window.removeEventListener('offline', off);
    };
  }, []);

  if (!online || !serverOk) return <div className="localdot offline" role="status">● Sin conexión con el servidor</div>;
  if (mutating > 0) return <div className="localdot saving" role="status">● Guardando…</div>;
  return <div className="localdot" role="status">● Conectado · guardado en tu servidor</div>;
}
