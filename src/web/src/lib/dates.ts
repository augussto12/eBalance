/** Fechas financieras = fechas locales "AAAA-MM-DD" sin conversión UTC. */
const ZONE = 'America/Argentina/Buenos_Aires';

export function todayIso(): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date());
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

export const currentMonth = (): string => todayIso().slice(0, 7);

export function monthIndex(month: string): number {
  return Number(month.slice(0, 4)) * 12 + Number(month.slice(5, 7)) - 1;
}

export function monthAt(index: number): string {
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}`;
}

export const addMonths = (month: string, delta: number): string => monthAt(monthIndex(month) + delta);

/** "2026-10-05" → "05/10/2026". */
export function formatDate(iso: string): string {
  const [y, m, d] = iso.split('-');
  return `${d}/${m}/${y}`;
}

const shortMonth = new Intl.DateTimeFormat('es-AR', { month: 'short', timeZone: 'UTC' });
const longMonth = new Intl.DateTimeFormat('es-AR', { month: 'long', year: 'numeric', timeZone: 'UTC' });

export function monthShort(month: string): string {
  return shortMonth.format(Date.UTC(Number(month.slice(0, 4)), Number(month.slice(5, 7)) - 1, 2)).replace('.', '');
}

export function monthLong(month: string): string {
  return longMonth.format(Date.UTC(Number(month.slice(0, 4)), Number(month.slice(5, 7)) - 1, 2));
}

export function formatDateTime(iso: string): string {
  return new Intl.DateTimeFormat('es-AR', { timeZone: ZONE, dateStyle: 'short', timeStyle: 'short' }).format(new Date(iso));
}

export function isValidMonth(m: string): boolean {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(m);
}
