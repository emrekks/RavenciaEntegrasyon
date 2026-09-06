import { useEffect, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

type ToastTone = 'success' | 'danger' | 'warning' | 'info';

interface ToastProps {
  open: boolean;
  onClose: () => void;
  tone?: ToastTone;
  duration?: number;
  children: ReactNode;
}

export function Toast({ open, onClose, tone = 'info', duration = 4000, children }: ToastProps) {
  useEffect(() => {
    if (!open || !duration) return;
    const id = setTimeout(onClose, duration);
    return () => clearTimeout(id);
  }, [open, onClose, duration]);

  if (!open) return null;

  return createPortal(
    <div className={`rv-toast rv-toast--${tone}`} role="alert">
      <span className="rv-toast__content">{children}</span>
      <button className="rv-toast__close" onClick={onClose} aria-label="Kapat">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M18 6L6 18M6 6l12 12" />
        </svg>
      </button>
    </div>,
    document.body
  );
}
