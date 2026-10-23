import { describe, expect, it } from 'vitest';
import { pickInitialFocus } from './Modal';

/**
 * Raíz mínima con la semántica de querySelector: devuelve el PRIMER elemento en orden del documento que cumple
 * cualquiera de los selectores de la lista. Entiende solo lo que usa el modal: `tag`, `[attr]`, `:not([attr])`
 * y `:not([attr=valor])`. Las pruebas corren sin DOM (environment node).
 */
interface FakeEl { tag: string; attrs: Record<string, string>; name: string }

function matches(el: FakeEl, selector: string): boolean {
  const tag = /^[a-z]+/.exec(selector)?.[0];
  if (tag && el.tag !== tag) return false;
  const rest = selector.slice(tag?.length ?? 0);
  for (const [, neg, attr, value] of rest.matchAll(/(:not\()?\[([a-z-]+)(?:=([^\]]+))?\]\)?/g)) {
    const has = value === undefined ? attr! in el.attrs : el.attrs[attr!] === value;
    if (neg ? has : !has) return false;
  }
  return true;
}

function root(elements: FakeEl[]) {
  return {
    querySelector: (list: string) => (elements.find((el) => list.split(',').some((s) => matches(el, s.trim()))) ?? null) as never,
  };
}

const el = (tag: string, name: string, attrs: Record<string, string> = {}): FakeEl => ({ tag, name, attrs });
const picked = (elements: FakeEl[]) => (pickInitialFocus(root(elements)) as unknown as FakeEl | null)?.name ?? null;

describe('foco inicial del modal', () => {
  // Como la confirmación de importación con cuentas a crear: los selects de tipo de cuenta están antes de los botones.
  const importDialog = (autofocus: 'cancel' | 'confirm') => [
    el('button', 'cerrar'),
    el('select', 'tipo de Mastercard'),
    el('select', 'tipo de Visa'),
    el('button', 'Cancelar', autofocus === 'cancel' ? { 'data-autofocus': 'true' } : {}),
    el('button', 'Confirmar importación', autofocus === 'confirm' ? { 'data-autofocus': 'true' } : { disabled: '' }),
  ];

  it('data-autofocus tiene prioridad aunque haya un select antes en el documento', () => {
    expect(picked(importDialog('cancel'))).toBe('Cancelar');
    expect(picked(importDialog('confirm'))).toBe('Confirmar importación');
    // La lista única de antes elegía el primer select:
    expect((root(importDialog('cancel')).querySelector('[data-autofocus], select') as unknown as FakeEl).name).toBe('tipo de Mastercard');
  });

  it('sin control marcado: el primer campo habilitado (no ocultos ni deshabilitados)', () => {
    expect(picked([el('button', 'cerrar'), el('input', 'oculto', { type: 'hidden' }), el('select', 'off', { disabled: '' }), el('input', 'concepto'), el('select', 'moneda')])).toBe('concepto');
    expect(picked([el('textarea', 'off', { disabled: '' }), el('textarea', 'nota')])).toBe('nota');
  });

  it('un control marcado pero deshabilitado no recibe el foco', () => {
    expect(picked([el('select', 'tipo'), el('button', 'Confirmar', { 'data-autofocus': 'true', disabled: '' })])).toBe('tipo');
    expect(picked([el('button', 'cerrar')])).toBeNull();
  });
});
