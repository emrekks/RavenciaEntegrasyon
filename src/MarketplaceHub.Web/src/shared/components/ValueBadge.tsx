import type { ReactNode } from 'react'
import { statusLabel, statusTone } from '../status-labels'

/**
 * A status code shown as a small badge with its Turkish label. The tone follows the code unless given; `statusClass`
 * adds `status-<code>` for screens that style single states.
 */
export function ValueBadge({ value, tone, label, title, icon, statusClass = false }: { value: string; tone?: string; label?: ReactNode; title?: string; icon?: ReactNode; statusClass?: boolean }) {
  const normalized = value.trim().toUpperCase()
  const className = ['badge', tone ?? statusTone(value), statusClass ? `status-${normalized.toLowerCase()}` : ''].filter(Boolean).join(' ')
  return <span className={className} title={title}>{icon ?? <i aria-hidden="true" />}{label ?? statusLabel(value)}</span>
}
