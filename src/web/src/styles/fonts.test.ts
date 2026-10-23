import { describe, expect, it } from 'vitest';
import reference from './reference.css?raw';
import extensions from './extensions.css?raw';

/** docs/01: Manrope en títulos/números y DM Sans en cuerpo, manteniendo pesos y fallback sans-serif. */
describe('tipografías con fallback sans-serif', () => {
  const declarations = (css: string) =>
    [...css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([.#\w\s:>+~[\]="'-]+)\{([^}]*)\}/g)].flatMap(([, selector, body]) =>
      body!.split(';').filter((d) => /^\s*font(-family)?\s*:/.test(d) && /Manrope|DM Sans/.test(d)).map((d) => `${selector!.trim()} → ${d.trim()}`));

  it.each([['reference.css', reference], ['extensions.css', extensions]])('%s: toda regla con Manrope/DM Sans termina en sans-serif', (_, css) => {
    const found = declarations(css);
    expect(found.length).toBeGreaterThan(0);
    for (const d of found) expect(d).toMatch(/,\s*sans-serif\s*$/);
  });

  it('incluye los títulos de panel y los números de KPI (antes sin fallback)', () => {
    const found = declarations(reference).join('\n');
    expect(found).toContain('.stat strong → font:600 clamp(20px,2vw,30px) Manrope,sans-serif');
    expect(found).toContain('.panel h2 → font:600 17px Manrope,sans-serif');
  });
});
