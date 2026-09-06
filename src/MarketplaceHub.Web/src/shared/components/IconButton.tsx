import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';

type IconButtonVariant = 'ghost' | 'outline' | 'danger';
type IconButtonSize = 'sm' | 'md';

interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: IconButtonVariant;
  size?: IconButtonSize;
  label: string;
  children: ReactNode;
}

export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(
  ({ variant = 'ghost', size = 'md', label, children, className = '', ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={`rv-icon-btn rv-icon-btn--${variant} rv-icon-btn--${size} ${className}`}
        aria-label={label}
        title={label}
        {...props}
      >
        {children}
      </button>
    );
  }
);

IconButton.displayName = 'IconButton';
