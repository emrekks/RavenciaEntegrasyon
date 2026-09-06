type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

interface StatusDotProps {
  tone: StatusTone;
  label?: string;
  pulse?: boolean;
  className?: string;
}

export function StatusDot({ tone, label, pulse = false, className = '' }: StatusDotProps) {
  return (
    <span className={`rv-status-dot rv-status-dot--${tone} ${pulse ? 'rv-status-dot--pulse' : ''} ${className}`}>
      <i aria-hidden />
      {label && <span>{label}</span>}
    </span>
  );
}
