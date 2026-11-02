import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { placePublishPopover } from './publish-popover-placement'
import { publishIssueChips, publishIssueChipText, publishIssueTipLines, type PublishIssueRow } from './publish-issue-chips'

export type { PublishIssueRow }

/** A button at the foot of the popover (the card's "Durumu Yenile", "İş Ayrıntısı"): its own handler and busy state. */
export type PublishIssueAction = { key: string; label: string; icon: ReactNode; onRun: () => void; disabled?: boolean; busy?: boolean; title?: string; ariaLabel?: string; tone?: 'danger' }

// A window (a dialog) in front that is not the one this badge sits in: the one a chip opened.
function windowInFront(wrapper: Element | null) {
  return [...document.querySelectorAll('[aria-modal="true"]')].some(dialog => !wrapper || !dialog.contains(wrapper))
}

// A publish card's gaps badge: red "Yayın için zorunlu eksik" when something blocks the publish, yellow "6 uyarı" when
// only optional attributes would be left out. Hovering it previews the gaps, pressing it (or Enter/Space) pins them in a
// popover: the required gaps as red chips, then the optional ones as yellow chips; each chip opens its own fix, so the
// card never grows a row or a box per fix. A chip shows its name only (no hover tooltip); what exactly is missing (the
// values without a marketplace value, a field with no mapping or an outdated one) is in its accessible name. With one gap that has a fix the
// press opens that fix directly. The popover keeps to the room above the sticky Yayınla bar.
// A window a chip opens leaves the popover open: its presses and its Escape are not outside presses, focus comes back to
// the chip when it closes, and the list follows the publish check (a fixed gap leaves it; none left says so).
// `variant="steps"`: a publish card's whole status is the trigger (`trigger`, named by `label`): hover or focus shows
// the popover, a press pins it (never a shortcut to one fix). The popover then opens with `lead` (the state and its
// reason in words) and ends with `actions`; `emptyText` (null for none) says no gap is left.
export function PublishIssueBadge({ id, label, heading, rows, requiredCount, optionalHeading, optionalRows = [], optionalCount, moreText, variant = 'pill', onOpenChange, trigger: triggerContent, lead, actions = [], emptyText = 'Eksik kalmadı.' }: {
  variant?: 'pill' | 'steps'
  trigger?: ReactNode
  lead?: ReactNode
  actions?: PublishIssueAction[]
  emptyText?: string | null
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
  // A fix or an action that opens a window renders once more, so the effect below starts watching for its close.
  const [, setReturnWatch] = useState(0)
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
  const steps = variant === 'steps'
  const shortcut = !steps && chips.required.length === 1 && !chips.optional.length && chips.required[0].onRun ? chips.required[0].onRun : null
  const tone = rows.length ? 'danger' : empty ? 'success' : 'warning'

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
      const chips = [...(panel.current?.querySelectorAll<HTMLElement>('.publish-issue-chip.is-fixable, .publish-issue-action') ?? [])]
      const chip = chips.find(item => item.dataset.chipKey === pending.key) ?? chips.find(item => item.matches('.publish-issue-chip'))
      ;(chip ?? trigger.current)?.focus()
    }, 120)
    return () => window.clearInterval(timer)
  })

  const run = (row: PublishIssueRow) => {
    returnTo.current = { key: row.key, sawWindow: false }
    setReturnWatch(value => value + 1)
    row.onRun?.()
  }
  const list = (items: PublishIssueRow[]) => <ul className="publish-issue-chips">{items.map(row => {
    const text = publishIssueChipText(row)
    const described = [row.title, ...publishIssueTipLines(row)].join('. ')
    const tipProps = { 'aria-label': described, 'data-chip-key': row.key }
    return <li key={row.key}>
      {row.onRun ? <button type="button" className="publish-issue-chip is-fixable" {...tipProps} onClick={() => run(row)}>{text}</button>
        : row.href ? <a className="publish-issue-chip is-fixable" href={row.href} target="_blank" rel="noreferrer" {...tipProps} onClick={() => setOpen(false)}>{text}</a>
          : <span className="publish-issue-chip" role="note" {...tipProps}>{text}</span>}
    </li>
  })}</ul>
  const runAction = (action: PublishIssueAction) => {
    returnTo.current = { key: `action:${action.key}`, sawWindow: false }
    setReturnWatch(value => value + 1)
    action.onRun()
  }
  return <span ref={wrapper} className={`publish-issue-badge is-${tone}${steps ? ' is-steps' : ''}`}>
    <button ref={trigger} type="button" className={steps ? 'publish-issue-trigger publish-steps-trigger' : `publish-platform-status status-${tone} publish-issue-trigger`} aria-label={steps ? label : undefined} aria-haspopup={shortcut ? 'dialog' : 'true'} aria-expanded={shortcut ? undefined : open} aria-controls={id} onMouseEnter={place} onFocus={place} onClick={() => { if (shortcut) { returnTo.current = null; shortcut() } else setOpen(value => !value) }}>
      {steps ? triggerContent : <>
        <i aria-hidden="true" /><span className="publish-issue-label">{label}</span>
        {requiredCount ? <span className="publish-issue-count" aria-label={`${requiredCount} zorunlu eksik`}>{requiredCount}</span> : null}
        {optionalCount ? <span className="publish-issue-count is-optional" aria-label={`${optionalCount} isteğe bağlı uyarı`}>{optionalCount}</span> : null}
        <UiIcon name="chevronDown" size={12} />
      </>}
    </button>
    <div ref={panel} id={id} className={`publish-issue-tooltip is-${tone}${open ? ' is-open' : ''}`} role={open ? 'dialog' : 'tooltip'} aria-label={steps ? label : rows.length ? heading : empty ? 'Eksik kalmadı' : optionalHeading}>
      {lead && <div className="publish-issue-lead">{lead}</div>}
      {chips.required.length > 0 && <section className="publish-issue-required"><strong>{heading}</strong>{list(chips.required)}{moreText && <p className="publish-issue-more">{moreText}</p>}</section>}
      {chips.optional.length > 0 && <section className="publish-issue-optional"><strong>{optionalHeading}</strong>{list(chips.optional)}</section>}
      {empty && emptyText && <p className="publish-issue-empty" role="status"><UiIcon name="check" size={14} />{emptyText}</p>}
      {actions.length > 0 && <div className="publish-issue-actions">{actions.map(action => <button key={action.key} type="button" className={`publish-issue-action${action.busy ? ' is-busy' : ''}${action.tone ? ` is-${action.tone}` : ''}`} data-chip-key={`action:${action.key}`} title={action.title} aria-label={action.ariaLabel} aria-busy={action.busy || undefined} disabled={action.disabled} onClick={() => runAction(action)}>{action.icon}{action.label}</button>)}</div>}
    </div>
  </span>
}
