import type { CSSProperties } from 'react'
import { ToneIcon } from './ToneIcon'
import { UiIcon } from './UiIcon'
import './notice-toast.css'

export type NoticeToastKind = 'success' | 'error' | 'info' | 'pending'

const defaultTitles: Record<NoticeToastKind, string> = {
  success: 'İşlem Başarılı',
  error: 'İşlem Başarısız',
  info: 'Bilgi',
  pending: 'İşlem Sürüyor'
}

// The single bottom-right notification of the panel. Tone shows only in the icon tile and the countdown bar,
// the card itself always follows the active theme.
export function NoticeToast({ kind, message, title, onClose, durationMs, className }: {
  kind: NoticeToastKind
  message: string
  title?: string
  onClose?: () => void
  durationMs?: number
  className?: string
}) {
  const failed = kind === 'error'
  return <div
    className={['app-toast', `app-toast-${kind}`, className].filter(Boolean).join(' ')}
    role={failed ? 'alert' : 'status'}
    aria-live={failed ? 'assertive' : 'polite'}
    aria-busy={kind === 'pending' || undefined}
    style={durationMs ? { '--app-toast-duration': `${durationMs}ms` } as CSSProperties : undefined}
  >
    <span className="app-toast-icon" aria-hidden="true">
      {kind === 'pending'
        ? <svg className="app-toast-spinner" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2.25" strokeLinecap="round"><circle cx="12" cy="12" r="8.5" opacity=".25" /><path d="M12 3.5a8.5 8.5 0 0 1 8.5 8.5" /></svg>
        : <ToneIcon kind={kind} />}
    </span>
    <div className="app-toast-content">
      <strong>{title ?? defaultTitles[kind]}</strong>
      <p>{message}</p>
    </div>
    {onClose && <button type="button" className="app-toast-close" onClick={onClose} aria-label="Bildirimi kapat"><UiIcon name="close" size={16} /></button>}
    {(durationMs || kind === 'pending') && <span className="app-toast-track" aria-hidden="true"><span className="app-toast-progress" /></span>}
  </div>
}
