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

// formatToParts arma el texto a mano: dateStyle 'short' de es-AR daba "5/10/26, 12:04 p. m." (no dd/MM/yyyy ni 24 h).
const dateTimeParts = new Intl.DateTimeFormat('en-CA', {
  timeZone: ZONE, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});

/** Instante ISO (UTC) → "dd/MM/yyyy HH:mm", 24 h, en hora de Buenos Aires. Ej.: "2026-10-05T15:04:00Z" → "05/10/2026 12:04". */
export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  const parts = dateTimeParts.formatToParts(date);
  const get = (t: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === t)?.value ?? '';
  return `${get('day')}/${get('month')}/${get('year')} ${get('hour')}:${get('minute')}`;
}

export function isValidMonth(m: string): boolean {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(m);
}
