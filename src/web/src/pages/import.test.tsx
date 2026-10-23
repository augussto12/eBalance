import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ImportPreview } from '../api/types';
import { text } from '../test/render';
import { ImportConfirmDialog } from './SettingsPage';

const preview = (news: number): ImportPreview => ({
  batchId: 'b1',
  expiresAt: '2026-10-05T15:34:00Z',
  summary: { format: 'legacy-v1', total: 2, new: news, duplicates: 2 - news, conflicts: 0, byTarget: {}, byCurrency: { ARS: 2 } },
  conflicts: [],
  warnings: [],
  accountsToCreate: [{ key: 'ARS:Mastercard', name: 'Mastercard', currency: 'ARS', usedByPlans: true }],
  categoriesToCreate: ['Transporte'],
  sample: [{ target: 'entry', type: 'expense', concept: 'Nafta (prueba)', amountMinor: '4500050', currency: 'ARS', date: '2026-10-05', count: '1', category: 'Transporte', account: 'Efectivo' }],
});

const noop = () => {};
const dialog = (p: ImportPreview | null, extra: { busy?: boolean; errors?: string[] } = {}) =>
  renderToStaticMarkup(<ImportConfirmDialog preview={p} kinds={{}} onKindsChange={noop} busy={extra.busy ?? false} errors={extra.errors ?? []} onCancel={noop} onConfirm={noop} />);

describe('confirmación de importación en modal', () => {
  it('se muestra dentro de un <dialog> con título, vista previa y acción clara', () => {
    const html = dialog(preview(2));
    const titleId = /^<dialog aria-labelledby="([^"]+)">/.exec(html)?.[1];
    expect(titleId).toBeTruthy();
    expect(html).toContain(`<h2 id="${titleId}">Confirmar importación</h2>`);
    expect(html.endsWith('</dialog>')).toBe(true);
    const t = text(html);
    expect(t).toContain('Vista previa (respaldo del prototipo)');
    expect(t).toContain('2 registros: 2 nuevos, 0 ya importados, 0 en conflicto.');
    expect(t).toContain('Al confirmar se crean 2 registros nuevos');
    expect(t).toContain('05/10/2026');
    expect(html).toMatch(/<button type="button" class="primary" data-autofocus="true">Confirmar importación<\/button>/);
    expect(html).toContain('>Cancelar</button>');
    expect(html).toContain('Mastercard (ARS)');
  });

  it('sin registros nuevos la confirmación queda deshabilitada y el foco va a Cancelar', () => {
    const html = dialog(preview(0));
    expect(html).toMatch(/<button type="button" class="primary" disabled="">Confirmar importación<\/button>/);
    expect(html).toMatch(/<button type="button" data-autofocus="true">Cancelar<\/button>/);
    expect(text(html)).toContain('No hay registros nuevos para importar');
  });

  it('mientras importa no se puede cerrar ni confirmar dos veces; los errores se ven dentro del modal', () => {
    const html = dialog(preview(2), { busy: true, errors: ['El lote de importación venció.'] });
    expect(html).toMatch(/aria-label="Cerrar" disabled=""/);
    expect(html).toMatch(/class="primary" disabled=""[^>]*>Importando…/);
    expect(html).toMatch(/<div class="notice danger" role="alert"><div>El lote de importación venció\.<\/div><\/div>/);
  });

  it('cerrado: el <dialog> no tiene contenido', () => {
    expect(dialog(null)).toMatch(/^<dialog aria-labelledby="[^"]+"><\/dialog>$/);
  });
});
