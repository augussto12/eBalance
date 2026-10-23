import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { LoginPage } from '../auth/AuthPages';
import { mapFieldErrors, type FieldErrors } from '../lib/forms';
import { PAYMENT_FIELD_MAP, PaymentDialog } from '../pages/PlansPage';
import { SettingsPage } from '../pages/SettingsPage';
import { danglingDescribedBy, render, seed, testClient, TEST_ME, text } from '../test/render';
import { PasswordField } from './PasswordField';
import { NEW_ACCOUNT_FIELD_MAP, RECORD_FIELD_MAP, RecordFormBody, empty, visibleFields, type FormState } from './RecordForm';

/** Todas las claves de validación que devuelven POST/PATCH /entries y /plans (EntryEndpoints, PlanEndpoints). */
const RECORD_SERVER_KEYS = [
  'kind', 'concept', 'amountMinor', 'currency', 'date', 'firstDueDate', 'accountId', 'categoryId', 'note', 'count', 'amountMode',
  'paidBeforeCount', 'remainingOnly', 'disbursementMinor', 'disbursementDate', 'disbursementAccountId', 'version',
];

const loanWithEverything: FormState = { ...empty('loan', 'ARS'), accountId: '__new__', ongoing: 'paid', disbursement: '1000', note: 'nota' };

/** Un mensaje distinto por campo para saber dónde aparece cada uno. */
const errorsFor = (fields: (keyof FormState)[]): FieldErrors<keyof FormState> =>
  Object.fromEntries(fields.map((f) => [f, [`Mensaje de ${f}.`]]));

const formErrorText = (html: string) => text(/<p class="formerror"[^>]*>(.*?)<\/p>/.exec(html)?.[1] ?? '');

describe('formulario de registro: cada error junto a su campo', () => {
  it('ninguna clave del servidor se pierde ni cae en otro campo (solo version queda como error general)', () => {
    const server = Object.fromEntries(RECORD_SERVER_KEYS.map((k) => [k, [`srv ${k}`]]));
    const { fields, other } = mapFieldErrors(server, RECORD_FIELD_MAP);
    expect(other).toEqual(['srv version']);
    expect(fields.concept).toEqual(['srv concept']); // antes las claves desconocidas iban a «concepto»
    expect(fields.date).toEqual(['srv date', 'srv firstDueDate']);
    expect(mapFieldErrors({ name: ['n'], kind: ['k'], currency: ['c'] }, NEW_ACCOUNT_FIELD_MAP).fields)
      .toEqual({ newAccountName: ['n'], newAccountKind: ['k'], currency: ['c'] });
  });

  it('todos los campos mapeados muestran su mensaje y aria-describedby apunta a un elemento existente', () => {
    const fields = [...new Set([...Object.values(RECORD_FIELD_MAP), ...Object.values(NEW_ACCOUNT_FIELD_MAP)])] as (keyof FormState)[];
    // Con este estado todos los campos están visibles.
    for (const f of fields) expect(visibleFields(loanWithEverything).has(f)).toBe(true);
    const html = render(<RecordFormBody initial={loanWithEverything} initialErrors={errorsFor(fields)} onClose={() => {}} />);

    expect(danglingDescribedBy(html)).toEqual([]);
    expect(html).not.toContain('aria-describedby="err-note"');
    for (const f of fields) {
      const id = new RegExp(`<span class="fielderror" id="([^"]+)" role="alert">Mensaje de ${f}\\.</span>`).exec(html)?.[1];
      expect(id, `sin mensaje junto al campo ${f}`).toBeTruthy();
      expect(html, `ningún control describe el error de ${f}`).toContain(`aria-describedby="${id}"`);
    }
    // Todo está junto a su campo: el mensaje general no repite ninguno.
    expect(formErrorText(html)).toBe('');
    // Los detalles con errores quedan abiertos para que el mensaje se vea.
    expect((html.match(/<details class="more" open="">/g) ?? []).length).toBe(2);
  });

  it('una sección plegable con un campo en error se abre para que el mensaje se vea', () => {
    const closed = render(<RecordFormBody initial={empty('loan', 'ARS')} onClose={() => {}} />);
    expect(closed).not.toContain('<details class="more" open="">');
    const html = render(<RecordFormBody initial={empty('loan', 'ARS')} initialErrors={errorsFor(['disbursementDate', 'note'])} onClose={() => {}} />);
    expect((html.match(/<details class="more" open="">/g) ?? []).length).toBe(2);
    expect(danglingDescribedBy(html)).toEqual([]);
  });

  it('un error de un campo que no se muestra en este estado va al mensaje general, no se descarta', () => {
    const html = render(<RecordFormBody initial={empty('expense', 'ARS')} initialErrors={errorsFor(['disbursementDate', 'concept'])} onClose={() => {}} />);
    expect(formErrorText(html)).toContain('Mensaje de disbursementDate.');
    expect(formErrorText(html)).not.toContain('Mensaje de concept.');
    expect(html).toMatch(/<span class="fielderror" id="[^"]+" role="alert">Mensaje de concept\.<\/span>/);
    expect(danglingDescribedBy(html)).toEqual([]);
  });

  it('sin errores no hay aria-invalid ni aria-describedby', () => {
    const html = render(<RecordFormBody initial={loanWithEverything} onClose={() => {}} />);
    expect(html).not.toContain('aria-invalid');
    expect(html).not.toContain('aria-describedby');
  });
});

describe('registrar pago: cada error junto a su campo', () => {
  /** Claves de validación de POST /payments (CashEndpoints.CreatePayment). */
  const PAYMENT_SERVER_KEYS = ['sourceAccountId', 'targetAccountId', 'planId', 'amountMinor', 'date', 'currency', 'note', 'idempotencyKey', 'allocations', 'allocations[0].amountMinor'];

  it('fecha, cuenta, destino e importe van a su campo; el resto (idempotencia, asignaciones, moneda, nota) al mensaje general', () => {
    const server = Object.fromEntries(PAYMENT_SERVER_KEYS.map((k) => [k, [`srv ${k}`]]));
    const { fields, other } = mapFieldErrors(server, PAYMENT_FIELD_MAP);
    expect(fields).toEqual({ source: ['srv sourceAccountId'], target: ['srv targetAccountId', 'srv planId'], amount: ['srv amountMinor'], date: ['srv date'] });
    expect(other).toEqual(['srv currency', 'srv note', 'srv idempotencyKey', 'srv allocations', 'srv allocations[0].amountMinor']);
  });

  it('cada campo muestra su mensaje, con aria-invalid y aria-describedby a un elemento existente (también la fecha)', () => {
    const fields = ['target', 'source', 'amount', 'date'] as const;
    const html = render(
      <PaymentDialog kind="card" currency="ARS" target={{}} plans={[]} onClose={() => {}} initialErrors={Object.fromEntries(fields.map((f) => [f, [`Mensaje de ${f}.`]]))} />,
    );
    expect(danglingDescribedBy(html)).toEqual([]);
    for (const f of fields) {
      const id = new RegExp(`<span class="fielderror" id="([^"]+)" role="alert">Mensaje de ${f}\\.</span>`).exec(html)?.[1];
      expect(id, `sin mensaje junto al campo ${f}`).toBeTruthy();
      expect(html, `ningún control describe el error de ${f}`).toMatch(new RegExp(`aria-invalid="true" aria-describedby="${id}"`));
    }
    expect(html).toMatch(/<input type="date"[^>]*aria-invalid="true"/);
  });

  it('sin errores no hay aria-invalid ni aria-describedby', () => {
    const html = render(<PaymentDialog kind="card" currency="ARS" target={{}} plans={[]} onClose={() => {}} />);
    expect(html).toContain('Registrar pago');
    expect(html).not.toContain('aria-invalid');
    expect(html).not.toContain('aria-describedby');
  });
});

describe('contraseñas sin recorte silencioso', () => {
  const passwordInputs = (html: string) => html.match(/<input[^>]*type="password"[^>]*>/g) ?? [];

  it('el campo no tiene maxlength (el navegador recortaría lo pegado)', () => {
    const html = renderToStaticMarkup(<PasswordField label="Contraseña" autoComplete="current-password" value={'a'.repeat(200)} onChange={() => {}} />);
    expect(passwordInputs(html)).toHaveLength(1);
    expect(html).not.toMatch(/maxlength/i);
    expect(html).toContain(`value="${'a'.repeat(200)}"`);
  });

  it('más de 128 caracteres: error junto al campo, asociado con aria-describedby', () => {
    const html = renderToStaticMarkup(<PasswordField label="Contraseña" autoComplete="current-password" value={'a'.repeat(129)} onChange={() => {}} />);
    expect(html).toContain('aria-invalid="true"');
    expect(danglingDescribedBy(html)).toEqual([]);
    expect(text(html)).toContain('La contraseña admite hasta 128 caracteres; la ingresada tiene 129.');
  });

  it('128 caracteres: sin error', () => {
    const html = renderToStaticMarkup(<PasswordField label="Contraseña" autoComplete="new-password" value={'a'.repeat(128)} onChange={() => {}} />);
    expect(html).not.toContain('fielderror');
    expect(html).not.toContain('aria-invalid');
  });

  it('errores del servidor se suman sin duplicar el de longitud', () => {
    const html = renderToStaticMarkup(<PasswordField label="Nueva" autoComplete="new-password" value="corta" errors={['La contraseña nueva debe tener al menos 15 caracteres.']} onChange={() => {}} />);
    expect(text(html)).toContain('al menos 15 caracteres');
    expect(danglingDescribedBy(html)).toEqual([]);
  });

  it('login, autenticación y cambio de contraseña: ningún campo de contraseña con maxlength', () => {
    const login = render(<LoginPage />);
    const settings = render(<SettingsPage me={TEST_ME} />);
    expect(passwordInputs(login)).toHaveLength(1);
    expect(passwordInputs(settings)).toHaveLength(3);
    for (const input of [...passwordInputs(login), ...passwordInputs(settings)]) expect(input).not.toMatch(/maxlength/i);
    // Gestores de contraseñas: autocomplete correcto en cada campo.
    expect(passwordInputs(login)[0]).toContain('autoComplete="current-password"');
    expect(passwordInputs(settings).map((i) => /autoComplete="([^"]+)"/.exec(i)?.[1])).toEqual(['current-password', 'new-password', 'new-password']);
  });
});

describe('configuración: fechas y horas visibles en dd/MM/yyyy HH:mm', () => {
  it('sesiones y papelera', () => {
    const qc = testClient();
    seed(qc, ['sessions'], [{ id: 's1', createdAt: '2026-10-05T12:00:00Z', lastSeenAt: '2026-10-05T15:04:00Z', expiresAt: '2026-10-06T00:00:00Z', userAgent: 'Firefox', ipPrefix: '10.0.0.0/24', current: false }]);
    seed(qc, ['trash'], [{ type: 'entry', id: 't1', concept: 'Borrado de prueba', currency: 'ARS', amountMinor: '100', date: '2026-10-01', deletedAt: '2026-10-05T03:00:00Z' }]);
    const html = text(render(<SettingsPage me={TEST_ME} />, qc));
    expect(html).toContain('última actividad 05/10/2026 12:04');
    expect(html).toContain('01/10/2026 · eliminado 05/10/2026 00:00');
    expect(html).not.toMatch(/\d{1,2}\/\d{1,2}\/\d{2},|[ap]\. ?m\./);
  });
});
