import { describe, expect, it } from 'vitest';
import { formatDate, formatDateTime } from './dates';

describe('formato de fechas visibles (dd/MM/yyyy, 24 h)', () => {
  it('fechas financieras: dd/MM/yyyy sin conversión de zona', () => {
    expect(formatDate('2026-10-05')).toBe('05/10/2026');
    expect(formatDate('2026-01-31')).toBe('31/01/2026');
  });

  it.each([
    // Instantes UTC del servidor → hora de Buenos Aires (UTC−3), siempre dd/MM/yyyy HH:mm.
    ['2026-10-05T15:04:00Z', '05/10/2026 12:04'],
    ['2026-10-05T03:00:00Z', '05/10/2026 00:00'], // medianoche: 00, nunca 24 ni «a. m.»
    ['2026-10-05T02:59:00Z', '04/10/2026 23:59'], // cambia el día por la zona
    ['2027-01-01T01:30:00Z', '31/12/2026 22:30'], // y el año
    ['2026-10-05T21:45:30.123456+00:00', '05/10/2026 18:45'], // formato de DateTimeOffset de .NET
  ])('%s → %s', (iso, expected) => {
    expect(formatDateTime(iso)).toBe(expected);
  });

  it('sin «p. m.», comas ni año de dos dígitos (antes: "5/10/26, 12:04 p. m.")', () => {
    const text = formatDateTime('2026-10-05T15:04:00Z');
    expect(text).toMatch(/^\d{2}\/\d{2}\/\d{4} \d{2}:\d{2}$/);
  });

  it('un valor inválido no rompe la pantalla', () => {
    expect(formatDateTime('no es una fecha')).toBe('—');
  });
});
