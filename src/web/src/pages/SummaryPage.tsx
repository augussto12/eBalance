import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { api } from '../api/client';
import type { Dashboard } from '../api/types';
import { OccurrenceTable, QueryState } from '../components/Records';
import { useUi } from '../components/Ui';
import { monthShort } from '../lib/dates';
import { formatMoney, ratio, type Currency } from '../lib/money';

export function SummaryPage() {
  const { month, currency } = useUi();
  const navigate = useNavigate();
  const q = useQuery({
    queryKey: ['dashboard', month, currency],
    queryFn: ({ signal }) => api<Dashboard>('GET', `/dashboard?month=${month}&currency=${currency}`, undefined, { signal }),
  });
  if (!q.data) return <section className="panel"><QueryState isPending={q.isPending} error={q.error} onRetry={() => q.refetch()} /></section>;
  const d = q.data;
  const t = d.totals;
  const balanceNegative = BigInt(t.balanceMinor) < 0n;

  return (
    <>
      <div className="stats">
        <div className="stat">
          <div className="label">Balance del mes <span>↗</span></div>
          <strong className={balanceNegative ? 'red' : undefined}>{formatMoney(t.balanceMinor, currency)}</strong>
          <small>Ingresos menos gastos y cuotas. No es saldo bancario disponible.</small>
        </div>
        <div className="stat">
          <div className="label">Ingresos <span className="green">↙</span></div>
          <strong>{formatMoney(t.incomeMinor, currency)}</strong>
          <small>Ingresos registrados este mes</small>
        </div>
        <div className="stat">
          <div className="label">Gastos y cuotas <span>↗</span></div>
          <strong>{formatMoney(t.expenseMinor, currency)}</strong>
          <small>Incluye tarjetas y préstamos</small>
        </div>
        <div className="stat">
          <div className="label">Comprometido en cuotas <span>▤</span></div>
          <strong>{formatMoney(t.installmentsMinor, currency)}</strong>
          <small>Incluido en gastos, no se suma otra vez</small>
        </div>
      </div>
      <div className="grid">
        <section className="panel">
          <div className="panelhead">
            <div>
              <h2>Tu ritmo financiero</h2>
              <p>Ingresos y gastos registrados</p>
            </div>
            <span className="chip">Últimos 6 meses</span>
          </div>
          <Chart series={d.series} currency={currency} />
          <div className="legend"><span><i></i>Ingresos</span><span><i className="dim"></i>Gastos y cuotas</span></div>
        </section>
        <section className="panel">
          <h2>¿A dónde va tu plata?</h2>
          <p>Distribución de los gastos del mes</p>
          {d.categories.length === 0 ? (
            <div className="empty">Todavía no hay gastos este mes.</div>
          ) : (
            d.categories.map((c) => (
              <div className="category" key={c.name}>
                <div><span>{c.name}</span><span>{formatMoney(c.amountMinor, currency)}</span></div>
                <section className="track">
                  <svg viewBox="0 0 1000 5" preserveAspectRatio="none" aria-hidden="true">
                    <rect x="0" y="0" height="5" width={Math.round(ratio(c.amountMinor, t.expenseMinor) * 1000)} />
                  </svg>
                </section>
              </div>
            ))
          )}
        </section>
      </div>
      <section className="panel wide">
        <div className="panelhead">
          <h2>Últimos movimientos</h2>
          <button onClick={() => navigate('/movimientos')}>Ver todos ↗</button>
        </div>
        <OccurrenceTable rows={d.recent} currency={currency} />
      </section>
    </>
  );
}

/** Barras en SVG (alturas como atributos, sin estilos inline): compatible con CSP style-src 'self'. */
function Chart({ series, currency }: { series: Dashboard['series']; currency: Currency }) {
  let max = 1n;
  for (const s of series) {
    for (const v of [BigInt(s.incomeMinor), BigInt(s.expenseMinor)]) if (v > max) max = v;
  }
  const H = 140;
  const h = (v: string) => Math.max(2, Math.round(ratio(v, max) * H));
  return (
    <div className="chart" role="img" aria-label="Ingresos y gastos de los últimos seis meses">
      {series.map((s) => (
        <div className="barcol" key={s.month}>
          <div className="bars">
            <svg viewBox={`0 0 43 ${H}`} aria-hidden="true">
              <rect className="in" x="0" width="19" y={H - h(s.incomeMinor)} height={h(s.incomeMinor)} rx="5">
                <title>{`Ingresos: ${formatMoney(s.incomeMinor, currency)}`}</title>
              </rect>
              <rect className="out" x="24" width="19" y={H - h(s.expenseMinor)} height={h(s.expenseMinor)} rx="5">
                <title>{`Gastos: ${formatMoney(s.expenseMinor, currency)}`}</title>
              </rect>
              {/* Base recta como en el prototipo (border-radius solo arriba). */}
              <rect className="in" x="0" width="19" y={H - Math.min(5, h(s.incomeMinor))} height={Math.min(5, h(s.incomeMinor))} />
              <rect className="out" x="24" width="19" y={H - Math.min(5, h(s.expenseMinor))} height={Math.min(5, h(s.expenseMinor))} />
            </svg>
          </div>
          <small>{monthShort(s.month)}</small>
        </div>
      ))}
    </div>
  );
}
