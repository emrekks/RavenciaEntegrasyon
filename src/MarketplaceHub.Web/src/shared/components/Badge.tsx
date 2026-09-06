import type { ReactNode } from 'react';

type BadgeTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'primary';

interface BadgeProps {
  tone?: BadgeTone;
  dot?: boolean;
  children: ReactNode;
  className?: string;
}

export function Badge({ tone = 'neutral', dot = false, children, className = '' }: BadgeProps) {
  return (
    <span className={`rv-badge rv-badge--${tone} ${className}`}>
      {dot && <i className="rv-badge__dot" aria-hidden />}
      {children}
    </span>
  );
}
