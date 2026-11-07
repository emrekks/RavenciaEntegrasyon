import type { ReactNode } from 'react'
import { UiIcon, type UiIconName } from './UiIcon'

// The one empty state (the look of "Sipariş Bulunamadı"): an icon, a Title Case title, one muted line and, when
// filters are active, "Filtreleri Temizle". No box around it.
export function EmptyState({ title = 'Kayıt Bulunamadı', children, icon = 'box', onClearFilters, className }: { title?: string; children?: ReactNode; icon?: UiIconName; onClearFilters?: () => void; className?: string }) {
  return <div className={['rv-empty-state', className].filter(Boolean).join(' ')}>
    <span className="rv-empty-state-icon" aria-hidden="true"><UiIcon name={icon} /></span>
    <b>{title}</b>
    {children ? <span className="rv-empty-state-lead">{children}</span> : null}
    {onClearFilters ? <button type="button" className="rv-empty-state-clear" onClick={onClearFilters}>Filtreleri Temizle</button> : null}
  </div>
}
