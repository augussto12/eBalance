import { describe, expect, it, vi } from 'vitest';
import { confirmDemoLoad } from './Shell';

describe('«Cargar ejemplo» (solo desarrollo) no mezcla datos en silencio', () => {
  it('sin registros propios agrega los ejemplos sin preguntar', async () => {
    const confirm = vi.fn(async () => true);
    expect(await confirmDemoLoad(async () => false, confirm)).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it.each([true, false])('con registros pide confirmación en el modal (respuesta: %s)', async (answer) => {
    const confirm = vi.fn(async () => answer);
    expect(await confirmDemoLoad(async () => true, confirm)).toBe(answer);
    expect(confirm).toHaveBeenCalledOnce();
    expect(confirm.mock.calls[0]).toEqual([expect.objectContaining({ title: 'Agregar datos de ejemplo', confirmLabel: 'Agregar ejemplos' })]);
  });

  it('si no se puede saber si hay registros, también pregunta', async () => {
    const confirm = vi.fn(async () => false);
    expect(await confirmDemoLoad(async () => { throw new Error('sin conexión'); }, confirm)).toBe(false);
    expect(confirm).toHaveBeenCalledOnce();
  });
});
