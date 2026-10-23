import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Occurrence, Page } from '../api/types';
import { OccurrenceTable, Pager, QueryState, RefetchError, usePageClamp } from '../components/Records';
import { useUi } from '../components/Ui';
import { keepPreviousPage, pageCount } from '../lib/paging';

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

  const queryKey = ['movements', month, currency, debounced, type, page];
  const q = useQuery<Page<Occurrence>>({
    queryKey,
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ month, currency, type, page: String(page), pageSize: String(PAGE_SIZE) });
      if (debounced) params.set('q', debounced);
      return api<Page<Occurrence>>('GET', `/movements?${params}`, undefined, { signal });
    },
    // Solo al cambiar de página se sigue viendo la anterior; con otro mes, moneda, tipo o búsqueda, «Cargando…».
    placeholderData: keepPreviousPage(queryKey, 5),
  });
  const pages = q.data ? pageCount(q.data.total, PAGE_SIZE) : 1;
  usePageClamp(page, pages, !!q.data && !q.isPlaceholderData, setPage);

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
          <RefetchError error={q.error} retrying={q.isFetching} onRetry={() => q.refetch()} />
          {q.data.total === 0 && (debounced || type !== 'all') ? (
            <div className="empty">No hay movimientos que coincidan con la búsqueda.</div>
          ) : (
            <OccurrenceTable rows={q.data.items} currency={currency} />
          )}
          <Pager page={page} pages={pages} total={q.data.total} noun="movimientos" onPage={setPage} />
        </>
      ) : (
        <QueryState isPending={q.isPending} error={q.error} onRetry={() => q.refetch()} />
      )}
    </section>
  );
}
