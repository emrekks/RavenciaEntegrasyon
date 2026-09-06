import type { ReactNode } from 'react';

type MetricAccent = 'primary' | 'success' | 'warning' | 'danger' | 'info' | 'neutral';

interface MetricCardProps {
  icon?: ReactNode;
  label: string;
  value: string | number;
  accent?: MetricAccent;
  onClick?: () => void;
  className?: string;
}

export function MetricCard({ icon, label, value, accent = 'neutral', onClick, className = '' }: MetricCardProps) {
  const Tag = onClick ? 'button' : 'div';
  return (
    <Tag
      className={`rv-metric rv-metric--${accent} ${onClick ? 'rv-metric--clickable' : ''} ${className}`}
      onClick={onClick}
    >
      {icon && <span className="rv-metric__icon">{icon}</span>}
      <div className="rv-metric__body">
        <span className="rv-metric__label">{label}</span>
        <strong className="rv-metric__value">{value}</strong>
      </div>
    </Tag>
  );
}
