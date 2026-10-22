import { useEffect, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Occurrence, Page } from '../api/types';
import { OccurrenceTable, QueryState } from '../components/Records';
import { useUi } from '../components/Ui';

const PAGE_SIZE = 25;

export function MovementsPage() {
  const { month, currency } = useUi();
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  const [type, setType] = useState('all');
  const [page, setPage] = useState(1);

  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(search.trim()), 250);
    return () => window.clearTimeout(id);
  }, [search]);
  useEffect(() => setPage(1), [debounced, type, month, currency]);

  const q = useQuery({
    queryKey: ['movements', month, currency, debounced, type, page],
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ month, currency, type, page: String(page), pageSize: String(PAGE_SIZE) });
      if (debounced) params.set('q', debounced);
      return api<Page<Occurrence>>('GET', `/movements?${params}`, undefined, { signal });
    },
    placeholderData: keepPreviousData,
  });
  const pages = q.data ? Math.max(1, Math.ceil(q.data.total / PAGE_SIZE)) : 1;

  return (
    <section className="panel">
      <div className="panelhead">
        <h2>Movimientos</h2>
        <div className="tools">
          <select aria-label="Filtrar por tipo" value={type} onChange={(e) => setType(e.target.value)}>
            <option value="all">Todos</option>
            <option value="income">Ingresos</option>
            <option value="expense">Gastos</option>
            <option value="card">Cuotas de tarjeta</option>
            <option value="loan">Cuotas de préstamos</option>
          </select>
          <input className="search" type="search" placeholder="Buscar movimiento…" aria-label="Buscar movimiento" maxLength={90} value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>
      {q.data ? (
        <>
          {q.data.total === 0 && (debounced || type !== 'all') ? (
            <div className="empty">No hay movimientos que coincidan con la búsqueda.</div>
          ) : (
            <OccurrenceTable rows={q.data.items} currency={currency} />
          )}
          {q.data.total > PAGE_SIZE && (
            <div className="pager">
              <span>{q.data.total} movimientos · página {page} de {pages}</span>
              <div>
                <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>‹ Anterior</button>
                <button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>Siguiente ›</button>
              </div>
            </div>
          )}
        </>
      ) : (
        <QueryState isPending={q.isPending} error={q.error} onRetry={() => q.refetch()} />
      )}
    </section>
  );
}
