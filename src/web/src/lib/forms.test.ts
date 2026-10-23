import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import { PASSWORD_MAX_LENGTH, mapFieldErrors, newPasswordErrors, passwordTooLong, problemErrors } from './forms';

describe('errores por campo', () => {
  it('reparte cada clave del servidor en su campo y no pierde las que no tienen campo', () => {
    const r = mapFieldErrors(
      { amountMinor: ['El importe debe ser positivo.'], firstDueDate: ['Fecha no válida.'], date: ['Otra.'], version: ['Falta la versión.'] },
      { amountMinor: 'amount', date: 'date', firstDueDate: 'date' } as const,
    );
    expect(r.fields).toEqual({ amount: ['El importe debe ser positivo.'], date: ['Fecha no válida.', 'Otra.'] });
    expect(r.other).toEqual(['Falta la versión.']);
  });

  it('problemErrors: título + mensajes sin campo como error general; fallback si no es de la API', () => {
    const e = new ApiError(400, { title: 'Revisá los datos ingresados.', errors: { name: ['El nombre es obligatorio.'], q: ['Búsqueda demasiado larga.'] } });
    expect(problemErrors(e, { name: 'name' } as const, 'x')).toEqual({
      fields: { name: ['El nombre es obligatorio.'] },
      form: 'Revisá los datos ingresados. Búsqueda demasiado larga.',
    });
    expect(problemErrors(new Error('boom'), { name: 'name' } as const, 'No se pudo guardar.')).toEqual({ fields: {}, form: 'No se pudo guardar.' });
  });
});

describe('contraseñas: sin recortes silenciosos (docs/03)', () => {
  it('128 caracteres es válido; 129 da error con la longitud, sin recortar', () => {
    expect(PASSWORD_MAX_LENGTH).toBe(128);
    expect(passwordTooLong('a'.repeat(128))).toBeNull();
    const msg = passwordTooLong('a'.repeat(150));
    expect(msg).toContain('hasta 128 caracteres');
    expect(msg).toContain('150');
  });

  it('cuenta igual que el servidor (unidades UTF-16 de string.Length)', () => {
    // 64 emojis = 128 unidades UTF-16: válido. 65 = 130: demasiado larga.
    expect(passwordTooLong('🔑'.repeat(64))).toBeNull();
    expect(passwordTooLong('🔑'.repeat(65))).not.toBeNull();
  });

  it('contraseña nueva: solo longitud 15..128 y coincidencia, sin reglas de símbolos', () => {
    expect(newPasswordErrors('frase larga sin simbolos', 'frase larga sin simbolos')).toEqual({});
    expect(newPasswordErrors('corta', 'corta').next?.[0]).toContain('al menos 15');
    expect(newPasswordErrors('x'.repeat(129), 'x'.repeat(129)).next?.[0]).toContain('hasta 128');
    expect(newPasswordErrors('frase larga sin simbolos', 'otra frase distinta!!').repeat).toEqual(['Las contraseñas nuevas no coinciden.']);
  });
});
