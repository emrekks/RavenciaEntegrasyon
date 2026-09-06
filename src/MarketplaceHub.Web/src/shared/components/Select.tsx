import { forwardRef, type SelectHTMLAttributes, type ReactNode } from 'react';

interface SelectOption {
  value: string;
  label: string;
}

interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  options: SelectOption[];
  label?: string;
  placeholder?: string;
  icon?: ReactNode;
}

export const Select = forwardRef<HTMLSelectElement, SelectProps>(
  ({ options, label, placeholder, icon, className = '', id, ...props }, ref) => {
    const selectId = id || (label ? `select-${label.toLowerCase().replace(/\s+/g, '-')}` : undefined);
    return (
      <div className={`rv-select-wrap ${className}`}>
        {label && <label className="rv-select-label" htmlFor={selectId}>{label}</label>}
        <div className="rv-select-field">
          {icon && <span className="rv-select-icon">{icon}</span>}
          <select ref={ref} id={selectId} className={`rv-select ${icon ? 'rv-select--has-icon' : ''}`} {...props}>
            {placeholder && <option value="">{placeholder}</option>}
            {options.map(opt => (
              <option key={opt.value} value={opt.value}>{opt.label}</option>
            ))}
          </select>
          <span className="rv-select-chevron" aria-hidden>▾</span>
        </div>
      </div>
    );
  }
);

Select.displayName = 'Select';
