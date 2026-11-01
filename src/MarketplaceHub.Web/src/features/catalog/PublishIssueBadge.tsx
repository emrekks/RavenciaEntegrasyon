import { useLayoutEffect, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { placePublishPopover } from './publish-popover-placement'
import { publishIssueChips, type PublishIssueRow } from './publish-issue-chips'

export type { PublishIssueRow }

// A publish card's gaps badge: red "Yayın için zorunlu eksik" when something blocks the publish, yellow "6 uyarı" when
// only optional attributes would be left out. Hovering it previews the gaps, pressing it (or Enter/Space) pins them in a
// popover: the required gaps as red chips, then the optional ones as yellow chips, each only its title (the detail is the
// chip's hover text and accessible name); each chip opens its own fix, so the card never grows a row or a box per fix.
// With one gap that has a fix the press opens that fix directly. The popover keeps to the room above the sticky Yayınla bar.
// `variant="note"`: the same gaps behind a small line of text under a status (a partly live card's "Kalan 21 varyantta 2
// eksik") instead of a status pill.
export function PublishIssueBadge({ id, label, heading, rows, requiredCount, optionalHeading, optionalRows = [], optionalCount, moreText, variant = 'pill' }: {
  variant?: 'pill' | 'note'
  id: string
  label: string
  heading: string
  rows: PublishIssueRow[]
  // The counters on the badge: red for what blocks, yellow for what is optional.
  requiredCount?: number
  optionalHeading?: string
  optionalRows?: PublishIssueRow[]
  optionalCount?: number
  moreText?: string
}) {
  const [open, setOpen] = useState(false)
  const wrapper = useRef<HTMLSpanElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrapper], () => setOpen(false), { triggerRef: trigger })
  const chips = publishIssueChips(rows, optionalRows)
  const shortcut = chips.required.length === 1 && !chips.optional.length && chips.required[0].onRun ? chips.required[0].onRun : null
  const tone = rows.length ? 'danger' : 'warning'
  const fixable = [...chips.required, ...chips.optional].some(row => row.onRun || row.href)

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
    panel.current?.querySelector<HTMLElement>('.publish-issue-chip.is-fixable')?.focus()
  }, [open])

  const run = (row: PublishIssueRow) => { setOpen(false); row.onRun?.() }
  const list = (items: PublishIssueRow[]) => <ul className="publish-issue-chips">{items.map(row => {
    const described = row.detail ? `${row.title}: ${row.detail}` : row.title
    return <li key={row.key}>
      {row.onRun ? <button type="button" className="publish-issue-chip is-fixable" title={row.detail || undefined} aria-label={described} onClick={() => run(row)}>{row.title}</button>
        : row.href ? <a className="publish-issue-chip is-fixable" href={row.href} target="_blank" rel="noreferrer" title={row.detail || undefined} aria-label={described} onClick={() => setOpen(false)}>{row.title}</a>
          : <span className="publish-issue-chip" title={row.detail || undefined} aria-label={described} role="note">{row.title}</span>}
    </li>
  })}</ul>
  const note = variant === 'note'
  return <span ref={wrapper} className={`publish-issue-badge is-${tone}${note ? ' publish-partial-note is-blocked' : ''}`}>
    <button ref={trigger} type="button" className={note ? 'publish-issue-trigger publish-issue-note-trigger' : `publish-platform-status status-${tone} publish-issue-trigger`} aria-haspopup={shortcut ? 'dialog' : 'true'} aria-expanded={shortcut ? undefined : open} aria-controls={id} title={label} onMouseEnter={place} onFocus={place} onClick={() => { if (shortcut) shortcut(); else setOpen(value => !value) }}>
      {note ? <UiIcon name="alert" size={12} /> : <i aria-hidden="true" />}<span className="publish-issue-label">{label}</span>
      {!note && requiredCount ? <span className="publish-issue-count" aria-label={`${requiredCount} zorunlu eksik`}>{requiredCount}</span> : null}
      {!note && optionalCount ? <span className="publish-issue-count is-optional" aria-label={`${optionalCount} isteğe bağlı uyarı`}>{optionalCount}</span> : null}
      <UiIcon name="chevronDown" size={12} />
    </button>
    <div ref={panel} id={id} className={`publish-issue-tooltip is-${tone}${open ? ' is-open' : ''}`} role={open ? 'dialog' : 'tooltip'} aria-label={rows.length ? heading : optionalHeading}>
      {chips.required.length > 0 && <section className="publish-issue-required"><strong>{heading}</strong>{list(chips.required)}{moreText && <p className="publish-issue-more">{moreText}</p>}</section>}
      {chips.optional.length > 0 && <section className="publish-issue-optional"><strong>{optionalHeading}</strong>{list(chips.optional)}</section>}
      {fixable && <p className="publish-issue-hint">Düzeltmek için bir eksiğe tıklayın.</p>}
    </div>
  </span>
}
