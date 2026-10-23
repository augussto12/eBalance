/** Paginación de listas del servidor (Movimientos, cuotas del mes). Sin React. */
import type { QueryKey } from '@tanstack/react-query';

/** Cantidad de páginas: al menos 1, también sin resultados. */
export const pageCount = (total: number, pageSize: number): number => Math.max(1, Math.ceil(total / pageSize));

/** La consulta anterior es la misma salvo la página (posición `pageIndex` de la clave). */
export function onlyPageChanged(prev: QueryKey, next: QueryKey, pageIndex: number): boolean {
  return prev.length === next.length && prev.every((part, i) => i === pageIndex || Object.is(part, next[i]));
}

/**
 * keepPreviousData acotado a un cambio de página: mientras carga la página nueva se sigue viendo la anterior de la
 * MISMA consulta. Si cambió un filtro (mes, moneda, tipo, búsqueda) no se muestran filas de otra consulta con el
 * formato de la nueva (p. ej. cuotas en ARS como US$, o cuotas de tarjeta bajo Préstamos): se ve «Cargando…».
 */
export function keepPreviousPage<T>(next: QueryKey, pageIndex: number) {
  return (previousData: T | undefined, previousQuery: { queryKey: QueryKey } | undefined): T | undefined =>
    previousQuery && onlyPageChanged(previousQuery.queryKey, next, pageIndex) ? previousData : undefined;
}

/**
 * Página a la que hay que volver si la actual quedó fuera de rango (p. ej. se borró lo último de la última página y
 * el servidor, que no recorta la página, devuelve items=[] con total>0), o null si está bien.
 */
export function clampedPage(page: number, pages: number): number | null {
  return page > pages ? pages : null;
}
