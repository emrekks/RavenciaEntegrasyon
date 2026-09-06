import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

interface DrawerProps {
  open: boolean;
  onClose: () => void;
  title?: string;
  width?: number;
  children: ReactNode;
  footer?: ReactNode;
  className?: string;
}

export function Drawer({ open, onClose, title, width = 480, children, footer, className = '' }: DrawerProps) {
  const backdropRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  return createPortal(
    <div
      ref={backdropRef}
      className={`rv-drawer-backdrop ${open ? 'rv-drawer-backdrop--open' : ''}`}
      onClick={(e) => { if (e.target === backdropRef.current) onClose(); }}
    >
      <aside
        className={`rv-drawer ${open ? 'rv-drawer--open' : ''} ${className}`}
        style={{ width }}
        role="dialog"
        aria-modal="true"
      >
        {title && (
          <header className="rv-drawer__header">
            <h2 className="rv-drawer__title">{title}</h2>
            <button className="rv-drawer__close" onClick={onClose} aria-label="Kapat">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <path d="M18 6L6 18M6 6l12 12" />
              </svg>
            </button>
          </header>
        )}
        <div className="rv-drawer__body">{children}</div>
        {footer && <footer className="rv-drawer__footer">{footer}</footer>}
      </aside>
    </div>,
    document.body
  );
}
