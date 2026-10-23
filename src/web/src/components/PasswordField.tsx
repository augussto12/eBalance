import { useId, type ReactNode } from 'react';
import { passwordTooLong } from '../lib/forms';
import { FieldError } from './Modal';

/**
 * Campo de contraseña sin maxLength (docs/03: sin recortes silenciosos). Permite pegar y gestores de contraseñas.
 * Si el valor supera el máximo, el error aparece junto al campo en el momento y quien lo usa no envía el formulario.
 */
export function PasswordField({ label, value, onChange, autoComplete, errors, required = true }: {
  label: ReactNode;
  value: string;
  onChange: (value: string) => void;
  autoComplete: 'current-password' | 'new-password';
  /** Errores del campo (cliente o servidor). */
  errors?: string[];
  required?: boolean;
}) {
  const errorId = `${useId()}err-password`;
  const tooLong = passwordTooLong(value);
  const all = [...(tooLong ? [tooLong] : []), ...(errors ?? []).filter((e) => e !== tooLong)];
  return (
    <label>
      {label}
      <input
        type="password"
        autoComplete={autoComplete}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        required={required}
        aria-invalid={all.length ? true : undefined}
        aria-describedby={all.length ? errorId : undefined}
      />
      <FieldError id={errorId} errors={all} />
    </label>
  );
}
