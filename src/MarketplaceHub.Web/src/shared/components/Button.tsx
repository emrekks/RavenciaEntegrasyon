import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';

type ButtonVariant = 'primary' | 'ghost' | 'danger' | 'outline';
type ButtonSize = 'sm' | 'md' | 'lg';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: ReactNode;
  loading?: boolean;
  children: ReactNode;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ variant = 'primary', size = 'md', icon, loading, disabled, children, className = '', ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={`rv-btn rv-btn--${variant} rv-btn--${size} ${loading ? 'rv-btn--loading' : ''} ${className}`}
        disabled={disabled || loading}
        {...props}
      >
        {loading && <span className="rv-btn__spinner" aria-hidden />}
        {!loading && icon && <span className="rv-btn__icon">{icon}</span>}
        <span className="rv-btn__label">{children}</span>
      </button>
    );
  }
);

Button.displayName = 'Button';
