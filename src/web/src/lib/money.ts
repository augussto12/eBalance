/**
 * Presentación de importes. La autoridad es el servidor: los importes viajan como string de unidades menores
 * (centavos) y acá se usan BigInt y strings decimales exactos. Nunca coma flotante.
 */
export type Currency = 'ARS' | 'USD';

const MAX_MINOR = 100_000_000_000_000n;

/** "123456" → "1234.56" (decimal exacto en texto). */
export function minorToDecimalString(minor: string | bigint): string {
  const v = typeof minor === 'bigint' ? minor : BigInt(minor);
  const negative = v < 0n;
  const abs = negative ? -v : v;
  const units = abs / 100n;
  const cents = abs % 100n;
  return `${negative ? '-' : ''}${units}.${cents.toString().padStart(2, '0')}`;
}

const formatters = new Map<string, Intl.NumberFormat>();

/** Formato es-AR. Sin decimales si son cero (como el prototipo); con dos si hay centavos. */
export function formatMoney(minor: string | bigint, currency: Currency): string {
  const v = typeof minor === 'bigint' ? minor : BigInt(minor);
  const hasCents = v % 100n !== 0n;
  const key = `${currency}|${hasCents}`;
  let f = formatters.get(key);
  if (!f) {
    f = new Intl.NumberFormat('es-AR', {
      style: 'currency',
      currency,
      minimumFractionDigits: hasCents ? 2 : 0,
      maximumFractionDigits: hasCents ? 2 : 0,
    });
    formatters.set(key, f);
  }
  // Intl.NumberFormat acepta strings decimales y los formatea con precisión exacta.
  return f.format(minorToDecimalString(v) as unknown as number);
}

export type ParseResult = { ok: true; minor: string } | { ok: false; error: string };

/**
 * Interpreta lo que escribe la persona: "1.234,56", "1234,56", "1234.56", "480000".
 * Coma = decimal. Si solo hay puntos: un punto con 1–2 dígitos finales es decimal; si no, separador de miles.
 * Más de dos decimales → error explícito (no se trunca).
 */
export function parseAmountInput(raw: string): ParseResult {
  const s = raw.trim().replace(/\s|\$/g, '');
  if (!s) return { ok: false, error: 'Ingresá un importe.' };
  if (!/^[0-9.,]+$/.test(s)) return { ok: false, error: 'Usá solo números, puntos y coma.' };
  let intPart: string;
  let frac = '';
  if (s.includes(',')) {
    const parts = s.split(',');
    if (parts.length > 2) return { ok: false, error: 'El importe tiene más de una coma.' };
    intPart = parts[0]!.replace(/\./g, '');
    frac = parts[1] ?? '';
  } else {
    const dots = s.split('.');
    if (dots.length === 2 && dots[1]!.length > 0 && dots[1]!.length <= 2) {
      intPart = dots[0]!;
      frac = dots[1]!;
    } else {
      if (dots.length > 1 && dots.slice(1).some((g) => g.length !== 3)) return { ok: false, error: 'Revisá los separadores de miles.' };
      intPart = dots.join('');
    }
  }
  if (frac.length > 2) return { ok: false, error: 'Máximo dos decimales.' };
  if (!/^\d*$/.test(intPart) || !/^\d*$/.test(frac)) return { ok: false, error: 'Importe no válido.' };
  if (intPart.length > 13) return { ok: false, error: 'El importe es demasiado grande.' };
  const minor = BigInt(intPart || '0') * 100n + BigInt((frac + '00').slice(0, 2));
  if (minor <= 0n) return { ok: false, error: 'El importe debe ser mayor a cero.' };
  if (minor > MAX_MINOR) return { ok: false, error: 'El importe supera 1.000.000.000.000,00.' };
  return { ok: true, minor: minor.toString() };
}

/** Para precargar el formulario al editar: "123456" → "1234,56". */
export function minorToInput(minor: string): string {
  const d = minorToDecimalString(minor);
  return d.endsWith('.00') ? d.slice(0, -3) : d.replace('.', ',');
}

export function sumMinor(values: Iterable<string>): bigint {
  let t = 0n;
  for (const v of values) t += BigInt(v);
  return t;
}

/** Distribución exacta (igual que el servidor): base = total DIV n, las primeras «resto» cuotas llevan +1. */
export function distribute(totalMinor: bigint, count: number): bigint[] {
  const n = BigInt(count);
  const base = totalMinor / n;
  const rest = totalMinor % n;
  return Array.from({ length: count }, (_, i) => (BigInt(i) < rest ? base + 1n : base));
}

/** Proporción 0..1 para barras, sin perder precisión relevante para un gráfico. */
export function ratio(part: string | bigint, whole: string | bigint): number {
  const p = BigInt(part);
  const w = BigInt(whole);
  if (w <= 0n || p <= 0n) return 0;
  return Number((p * 10_000n) / w) / 10_000;
}
