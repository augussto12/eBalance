import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { MonthTotals } from '../api/types';
import { QueryState } from '../components/Records';
import { useUi } from '../components/Ui';
import { monthLong } from '../lib/dates';
import { formatMoney } from '../lib/money';

export function ProjectionPage() {
  const { month, currency } = useUi();
  const q = useQuery({
    queryKey: ['projection', month, currency],
    queryFn: ({ signal }) => api<{ months: MonthTotals[] }>('GET', `/projection?startMonth=${month}&months=12&currency=${currency}`, undefined, { signal }),
  });
  return (
    <>
      <div className="notice">
        Esta proyección incluye únicamente ingresos, gastos y cuotas ya cargados. No adivina ingresos futuros ni calcula intereses nuevos. Los préstamos se registran por el total de sus cuotas. Las cuotas se cuentan en su mes aunque las hayas pagado por adelantado; el estado pagado/pendiente se muestra aparte.
      </div>
      <section className="panel">
        <h2>Los próximos 12 meses</h2>
        {q.data ? (
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Mes</th>
                  <th>Ingresos registrados</th>
                  <th>Gastos y cuotas</th>
                  <th>De los cuales, cuotas</th>
                  <th>Cuotas pendientes</th>
                  <th>Balance</th>
                </tr>
              </thead>
              <tbody>
                {q.data.months.map((m) => (
                  <tr key={m.month}>
                    <td>{monthLong(m.month)}</td>
                    <td>{formatMoney(m.incomeMinor, currency)}</td>
                    <td>{formatMoney(m.expenseMinor, currency)}</td>
                    <td>{formatMoney(m.installmentsMinor, currency)}</td>
                    <td>{formatMoney(m.installmentsPendingMinor, currency)}{m.installmentsPaidMinor !== '0' && <small>pagado {formatMoney(m.installmentsPaidMinor, currency)}</small>}</td>
                    <td className={BigInt(m.balanceMinor) >= 0n ? 'green' : 'red'}>{formatMoney(m.balanceMinor, currency)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <QueryState isPending={q.isPending} error={q.error} onRetry={() => q.refetch()} />
        )}
      </section>
    </>
  );
}
