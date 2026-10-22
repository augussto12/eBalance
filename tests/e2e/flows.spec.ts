import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { createHmac } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import pixelmatch from 'pixelmatch';

/*
 * Flujos críticos sobre el stack real con datos FICTICIOS. Requiere un propietario creado con
 * infra/scripts/bootstrap-owner.sh (usuario/contraseña en E2E_USER / E2E_PASSWORD).
 */
const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../..');
const visualDir = resolve(repo, 'docs/visual');
const statePath = resolve(here, '.e2e-state.json');
const USER = process.env.E2E_USER ?? 'augusto';
const PASSWORD = process.env.E2E_PASSWORD ?? 'contraseña de prueba larga para e2e 2026';

// ---------- TOTP (RFC 6238) para el test ----------
function base32Decode(s: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of s.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}
function totp(secret: string, step: number): string {
  const msg = Buffer.alloc(8);
  msg.writeBigUInt64BE(BigInt(step));
  const h = createHmac('sha1', base32Decode(secret)).update(msg).digest();
  const o = h[h.length - 1]! & 0xf;
  const code = ((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000).toString();
  return code.padStart(6, '0');
}
let lastStep = 0;
/** Espera un timestep nuevo: el servidor rechaza reutilizar el mismo código. */
async function freshCode(secret: string): Promise<string> {
  for (;;) {
    const step = Math.floor(Date.now() / 1000 / 30);
    if (step > lastStep) {
      lastStep = step;
      return totp(secret, step);
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
}

// ---------- utilidades ----------
const month = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Argentina/Buenos_Aires', year: 'numeric', month: '2-digit' }).format(new Date());
const day5 = `${month}-05`;
const cspViolations: string[] = [];
const dialogs: string[] = [];

function watch(page: Page) {
  page.on('console', (m) => {
    if (/Content Security Policy|Refused to/i.test(m.text())) cspViolations.push(m.text());
  });
  page.on('dialog', async (d) => {
    dialogs.push(d.message());
    await d.dismiss();
  });
}

async function addRecord(page: Page, r: { type: string; concept: string; amount: string; category?: string; account?: string; newAccount?: { name: string; kind: string }; count?: string; date?: string }) {
  await page.getByRole('button', { name: '＋ Agregar movimiento' }).click();
  const dlg = page.locator('dialog[open]');
  await dlg.getByLabel('Tipo').selectOption(r.type);
  await dlg.getByLabel('Concepto').fill(r.concept);
  await dlg.locator('input[inputmode=decimal]').first().fill(r.amount);
  await dlg.locator('input[type=date]').first().fill(r.date ?? day5);
  if (r.category) await dlg.getByLabel('Categoría').selectOption({ label: r.category });
  if (r.newAccount) {
    await dlg.getByLabel('Cuenta / tarjeta').selectOption('__new__');
    await dlg.getByLabel('Nombre de la cuenta').fill(r.newAccount.name);
    await dlg.getByLabel('Tipo de cuenta').selectOption(r.newAccount.kind);
  } else if (r.account) {
    await dlg.getByLabel('Cuenta / tarjeta').selectOption({ label: r.account });
  }
  if (r.count) {
    await dlg.getByLabel('Cantidad de cuotas').fill(r.count);
    await expect(dlg.locator('.preview')).toContainText('Total calculado');
  }
  await dlg.getByRole('button', { name: 'Guardar registro' }).click();
  await expect(page.locator('dialog[open]')).toHaveCount(0);
  await expect(page.locator('#toast')).toContainText(/guardad/i);
}

async function stat(page: Page, label: string) {
  return page.locator('.stat').filter({ has: page.locator('.label', { hasText: new RegExp(`^${label}`) }) }).locator('strong');
}

/** Sirve las fuentes autoalojadas al prototipo de referencia (que pide Google Fonts) para comparar en igualdad. */
async function routeReferenceFonts(page: Page) {
  const fontDir = (pkg: string) => resolve(repo, 'src/web/node_modules/@fontsource', pkg, 'files');
  const faces = [
    ...[400, 500, 600, 700, 800].map((w) => ['Manrope', 'manrope', w] as const),
    ...[400, 500, 600, 700].map((w) => ['DM Sans', 'dm-sans', w] as const),
  ].map(([family, pkg, w]) => `@font-face{font-family:'${family}';font-weight:${w};font-style:normal;src:url('https://fonts.local/${pkg}/${pkg}-latin-${w}-normal.woff2') format('woff2')}`);
  await page.route('https://fonts.googleapis.com/**', (r) => r.fulfill({ contentType: 'text/css', body: faces.join('\n') }));
  await page.route('https://fonts.local/**', (r) => {
    const [, pkg, file] = new URL(r.request().url()).pathname.split('/');
    r.fulfill({ contentType: 'font/woff2', body: readFileSync(resolve(fontDir(pkg!), file!)) });
  });
}

/** Elementos fuera del viewport que no están dentro de un contenedor con scroll propio (diagnóstico). */
function offenders(page: Page): Promise<string> {
  return page.evaluate(() => {
    const out: string[] = [];
    for (const el of Array.from(document.querySelectorAll('body *'))) {
      const r = el.getBoundingClientRect();
      if (r.right <= window.innerWidth + 1) continue;
      let p = el.parentElement, scrolled = false;
      while (p) { const o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll' || o === 'hidden') { scrolled = true; break; } p = p.parentElement; }
      if (!scrolled) out.push(`${el.tagName}.${el.className} → ${Math.round(r.right)}`);
      else if (out.length < 3 && r.width > 0) out.push(`(scroll) ${el.tagName}.${el.className}#${el.id} w=${Math.round(r.width)} r=${Math.round(r.right)} pos=${getComputedStyle(el).position}`);
    }
    return out.slice(0, 10).join(' | ');
  });
}

function compare(name: string, a: Buffer, b: Buffer): number {
  const ia = PNG.sync.read(a);
  const ib = PNG.sync.read(b);
  const w = Math.min(ia.width, ib.width);
  const h = Math.min(ia.height, ib.height);
  const crop = (img: PNG) => {
    const out = new PNG({ width: w, height: h });
    PNG.bitblt(img, out, 0, 0, w, h, 0, 0);
    return out;
  };
  const ca = crop(ia);
  const cb = crop(ib);
  const diff = new PNG({ width: w, height: h });
  const n = pixelmatch(ca.data, cb.data, diff.data, w, h, { threshold: 0.15 });
  writeFileSync(resolve(visualDir, `${name}-diff.png`), PNG.sync.write(diff));
  // Lado a lado: referencia | implementación.
  const side = new PNG({ width: w * 2 + 16, height: h });
  side.data.fill(0x30);
  PNG.bitblt(ca, side, 0, 0, w, h, 0, 0);
  PNG.bitblt(cb, side, 0, 0, w, h, w + 16, 0);
  writeFileSync(resolve(visualDir, `${name}-lado-a-lado.png`), PNG.sync.write(side));
  return n / (w * h);
}

// ---------- flujos ----------
test.describe.serial('Álvarez Finanzas', () => {
  let context: BrowserContext;
  let page: Page;
  let secret = '';

  test.beforeAll(async ({ browser }) => {
    mkdirSync(visualDir, { recursive: true });
    context = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
    page = await context.newPage();
    watch(page);
    if (existsSync(statePath)) secret = JSON.parse(readFileSync(statePath, 'utf8')).secret;
  });

  test.afterAll(async () => {
    await context.close();
  });

  test('login genérico, alta obligatoria del autenticador y códigos de recuperación', async () => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: /Ingresá a tus finanzas/ })).toBeVisible();
    await page.screenshot({ path: resolve(visualDir, 'login-1440.png') });

    await page.getByLabel('Usuario').fill(USER);
    await page.getByLabel('Contraseña').fill('contraseña incorrecta pero larga');
    await page.getByRole('button', { name: 'Ingresar' }).click();
    await expect(page.getByRole('alert')).toHaveText('Usuario o contraseña incorrectos.');

    await page.getByLabel('Contraseña').fill(PASSWORD);
    await page.getByRole('button', { name: 'Ingresar' }).click();
    await expect(page.getByRole('heading', { name: /Configurá tu autenticador|Confirmá que sos vos/ })).toBeVisible();
    if (await page.getByRole('heading', { name: /Configurá tu autenticador/ }).isVisible()) {
      // Contraseña correcta sin MFA: no hay acceso financiero.
      const r = await page.request.get('/api/v1/entries');
      expect(r.status()).toBe(401);
      await page.getByRole('button', { name: 'Generar código QR' }).click();
      await expect(page.locator('.qr img')).toBeVisible();
      secret = (await page.locator('.secret').textContent())!.trim();
      writeFileSync(statePath, JSON.stringify({ secret }));
      await page.getByLabel(/Código de 6 dígitos/).fill(await freshCode(secret));
      await page.getByRole('button', { name: 'Activar segundo factor' }).click();
      await expect(page.locator('.codes span')).toHaveCount(10);
      await page.getByRole('button', { name: 'Ya los guardé' }).click();
    } else {
      await expect(page.getByRole('heading', { name: /Confirmá que sos vos/ })).toBeVisible();
      await page.getByLabel('Código').fill(await freshCode(secret));
      await page.getByRole('button', { name: 'Verificar' }).click();
    }
    await expect(page.getByRole('heading', { name: /Tus finanzas, en orden/ })).toBeVisible();
    await expect(page.locator('.localdot')).toContainText('Conectado');
  });

  test('carga por formulario y KPIs del caso de aceptación', async () => {
    await page.screenshot({ path: resolve(visualDir, 'resumen-vacio-1440.png') });
    await addRecord(page, { type: 'income', concept: 'Sueldo · ejemplo', amount: '2.100.000', category: 'Trabajo', newAccount: { name: 'Banco', kind: 'bank' } });
    await addRecord(page, { type: 'expense', concept: 'Supermercado · ejemplo', amount: '86.500', category: 'Alimentación', newAccount: { name: 'Débito', kind: 'bank' } });
    await addRecord(page, { type: 'expense', concept: 'Internet · ejemplo', amount: '32000', category: 'Servicios', account: 'Banco' });
    await addRecord(page, { type: 'expense', concept: 'Café y merienda · ejemplo', amount: '12500', category: 'Ocio', newAccount: { name: 'Efectivo', kind: 'cash' } });
    await addRecord(page, { type: 'card', concept: 'Monitor · ejemplo', amount: '480000', category: 'Tecnología', newAccount: { name: 'Visa', kind: 'credit_card' }, count: '6' });
    await addRecord(page, { type: 'loan', concept: 'Préstamo · ejemplo', amount: '720000', category: 'Otros', account: 'Banco', count: '6' });

    await expect(await stat(page, 'Balance del mes')).toHaveText(/1\.769\.000/);
    await expect(await stat(page, 'Ingresos')).toHaveText(/2\.100\.000/);
    await expect(await stat(page, 'Gastos y cuotas')).toHaveText(/331\.000/);
    await expect(await stat(page, 'Comprometido en cuotas')).toHaveText(/200\.000/);
    await expect(page.locator('.category')).toHaveCount(5);
    await expect(page.locator('.chart .barcol')).toHaveCount(6);
  });

  test('comparación visual con el prototipo (1440×900 y 390×844)', async ({ browser }) => {
    const ref = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await routeReferenceFonts(ref);
    await ref.goto(`file://${resolve(repo, 'reference/index.html')}`);
    await ref.locator('#demo').click();
    // El botón está al pie: volver arriba y esperar a que se oculte el toast para comparar lo mismo.
    await ref.evaluate(() => window.scrollTo(0, 0));
    await ref.waitForTimeout(3800);
    await ref.evaluate(() => document.fonts.ready);
    const refDesktop = await ref.screenshot({ path: resolve(visualDir, 'referencia-resumen-1440.png') });
    await ref.setViewportSize({ width: 390, height: 844 });
    await ref.evaluate(() => window.scrollTo(0, 0));
    await ref.waitForTimeout(300);
    const refMobile = await ref.screenshot({ path: resolve(visualDir, 'referencia-resumen-390.png') });
    const refMobileFull = await ref.screenshot({ path: resolve(visualDir, 'referencia-resumen-390-completa.png'), fullPage: true });
    await ref.close();

    await page.goto('/');
    await expect(page.locator('.stat')).toHaveCount(4);
    await expect(page.locator('#toast.show')).toHaveCount(0, { timeout: 6000 });
    await page.evaluate(() => document.fonts.ready);
    const appDesktop = await page.screenshot({ path: resolve(visualDir, 'app-resumen-1440.png') });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(300);
    const appMobile = await page.screenshot({ path: resolve(visualDir, 'app-resumen-390.png') });
    const appMobileFull = await page.screenshot({ path: resolve(visualDir, 'app-resumen-390-completa.png'), fullPage: true });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, await offenders(page)).toBeLessThanOrEqual(0);
    await page.setViewportSize({ width: 1440, height: 900 });

    const results = {
      desktop1440: compare('resumen-1440', refDesktop, appDesktop),
      mobile390: compare('resumen-390', refMobile, appMobile),
      mobile390FullPage: compare('resumen-390-completa', refMobileFull, appMobileFull),
    };
    writeFileSync(resolve(visualDir, 'comparacion.json'), JSON.stringify(results, null, 2));
    console.log('Diferencia de píxeles vs. referencia:', results);
  });

  test('XSS en concepto se muestra como texto y no hay violaciones de CSP', async () => {
    const payload = '<img src=x onerror=alert(1)>';
    await addRecord(page, { type: 'expense', concept: payload, amount: '1', category: 'Otros' });
    await page.getByRole('navigation', { name: 'Secciones' }).getByRole('button', { name: 'Movimientos' }).click();
    await page.getByLabel('Buscar movimiento').fill('onerror');
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await expect(page.locator('tbody tr td').first()).toContainText(payload);
    expect(await page.locator('tbody img').count()).toBe(0);
    expect(dialogs).toEqual([]);
  });

  test('búsqueda, edición con conflicto 409 y eliminación con deshacer', async () => {
    await page.getByLabel('Buscar movimiento').fill('Café');
    await expect(page.locator('tbody tr')).toHaveCount(1);
    // Editar en la UI mientras otro cliente modifica el mismo registro → conflicto visible, sin sobrescritura.
    await page.getByRole('button', { name: 'Editar Café y merienda · ejemplo' }).click();
    const dlg = page.locator('dialog[open]');
    await expect(dlg.getByLabel('Concepto')).toHaveValue('Café y merienda · ejemplo');
    const list = await (await page.request.get(`/api/v1/entries?q=Caf%C3%A9`)).json();
    const entry = list.items[0];
    const csrf = (await (await page.request.get('/api/v1/auth/csrf')).json()).token;
    const other = await page.request.patch(`/api/v1/entries/${entry.id}`, {
      headers: { 'X-CSRF-TOKEN': csrf, Origin: new URL(page.url()).origin },
      data: { kind: 'expense', concept: 'Café (otra pestaña)', amountMinor: entry.amountMinor, currency: 'ARS', date: entry.date, version: entry.version, accountId: entry.accountId, categoryId: entry.categoryId },
    });
    expect(other.status()).toBe(200);
    await dlg.getByLabel('Concepto').fill('Café editado acá');
    await dlg.getByRole('button', { name: 'Guardar registro' }).click();
    await expect(dlg.locator('.notice.warn')).toContainText('cambió desde que lo abriste');
    await dlg.getByRole('button', { name: 'Cargar la versión actual' }).click();
    await expect(page.locator('dialog[open]').getByLabel('Concepto')).toHaveValue('Café (otra pestaña)');
    await page.locator('dialog[open]').getByRole('button', { name: 'Cerrar' }).click();

    await page.getByLabel('Buscar movimiento').fill('Café');
    await page.getByRole('button', { name: /Eliminar Café/ }).click();
    await page.locator('dialog[open]').getByRole('button', { name: 'Eliminar' }).click();
    await expect(page.locator('#toast')).toContainText('Registro eliminado');
    await expect(page.locator('tbody tr')).toHaveCount(0);
    await page.locator('#toast').getByRole('button', { name: 'Deshacer' }).click();
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await page.getByLabel('Buscar movimiento').fill('');
  });

  test('pago de tarjeta: reduce pendiente sin sumar gasto', async () => {
    await page.getByRole('navigation', { name: 'Secciones' }).getByRole('button', { name: 'Tarjetas y cuotas' }).click();
    await expect(page.locator('.panel.wide tbody tr')).toHaveCount(1);
    await page.getByRole('button', { name: 'Registrar pago', exact: true }).click();
    const dlg = page.locator('dialog[open]');
    await dlg.getByLabel('Qué pagás').selectOption({ label: 'Visa' });
    await expect(dlg.locator('.preview')).toContainText('480.000');
    await dlg.getByLabel('Desde la cuenta').selectOption({ label: 'Banco' });
    await expect(dlg.getByLabel(/Importe/)).toHaveValue('80000');
    await dlg.getByRole('button', { name: 'Registrar pago' }).click();
    await expect(page.locator('#toast')).toContainText('Pago registrado');
    await expect(page.locator('.panel.wide tbody tr').first()).toContainText('400.000');
    await page.screenshot({ path: resolve(visualDir, 'app-tarjetas-1440.png'), fullPage: true });
    await page.getByRole('navigation', { name: 'Secciones' }).getByRole('button', { name: 'Resumen' }).click();
    await expect(await stat(page, 'Gastos y cuotas')).toHaveText(/331\.001/); // 331.000 + el gasto XSS de $1
  });

  test('proyección de 12 meses: cuotas hasta el sexto mes y luego cero', async () => {
    await page.getByRole('navigation', { name: 'Secciones' }).getByRole('button', { name: 'Proyección' }).click();
    const rows = page.locator('tbody tr');
    await expect(rows).toHaveCount(12);
    await expect(rows.nth(1).locator('td').nth(2)).toHaveText(/200\.000/);
    await expect(rows.nth(6).locator('td').nth(2)).toHaveText(/\$\s?0/);
    await page.screenshot({ path: resolve(visualDir, 'app-proyeccion-1440.png'), fullPage: true });
  });

  test('exportación exige reautenticación; importación del respaldo del prototipo con vista previa', async () => {
    await page.getByRole('button', { name: '↓ Exportar respaldo' }).click();
    await page.getByRole('button', { name: '↓ Exportar respaldo completo' }).click();
    const dlg = page.locator('dialog[open]');
    await expect(dlg).toContainText('Confirmá que sos vos');
    await dlg.getByLabel('Contraseña').fill(PASSWORD);
    await dlg.getByLabel('Código de 6 dígitos').fill(await freshCode(secret));
    const download = page.waitForEvent('download');
    await dlg.getByRole('button', { name: 'Confirmar' }).click();
    // Tras la reautenticación se reintenta la exportación manualmente (la sesión quedó elevada unos minutos).
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    const file = await download.catch(async () => {
      const again = page.waitForEvent('download');
      await page.getByRole('button', { name: '↓ Exportar respaldo completo' }).click();
      return again;
    });
    const text = readFileSync((await file.path())!, 'utf8');
    expect(text).toContain('"format": "alvarez-finanzas-export"');
    expect(text).not.toContain(secret);

    const legacy = { version: 1, records: [
      { id: 'proto-1', type: 'expense', name: 'Nafta (prototipo)', amount: 45000.5, currency: 'ARS', date: day5, category: 'Transporte', account: 'Efectivo', count: 1 },
      { id: 'proto-2', type: 'card', name: 'Zapatillas (prototipo)', amount: 90000, currency: 'ARS', date: day5, category: 'Otros', account: 'Mastercard', count: 3 },
    ] };
    await page.locator('#importfile').setInputFiles({ name: 'alvarez-finanzas-respaldo.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(legacy)) });
    const preview = page.locator('.preview', { hasText: 'Vista previa' });
    await expect(preview).toContainText('2 nuevos');
    await expect(preview).toContainText('Mastercard (ARS)');
    await preview.getByLabel('Mastercard (ARS)').selectOption('credit_card');
    await page.screenshot({ path: resolve(visualDir, 'app-importacion-1440.png'), fullPage: true });
    await preview.getByRole('button', { name: 'Confirmar importación' }).click();
    await expect(page.locator('#toast')).toContainText('2 nuevos');
    // Misma carga otra vez: todo duplicado, nada nuevo.
    await page.locator('#importfile').setInputFiles({ name: 'alvarez-finanzas-respaldo.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(legacy)) });
    await expect(page.locator('.preview', { hasText: 'Vista previa' })).toContainText('0 nuevos, 2 ya importados');
    await page.locator('.preview', { hasText: 'Vista previa' }).getByRole('button', { name: 'Cancelar' }).click();
  });

  test('celular 390×844: navegación superior, sin desborde horizontal en todas las secciones', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    for (const [name, file] of [['Resumen', 'resumen'], ['Movimientos', 'movimientos'], ['Tarjetas y cuotas', 'tarjetas'], ['Préstamos', 'prestamos'], ['Proyección', 'proyeccion']] as const) {
      await page.getByRole('navigation', { name: 'Secciones' }).getByRole('button', { name }).click();
      await expect(page.locator('#content')).not.toBeEmpty();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, `desborde en ${name}: ${await offenders(page)}`).toBeLessThanOrEqual(0);
      await page.screenshot({ path: resolve(visualDir, `app-${file}-390.png`) });
    }
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test('logout limpia la sesión y los datos en memoria', async () => {
    await page.getByRole('button', { name: '⎋ Cerrar sesión' }).click();
    await expect(page.getByRole('heading', { name: /Ingresá a tus finanzas/ })).toBeVisible();
    expect((await page.request.get('/api/v1/entries')).status()).toBe(401);
    await page.goBack();
    await expect(page.getByRole('heading', { name: /Ingresá a tus finanzas/ })).toBeVisible();
    expect(await page.evaluate(() => Object.keys(localStorage).length + Object.keys(sessionStorage).length)).toBe(0);
  });

  test('sin violaciones de CSP ni diálogos durante toda la sesión', async () => {
    expect(cspViolations).toEqual([]);
    expect(dialogs).toEqual([]);
  });
});
