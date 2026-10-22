/**
 * Cliente HTTP del mismo origen. Cookies HttpOnly de sesión (nunca tokens en localStorage/URL).
 * El token CSRF vive solo en memoria y se envía en X-CSRF-TOKEN en métodos que modifican estado.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly fieldErrors: Record<string, string[]>;
  readonly details: string[];
  readonly current: unknown;
  readonly code?: string;

  constructor(status: number, body: ProblemBody | null) {
    super(body?.title ?? `Error ${status}`);
    this.status = status;
    this.title = body?.title ?? defaultTitle(status);
    this.fieldErrors = body?.errors && !Array.isArray(body.errors) ? body.errors : {};
    this.details = Array.isArray(body?.errors) ? body.errors : [];
    this.current = body?.current;
    this.code = body?.code;
  }
}

interface ProblemBody {
  title?: string;
  errors?: Record<string, string[]> | string[];
  current?: unknown;
  code?: string;
}

function defaultTitle(status: number): string {
  if (status === 0) return 'No hay conexión con el servidor.';
  if (status === 401) return 'Tu sesión terminó. Volvé a ingresar.';
  if (status === 403) return 'No tenés permiso para esta acción.';
  if (status === 404) return 'No encontrado.';
  if (status === 409) return 'Hubo un conflicto con otro cambio.';
  if (status === 429) return 'Demasiados intentos. Esperá un momento.';
  return 'Ocurrió un error inesperado. Probá de nuevo.';
}

let csrfToken: string | null = null;
let unauthorizedHandler: (() => void) | null = null;

export function onUnauthorized(handler: () => void): void {
  unauthorizedHandler = handler;
}

/** Tras un cambio de identidad (login, MFA, logout, reautenticación) el token cambia. */
export function resetCsrf(): void {
  csrfToken = null;
}

async function ensureCsrf(): Promise<string> {
  if (csrfToken) return csrfToken;
  const r = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store' });
  if (!r.ok) throw new ApiError(r.status, null);
  csrfToken = ((await r.json()) as { token: string }).token;
  return csrfToken;
}

export interface RequestOptions {
  headers?: Record<string, string>;
  raw?: BodyInit;
  contentType?: string;
  /** No disparar el manejador global de 401 (pantallas de login/MFA). */
  quiet401?: boolean;
  signal?: AbortSignal;
}

export async function api<T>(method: string, path: string, body?: unknown, options: RequestOptions = {}, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
  const unsafe = method !== 'GET' && method !== 'HEAD';
  let payload: BodyInit | undefined;
  if (options.raw !== undefined) {
    payload = options.raw;
    headers['Content-Type'] = options.contentType ?? 'application/json';
  } else if (body !== undefined) {
    payload = JSON.stringify(body);
    headers['Content-Type'] = 'application/json';
  }
  if (unsafe) headers['X-CSRF-TOKEN'] = await ensureCsrf();

  let response: Response;
  try {
    response = await fetch(`/api/v1${path}`, {
      method,
      headers,
      body: payload,
      credentials: 'same-origin',
      cache: 'no-store',
      signal: options.signal,
    });
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e;
    throw new ApiError(0, null);
  }

  if (response.status === 204) return undefined as T;
  const text = await response.text();
  const json = text ? safeJson(text) : null;
  if (response.ok) return json as T;

  const problem = (json ?? null) as ProblemBody | null;
  if (response.status === 403 && problem?.code === 'csrf' && !retried) {
    resetCsrf();
    return api<T>(method, path, body, options, true);
  }
  if (response.status === 401 && !options.quiet401) {
    resetCsrf();
    unauthorizedHandler?.();
  }
  throw new ApiError(response.status, problem);
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

/** Descarga autenticada (exportación). El archivo se entrega al navegador sin guardarlo en el servidor. */
export async function download(path: string, fallbackName: string): Promise<void> {
  const r = await fetch(`/api/v1${path}`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': await ensureCsrf() },
    credentials: 'same-origin',
    cache: 'no-store',
  });
  if (!r.ok) {
    const text = await r.text();
    throw new ApiError(r.status, text ? (safeJson(text) as ProblemBody) : null);
  }
  const blob = await r.blob();
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(r.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName;
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = decodeURIComponent(name);
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Clave de idempotencia aleatoria criptográfica (nunca Math.random). */
export function idempotencyKey(): string {
  return crypto.randomUUID();
}
