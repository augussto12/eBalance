/** Utilidades de formularios sin React: errores por campo y reglas de contraseña (el servidor igual valida todo). */
import { ApiError } from '../api/client';

export type FieldErrors<K extends string = string> = Partial<Record<K, string[]>>;

/**
 * Reparte los errores de validación del servidor (`errors` de ProblemDetails) entre los campos visibles.
 * Las claves sin campo propio (p. ej. `version`) no se pierden ni se cargan a otro campo: quedan como errores generales.
 */
export function mapFieldErrors<K extends string>(
  server: Record<string, string[]>,
  map: Readonly<Partial<Record<string, K>>>,
): { fields: FieldErrors<K>; other: string[] } {
  const fields: FieldErrors<K> = {};
  const other: string[] = [];
  for (const [key, messages] of Object.entries(server)) {
    const field = map[key];
    if (field) fields[field] = [...(fields[field] ?? []), ...messages];
    else other.push(...messages);
  }
  return { fields, other };
}

/**
 * Error de una petición → errores por campo + mensaje general (título del problema y mensajes sin campo propio).
 * Si el error no es una respuesta de la API (excepción local) se usa `fallback`.
 */
export function problemErrors<K extends string>(
  e: unknown,
  map: Readonly<Partial<Record<string, K>>>,
  fallback: string,
): { fields: FieldErrors<K>; form: string } {
  if (!(e instanceof ApiError)) return { fields: {}, form: fallback };
  const { fields, other } = mapFieldErrors(e.fieldErrors, map);
  return { fields, form: [e.title, ...other].join(' ') };
}

export const hasErrors = (errors: FieldErrors): boolean => Object.values(errors).some((e) => !!e?.length);

/** Mismos límites que el servidor (AuthEndpoints.PasswordMinLength/PasswordMaxLength, docs/03). */
export const PASSWORD_MIN_LENGTH = 15;
export const PASSWORD_MAX_LENGTH = 128;

/**
 * Contraseña demasiado larga. Los campos de contraseña NO usan maxLength: el navegador recortaría en silencio lo que
 * pega un gestor de contraseñas. En su lugar se avisa junto al campo y no se envía el formulario.
 * La longitud se cuenta igual que en el servidor (unidades UTF-16, string.Length de .NET).
 */
export function passwordTooLong(value: string): string | null {
  return value.length > PASSWORD_MAX_LENGTH
    ? `La contraseña admite hasta ${PASSWORD_MAX_LENGTH} caracteres; la ingresada tiene ${value.length}. No se recorta: revisala o acortala.`
    : null;
}

/** Reglas de la contraseña nueva: solo longitud (15 a 128), sin reglas arbitrarias de símbolos. */
export function newPasswordErrors(next: string, repeat: string): FieldErrors<'next' | 'repeat'> {
  const errors: FieldErrors<'next' | 'repeat'> = {};
  const tooLong = passwordTooLong(next);
  if (tooLong) errors.next = [tooLong];
  else if (next.length < PASSWORD_MIN_LENGTH) errors.next = [`La contraseña nueva debe tener al menos ${PASSWORD_MIN_LENGTH} caracteres.`];
  if (next !== repeat) errors.repeat = ['Las contraseñas nuevas no coinciden.'];
  return errors;
}
