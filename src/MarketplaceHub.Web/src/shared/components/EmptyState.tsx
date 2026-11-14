import type { ReactNode } from 'react'
import { UiIcon, type UiIconName } from './UiIcon'

// The one empty state of the whole panel (lists, tables, charts, cards): an icon tile, a Title Case title, one muted
// line and an optional action. Pages pass only the icon, title, text and action; size, spacing, colours and type live
// in design-step2.css (.rv-empty-state). onClearFilters is the "Temizle" action.
export function EmptyState({ title = 'Kayıt Bulunamadı', children, icon = 'box', action, onClearFilters, className }: { title?: string; children?: ReactNode; icon?: UiIconName; action?: ReactNode; onClearFilters?: () => void; className?: string }) {
  return <div className={['rv-empty-state', className].filter(Boolean).join(' ')}>
    <span className="rv-empty-state-icon" aria-hidden="true"><UiIcon name={icon} /></span>
    <b>{title}</b>
    {children ? <span className="rv-empty-state-lead">{children}</span> : null}
    {onClearFilters ? <button type="button" className="rv-empty-state-clear" onClick={onClearFilters}>Temizle</button> : null}
    {action}
  </div>
}

// A list's empty state: with a filter, search or tab filter active it is "<Noun> Bulunamadı" + Temizle; with none
// active it is "Henüz <Noun> Yok" with a hint how records come in, and no Temizle (nothing to clear).
export function ListEmptyState({ noun, hasActiveFilters, onClear, filteredHint, emptyHint, icon }: { noun: string; hasActiveFilters: boolean; onClear?: () => void; filteredHint?: ReactNode; emptyHint: ReactNode; icon?: UiIconName }) {
  return hasActiveFilters
    ? <EmptyState title={`${noun} Bulunamadı`} icon={icon} onClearFilters={onClear}>{filteredHint ?? 'Filtrelerle eşleşen kayıt yok.'}</EmptyState>
    : <EmptyState title={`Henüz ${noun} Yok`} icon={icon}>{emptyHint}</EmptyState>
}
