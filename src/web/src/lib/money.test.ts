import { describe, expect, it } from 'vitest';
import { distribute, formatMoney, minorToInput, parseAmountInput } from './money';
import { addMonths, formatDate, monthShort } from './dates';

describe('parseAmountInput', () => {
  it.each([
    ['480000', '48000000'],
    ['480.000', '48000000'],
    ['1.234,56', '123456'],
    ['1234,5', '123450'],
    ['1234.56', '123456'],
    ['0,01', '1'],
    ['$ 2.100.000', '210000000'],
  ])('%s → %s', (input, minor) => {
    expect(parseAmountInput(input)).toEqual({ ok: true, minor });
  });

  it.each(['1,234', '0', '-5', 'abc', '1.23.4', '10000000000000', '1,2,3', ''])('rechaza %s', (input) => {
    expect(parseAmountInput(input).ok).toBe(false);
  });
});

describe('formato y distribución', () => {
  it('formatea en es-AR sin decimales cuando no hay centavos', () => {
    expect(formatMoney('210000000', 'ARS').replace(/\s/g, ' ')).toBe('$ 2.100.000');
    expect(formatMoney('123456', 'ARS').replace(/\s/g, ' ')).toBe('$ 1.234,56');
    expect(formatMoney('99999999999999', 'USD')).toContain('999.999.999.999,99');
  });

  it('distribuye exacto como el servidor', () => {
    expect(distribute(10000n, 3)).toEqual([3334n, 3333n, 3333n]);
  });

  it('precarga importes para edición', () => {
    expect(minorToInput('123456')).toBe('1234,56');
    expect(minorToInput('48000000')).toBe('480000');
  });
});

describe('fechas', () => {
  it('no corre fechas por zona horaria', () => {
    expect(formatDate('2026-01-31')).toBe('31/01/2026');
    expect(addMonths('2026-12', 1)).toBe('2027-01');
    expect(addMonths('2027-01', -1)).toBe('2026-12');
    expect(monthShort('2026-10')).toBe('oct');
  });
});
