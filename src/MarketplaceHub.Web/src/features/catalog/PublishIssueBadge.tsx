import { useLayoutEffect, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { placePublishPopover } from './publish-popover-placement'

export type PublishIssueRow = { key: string; title: string; detail: string; href?: string }
export type PublishIssueAction = { key: string; label: string; onRun: () => void }

// A publish card's gaps badge: red "Yayın için zorunlu eksik" when something blocks the publish, yellow "6 uyarı" when
// only optional attributes would be left out. Hovering it previews the gaps, pressing it (or Enter/Space) pins them in a
// popover, required ones first, then the optional ones, with the fixes (Kategori Eşle, Değerleri Eşle…), so the card
// never grows a row or a box per fix. With one gap and one fix the press opens that fix directly. The popover keeps to
// the room above the sticky Yayınla bar.
export function PublishIssueBadge({ id, label, heading, rows, requiredCount, optionalHeading, optionalRows = [], optionalCount, actions, moreText }: {
  id: string
  label: string
  heading: string
  rows: PublishIssueRow[]
  // The counters on the badge: red for what blocks, yellow for what is optional.
  requiredCount?: number
  optionalHeading?: string
  optionalRows?: PublishIssueRow[]
  optionalCount?: number
  actions: PublishIssueAction[]
  moreText?: string
}) {
  const [open, setOpen] = useState(false)
  const wrapper = useRef<HTMLSpanElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrapper], () => setOpen(false), { triggerRef: trigger })
  const shortcut = rows.length === 1 && !optionalRows.length && actions.length === 1 ? actions[0] : null
  const tone = rows.length ? 'danger' : 'warning'

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

  const list = (items: PublishIssueRow[]) => <ul>{items.map(row => <li key={row.key}><b>{row.href ? <a href={row.href} target="_blank" rel="noreferrer">{row.title}</a> : row.title}</b> {row.detail}</li>)}</ul>
  return <span ref={wrapper} className={`publish-issue-badge is-${tone}`}>
    <button ref={trigger} type="button" className={`publish-platform-status status-${tone} publish-issue-trigger`} aria-haspopup={shortcut ? 'dialog' : 'true'} aria-expanded={shortcut ? undefined : open} aria-controls={id} title={shortcut ? `${label} · ${shortcut.label}` : label} onMouseEnter={place} onFocus={place} onClick={() => { if (shortcut) shortcut.onRun(); else setOpen(value => !value) }}>
      <i aria-hidden="true" /><span className="publish-issue-label">{label}</span>
      {requiredCount ? <span className="publish-issue-count" aria-label={`${requiredCount} zorunlu eksik`}>{requiredCount}</span> : null}
      {optionalCount ? <span className="publish-issue-count is-optional" aria-label={`${optionalCount} isteğe bağlı uyarı`}>{optionalCount}</span> : null}
      <UiIcon name="chevronDown" size={12} />
    </button>
    <div ref={panel} id={id} className={`publish-issue-tooltip is-${tone}${open ? ' is-open' : ''}`} role={open ? 'dialog' : 'tooltip'} aria-label={rows.length ? heading : optionalHeading}>
      {rows.length > 0 && <section className="publish-issue-required"><strong>{heading}</strong>{list(rows)}{moreText && <p className="publish-issue-more">{moreText}</p>}</section>}
      {optionalRows.length > 0 && <section className="publish-issue-optional"><strong>{optionalHeading}</strong>{list(optionalRows)}</section>}
      {actions.length > 0 && <div className="publish-issue-actions">{actions.map(action => <button key={action.key} type="button" onClick={() => { setOpen(false); action.onRun() }}>{action.label}</button>)}</div>}
    </div>
  </span>
}
