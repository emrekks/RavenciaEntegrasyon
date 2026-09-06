import type { ReactNode } from 'react';

type TabVariant = 'pill' | 'underline';

interface TabItem {
  key: string;
  label: string;
  count?: number;
  countTone?: 'danger' | 'warning' | 'primary' | 'neutral';
  icon?: ReactNode;
}

interface TabsProps {
  items: TabItem[];
  activeKey: string;
  onChange: (key: string) => void;
  variant?: TabVariant;
  className?: string;
}

export function Tabs({ items, activeKey, onChange, variant = 'pill', className = '' }: TabsProps) {
  return (
    <nav className={`rv-tabs rv-tabs--${variant} ${className}`} role="tablist">
      {items.map((item) => (
        <button
          key={item.key}
          role="tab"
          aria-selected={item.key === activeKey}
          className={`rv-tab ${item.key === activeKey ? 'rv-tab--active' : ''}`}
          onClick={() => onChange(item.key)}
        >
          {item.icon && <span className="rv-tab__icon">{item.icon}</span>}
          <span className="rv-tab__label">{item.label}</span>
          {item.count != null && (
            <span className={`rv-tab__count ${item.countTone ? `rv-tab__count--${item.countTone}` : ''}`}>
              {item.count}
            </span>
          )}
        </button>
      ))}
    </nav>
  );
}
