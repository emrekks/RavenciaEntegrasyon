import type { CSSProperties, ReactNode } from 'react'
import { publishStepBadges, publishStepNames, publishStepTitle, type PublishSteps } from './publish-status-steps'

const stroke = { fill: 'none', stroke: 'currentColor', strokeLinecap: 'round', strokeLinejoin: 'round' } as const
// Every step keeps its own icon whatever its state; the state is its colour and its motion (publish-platform-issues.css).
// The clock has its hands apart so the waiting Onay step can turn them.
const icons: Record<'box' | 'send' | 'clock' | 'store', ReactNode> = {
  box: <g strokeWidth={2.2}><path d="M3 7.5 12 3l9 4.5v9L12 21l-9-4.5v-9Z" /><path d="M3 7.5 12 12l9-4.5M12 12v9" /></g>,
  send: <path d="M4 12 20 4l-6 16-3-7-7-1Z" strokeWidth={2.4} />,
  clock: <g strokeWidth={2.2}><circle cx="12" cy="12" r="9.5" strokeWidth={2} /><path className="publish-clock-hand publish-clock-hour" d="M12 12V7.5" strokeWidth={2.6} /><path className="publish-clock-hand publish-clock-minute" d="M12 12V4.6" /><circle cx="12" cy="12" r="1.1" fill="currentColor" stroke="none" /></g>,
  store: <g strokeWidth={2.2}><path d="M4 9h16l-1-5H5L4 9Z" /><path d="M5 9v11h14V9M10 20v-6h4v6" /></g>
}
const stepIcons = ['box', 'send', 'clock', 'store'] as const

// The compact pill of a publish card's status: four icon steps joined by short links, the green live count as a corner
// badge on Yayında, then the state label; the red and yellow counts take the label's place as two small chips ("2 Eksik",
// "6 Uyarı"), which give way on a narrow cell like the label.
export function PublishStatusSteps({ steps, fit, unit = 'varyant' }: { steps: PublishSteps; fit: number; unit?: 'varyant' | 'renk' }) {
  const badges = publishStepBadges(steps, unit)
  const corner = badges.filter(badge => badge.tone === 'green')
  const chips = badges.filter(badge => badge.tone !== 'green')
  return <span className="publish-steps" data-fit={fit} style={{ '--publish-step-share': steps.share ?? 50 } as CSSProperties}>
    <span className="publish-steps-track" aria-hidden="true">
      {steps.steps.map((state, index) => <span key={publishStepNames[index]} className="publish-steps-item">
        {index > 0 && <span className={`publish-step-link is-${steps.links[index - 1]}`} />}
        <span className={`publish-step is-${state}`} title={[publishStepTitle(index, state, steps), ...badges.filter(badge => badge.step === index).map(badge => badge.title)].join(' · ')}>
          <svg viewBox="0 0 24 24" width="12" height="12" {...stroke}>{icons[stepIcons[index]]}</svg>
          {corner.filter(badge => badge.step === index).map(badge => <b key={badge.tone} className={`publish-step-badge is-${badge.tone} is-${badge.corner}`}>{badge.text}</b>)}
        </span>
      </span>)}
    </span>
    {chips.length ? <span className="publish-steps-label publish-steps-chips" aria-hidden="true">{chips.map(badge => <b key={badge.tone} className={`publish-steps-chip is-${badge.tone}`}>{badge.text}<span className="publish-steps-chip-word">{badge.chip.slice(badge.text.length)}</span></b>)}</span>
      : <span className={`publish-steps-label publish-steps-word is-${steps.steps.includes('fail') ? 'fail' : steps.steps.includes('now') ? 'now' : steps.steps.every(state => state === 'idle') ? 'idle' : 'ok'}`} aria-hidden="true">{steps.label}</span>}
  </span>
}
