import type { ReactNode } from 'react';

interface PageHeaderProps {
  eyebrow?: string;
  title: string;
  actions?: ReactNode;
  className?: string;
}

export function PageHeader({ eyebrow, title, actions, className = '' }: PageHeaderProps) {
  return (
    <header className={`rv-page-header ${className}`}>
      <div className="rv-page-header__text">
        {eyebrow && <span className="rv-page-header__eyebrow">{eyebrow}</span>}
        <h1 className="rv-page-header__title">{title}</h1>
      </div>
      {actions && <div className="rv-page-header__actions">{actions}</div>}
    </header>
  );
}
