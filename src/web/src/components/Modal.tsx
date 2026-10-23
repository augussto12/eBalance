import { useEffect, useId, useRef, type ReactNode } from 'react';
import type { FieldErrors } from '../lib/forms';

/**
 * Modal accesible sobre <dialog> nativo (mismo estilo que el prototipo): showModal() vuelve inerte el resto de la
 * página (foco atrapado), Escape cierra, y al cerrar el foco vuelve al elemento que lo abrió.
 */
export function Modal({ open, title, onClose, children, busy = false }: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  busy?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const opener = useRef<Element | null>(null);
  const titleId = useId();

  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    if (open && !d.open) {
      opener.current = document.activeElement;
      d.showModal();
      const first = d.querySelector<HTMLElement>('[data-autofocus], input:not([type=hidden]):not([disabled]), select, textarea');
      first?.focus();
    } else if (!open && d.open) {
      d.close();
      (opener.current as HTMLElement | null)?.focus?.();
    }
  }, [open]);

  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    const onCancel = (e: Event) => {
      e.preventDefault();
      if (!busy) onClose();
    };
    d.addEventListener('cancel', onCancel);
    return () => d.removeEventListener('cancel', onCancel);
  }, [onClose, busy]);

  return (
    <dialog ref={ref} aria-labelledby={titleId}>
      {open && (
        <>
          <div className="modalhead">
            <h2 id={titleId}>{title}</h2>
            <button type="button" aria-label="Cerrar" onClick={onClose} disabled={busy}>✕</button>
          </div>
          {children}
        </>
      )}
    </dialog>
  );
}

export function FieldError({ id, errors }: { id: string; errors?: string[] }) {
  if (!errors?.length) return null;
  return <span className="fielderror" id={id} role="alert">{errors.join(' ')}</span>;
}

/**
 * Errores asociados al campo: `props(k)` va en el control (aria-invalid + aria-describedby) y `error(k)` junto a él.
 * Ambos usan el mismo id (único en la página por useId), así aria-describedby nunca apunta a un elemento inexistente.
 */
export function useFieldErrors<K extends string>(errors: FieldErrors<K>) {
  const base = useId();
  const id = (k: K) => `${base}err-${k}`;
  return {
    props: (k: K): { 'aria-invalid'?: true; 'aria-describedby'?: string } =>
      errors[k]?.length ? { 'aria-invalid': true, 'aria-describedby': id(k) } : {},
    error: (k: K) => <FieldError id={id(k)} errors={errors[k]} />,
  };
}
