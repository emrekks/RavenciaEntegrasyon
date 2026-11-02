import { useEffect, useId, useLayoutEffect, useRef, useState, type FocusEvent, type MouseEvent } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { placePublishPopover } from './publish-popover-placement'
import { publishIssueChips, publishIssueChipText, publishIssueTipLines, type PublishIssueRow } from './publish-issue-chips'

export type { PublishIssueRow }

type ChipTip = { lines: string[]; left: number; top: number; above: boolean }

// A window (a dialog) in front that is not the one this badge sits in: the one a chip opened.
function windowInFront(wrapper: Element | null) {
  return [...document.querySelectorAll('[aria-modal="true"]')].some(dialog => !wrapper || !dialog.contains(wrapper))
}

// A publish card's gaps badge: red "Yayın için zorunlu eksik" when something blocks the publish, yellow "6 uyarı" when
// only optional attributes would be left out. Hovering it previews the gaps, pressing it (or Enter/Space) pins them in a
// popover: the required gaps as red chips, then the optional ones as yellow chips; each chip opens its own fix, so the
// card never grows a row or a box per fix. A chip's hover or focus shows a small tooltip with what exactly is missing
// (the values without a marketplace value, a field with no mapping or an outdated one). With one gap that has a fix the
// press opens that fix directly. The popover keeps to the room above the sticky Yayınla bar.
// A window a chip opens leaves the popover open: its presses and its Escape are not outside presses, focus comes back to
// the chip when it closes, and the list follows the publish check (a fixed gap leaves it; none left says so).
// `variant="note"`: the same gaps behind a small line of text under a status (a partly live card's "Kalan 21 varyantta 2
// eksik") instead of a status pill. `onOpenChange` lets the card keep the badge while its popover is open.
export function PublishIssueBadge({ id, label, heading, rows, requiredCount, optionalHeading, optionalRows = [], optionalCount, moreText, variant = 'pill', onOpenChange }: {
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
  onOpenChange?: (open: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const wrapper = useRef<HTMLSpanElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  const returnTo = useRef<{ key: string; sawWindow: boolean } | null>(null)
  const latestOpenChange = useRef(onOpenChange)
  useEffect(() => { latestOpenChange.current = onOpenChange })
  useEffect(() => { latestOpenChange.current?.(open) }, [open])
  useDismissable(open, [wrapper], () => setOpen(false), { triggerRef: trigger, stayOpen: () => windowInFront(wrapper.current) })
  const chips = publishIssueChips(rows, optionalRows)
  const empty = !chips.required.length && !chips.optional.length
  const shortcut = chips.required.length === 1 && !chips.optional.length && chips.required[0].onRun ? chips.required[0].onRun : null
  const tone = rows.length ? 'danger' : empty ? 'success' : 'warning'
  const [tip, setTip] = useState<ChipTip | null>(null)
  const tipId = useId()

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
    if (!returnTo.current) panel.current?.querySelector<HTMLElement>('.publish-issue-chip.is-fixable')?.focus()
  }, [open])

  // Back from the window a chip opened: focus returns to that chip (or, when its gap is gone, to the popover's first
  // chip or the badge itself).
  useEffect(() => {
    if (!open || !returnTo.current) return
    const timer = window.setInterval(() => {
      const pending = returnTo.current
      if (!pending) return
      if (windowInFront(wrapper.current)) { pending.sawWindow = true; return }
      if (!pending.sawWindow) return
      returnTo.current = null
      window.clearInterval(timer)
      const chips = [...(panel.current?.querySelectorAll<HTMLElement>('.publish-issue-chip.is-fixable') ?? [])]
      const chip = chips.find(item => item.dataset.chipKey === pending.key) ?? chips[0]
      ;(chip ?? trigger.current)?.focus()
    }, 120)
    return () => window.clearInterval(timer)
  })

  const showTip = (row: PublishIssueRow) => (event: MouseEvent<HTMLElement> | FocusEvent<HTMLElement>) => {
    const lines = publishIssueTipLines(row)
    if (!lines.length) return
    const box = event.currentTarget.getBoundingClientRect()
    const width = Math.min(300, window.innerWidth - 16)
    const above = window.innerHeight - box.bottom < 96 && box.top > 96
    setTip({ lines, left: Math.max(8, Math.min(box.left, window.innerWidth - width - 8)), top: above ? box.top - 6 : box.bottom + 6, above })
  }
  const hideTip = () => setTip(null)
  const run = (row: PublishIssueRow) => {
    hideTip()
    returnTo.current = { key: row.key, sawWindow: false }
    row.onRun?.()
  }
  const list = (items: PublishIssueRow[]) => <ul className="publish-issue-chips">{items.map(row => {
    const text = publishIssueChipText(row)
    const described = [row.title, ...publishIssueTipLines(row)].join('. ')
    const tipProps = { onMouseEnter: showTip(row), onMouseLeave: hideTip, onFocus: showTip(row), onBlur: hideTip, 'aria-label': described, 'aria-describedby': tip ? tipId : undefined, 'data-chip-key': row.key }
    return <li key={row.key}>
      {row.onRun ? <button type="button" className="publish-issue-chip is-fixable" {...tipProps} onClick={() => run(row)}>{text}</button>
        : row.href ? <a className="publish-issue-chip is-fixable" href={row.href} target="_blank" rel="noreferrer" {...tipProps} onClick={() => setOpen(false)}>{text}</a>
          : <span className="publish-issue-chip" role="note" {...tipProps}>{text}</span>}
    </li>
  })}</ul>
  const note = variant === 'note'
  const tipHost = typeof document === 'undefined' ? null : document.querySelector('.app-shell') ?? document.body
  return <span ref={wrapper} className={`publish-issue-badge is-${tone}${note ? ' publish-partial-note is-blocked' : ''}`}>
    <button ref={trigger} type="button" className={note ? 'publish-issue-trigger publish-issue-note-trigger' : `publish-platform-status status-${tone} publish-issue-trigger`} aria-haspopup={shortcut ? 'dialog' : 'true'} aria-expanded={shortcut ? undefined : open} aria-controls={id} onMouseEnter={place} onFocus={place} onClick={() => { if (shortcut) { returnTo.current = null; shortcut() } else setOpen(value => !value) }}>
      {note ? <UiIcon name="alert" size={12} /> : <i aria-hidden="true" />}<span className="publish-issue-label">{label}</span>
      {!note && requiredCount ? <span className="publish-issue-count" aria-label={`${requiredCount} zorunlu eksik`}>{requiredCount}</span> : null}
      {!note && optionalCount ? <span className="publish-issue-count is-optional" aria-label={`${optionalCount} isteğe bağlı uyarı`}>{optionalCount}</span> : null}
      <UiIcon name="chevronDown" size={12} />
    </button>
    <div ref={panel} id={id} className={`publish-issue-tooltip is-${tone}${open ? ' is-open' : ''}`} role={open ? 'dialog' : 'tooltip'} aria-label={rows.length ? heading : empty ? 'Eksik kalmadı' : optionalHeading}>
      {chips.required.length > 0 && <section className="publish-issue-required"><strong>{heading}</strong>{list(chips.required)}{moreText && <p className="publish-issue-more">{moreText}</p>}</section>}
      {chips.optional.length > 0 && <section className="publish-issue-optional"><strong>{optionalHeading}</strong>{list(chips.optional)}</section>}
      {empty && <p className="publish-issue-empty" role="status"><UiIcon name="check" size={14} />Eksik kalmadı.</p>}
    </div>
    {tip && tipHost && createPortal(<div id={tipId} role="tooltip" className={`publish-issue-chip-tip${tip.above ? ' is-above' : ''}`} style={{ left: tip.left, top: tip.top }}>{tip.lines.map(line => <span key={line}>{line}</span>)}</div>, tipHost)}
  </span>
}
