import { useLayoutEffect, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { placePublishPopover } from './publish-popover-placement'

export type PublishIssueRow = { key: string; title: string; detail: string; href?: string }
export type PublishIssueAction = { key: string; label: string; onRun: () => void }

// A publish card's red "Yayın için zorunlu eksik" badge: hovering it previews the gaps, pressing it (or Enter/Space)
// pins them in a popover with the fixes (Kategori Eşle, Değerleri Eşle…), so the card never grows a row per fix. With
// one gap and one fix the press opens that fix directly. The popover keeps to the room above the sticky Yayınla bar.
export function PublishIssueBadge({ id, label, count, heading, rows, actions, moreText }: {
  id: string
  label: string
  // Fixes waiting (value mappings to complete…), shown as a small counter on the badge.
  count?: number
  heading: string
  rows: PublishIssueRow[]
  actions: PublishIssueAction[]
  moreText?: string
}) {
  const [open, setOpen] = useState(false)
  const wrapper = useRef<HTMLSpanElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrapper], () => setOpen(false), { triggerRef: trigger })
  const shortcut = rows.length === 1 && actions.length === 1 ? actions[0] : null

  // Measured while shown, then placed below, above, or cut to the room (it scrolls).
  const place = () => {
    const box = panel.current
    if (!box || !trigger.current) return
    const shown = box.style.display
    box.style.display = 'block'
    const { placement, maxHeight } = placePublishPopover(trigger.current, box.scrollHeight)
    box.style.display = shown
    box.dataset.placement = placement
    if (maxHeight) box.style.maxHeight = `${maxHeight}px`
    else box.style.removeProperty('max-height')
  }
  useLayoutEffect(() => {
    if (!open) return
    place()
    panel.current?.querySelector<HTMLElement>('button, a[href]')?.focus()
  }, [open])

  return <span ref={wrapper} className="publish-issue-badge">
    <button ref={trigger} type="button" className="publish-platform-status status-danger publish-issue-trigger" aria-haspopup={shortcut ? 'dialog' : 'true'} aria-expanded={shortcut ? undefined : open} aria-controls={id} title={shortcut ? `${label} · ${shortcut.label}` : label} onMouseEnter={place} onFocus={place} onClick={() => { if (shortcut) shortcut.onRun(); else setOpen(value => !value) }}>
      <i aria-hidden="true" /><span className="publish-issue-label">{label}</span>{count ? <span className="publish-issue-count" aria-label={`${count} düzeltme`}>{count}</span> : null}<UiIcon name="chevronDown" size={12} />
    </button>
    <div ref={panel} id={id} className={`publish-issue-tooltip${open ? ' is-open' : ''}`} role={open ? 'dialog' : 'tooltip'} aria-label={heading}>
      <strong>{heading}</strong>
      <ul>{rows.map(row => <li key={row.key}><b>{row.href ? <a href={row.href} target="_blank" rel="noreferrer">{row.title}</a> : row.title}</b> {row.detail}</li>)}</ul>
      {moreText && <p className="publish-issue-more">{moreText}</p>}
      {actions.length > 0 && <div className="publish-issue-actions">{actions.map(action => <button key={action.key} type="button" onClick={() => { setOpen(false); action.onRun() }}>{action.label}</button>)}</div>}
    </div>
  </span>
}
