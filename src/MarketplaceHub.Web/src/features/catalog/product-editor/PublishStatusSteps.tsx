import type { CSSProperties, ReactNode } from 'react'
import { publishStepBadges, publishStepNames, publishStepTitle, type PublishStepState, type PublishSteps } from './publish-status-steps'

const stroke = { fill: 'none', stroke: 'currentColor', strokeLinecap: 'round', strokeLinejoin: 'round' } as const
const icons: Record<'check' | 'x' | 'spin' | 'box' | 'send' | 'clock' | 'store', ReactNode> = {
  check: <path d="M5 12.5l4.5 4.5L19 7.5" strokeWidth={3} />,
  x: <path d="M7 7l10 10M17 7L7 17" strokeWidth={3} />,
  spin: <path d="M12 3a9 9 0 1 0 9 9" strokeWidth={3} />,
  box: <g strokeWidth={2.2}><path d="M3 7.5 12 3l9 4.5v9L12 21l-9-4.5v-9Z" /><path d="M3 7.5 12 12l9-4.5M12 12v9" /></g>,
  send: <path d="M4 12 20 4l-6 16-3-7-7-1Z" strokeWidth={2.4} />,
  clock: <g strokeWidth={2.4}><circle cx="12" cy="12" r="8.5" /><path d="M12 7.5V12l3 2" /></g>,
  store: <g strokeWidth={2.2}><path d="M4 9h16l-1-5H5L4 9Z" /><path d="M5 9v11h14V9M10 20v-6h4v6" /></g>
}
const stepIcons = ['box', 'send', 'clock', 'store'] as const

function iconFor(state: PublishStepState, index: number) {
  return state === 'done' ? 'check' : state === 'fail' ? 'x' : state === 'now' ? 'spin' : stepIcons[index]
}

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
          <svg viewBox="0 0 24 24" width="12" height="12" {...stroke}>{icons[iconFor(state, index)]}</svg>
          {corner.filter(badge => badge.step === index).map(badge => <b key={badge.tone} className={`publish-step-badge is-${badge.tone} is-${badge.corner}`}>{badge.text}</b>)}
        </span>
      </span>)}
    </span>
    {chips.length ? <span className="publish-steps-label publish-steps-chips" aria-hidden="true">{chips.map(badge => <b key={badge.tone} className={`publish-steps-chip is-${badge.tone}`}>{badge.chip}</b>)}</span>
      : <span className={`publish-steps-label publish-steps-word is-${steps.steps.includes('fail') ? 'fail' : steps.steps.includes('now') ? 'now' : steps.steps.every(state => state === 'idle') ? 'idle' : 'ok'}`} aria-hidden="true">{steps.label}</span>}
  </span>
}
