import { forwardRef, type InputHTMLAttributes, type ReactNode } from 'react';

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  icon?: ReactNode;
  error?: string;
  label?: string;
}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ icon, error, label, className = '', id, ...props }, ref) => {
    const inputId = id || (label ? `input-${label.toLowerCase().replace(/\s+/g, '-')}` : undefined);
    return (
      <div className={`rv-input-wrap ${error ? 'rv-input-wrap--error' : ''} ${className}`}>
        {label && <label className="rv-input-label" htmlFor={inputId}>{label}</label>}
        <div className="rv-input-field">
          {icon && <span className="rv-input-icon">{icon}</span>}
          <input ref={ref} id={inputId} className={`rv-input ${icon ? 'rv-input--has-icon' : ''}`} {...props} />
        </div>
        {error && <p className="rv-input-error">{error}</p>}
      </div>
    );
  }
);

Input.displayName = 'Input';
