// A model's colour cards of one send (job-model-group.ts): one row with the cards' summed-up state and the newest job's
// attempts and time; Tümünü Aç lists the cards, each its own job row (renderCard: the list's own row) that opens its
// detail as before. Loaded on its own the first time a list holds a model row, so the operations chunk stays its size.
import type { ReactNode } from 'react'
import { UiIcon } from '../../shared/components'
import { JobStateGlyph } from './job-state-glyph'
import { jobModelCardColour, jobModelGroupLine, jobModelGroupState, type JobModelGroup, type JobModelGroupCard } from './job-model-group'

type Props = {
  group: JobModelGroup
  open: boolean
  onToggle: (key: string) => void
  title: string
  attempts: string
  time: { time: string; day: string }
  correlationId: string
  renderCard: (job: JobModelGroupCard['job'], colour: string, variantCount: number | null | undefined) => ReactNode
}

export default function JobModelGroupRows({ group, open, onToggle, title, attempts, time, correlationId, renderCard }: Props) {
  const state = jobModelGroupState(group.cards.map(card => card.job))
  const line = jobModelGroupLine(group)
  const toggleLabel = open ? 'Tümünü Kapat' : 'Tümünü Aç'
  return <>
    <tr className={`jobs-reference-row jobs-model-group-row${open ? ' is-open' : ''}`} onClick={() => onToggle(group.key)}>
      <td><div className="jobs-reference-type"><button type="button" className="jobs-reference-type-icon jobs-model-group-toggle" aria-expanded={open} aria-label={`${toggleLabel}: ${title}, ${group.cards.length} iş`} title={toggleLabel} onClick={event => { event.stopPropagation(); onToggle(group.key) }}><UiIcon name="chevronDown" size={16} /></button><span className="jobs-reference-type-content"><strong>{title}</strong><small title={line}>{line}</small></span></div></td>
      <td><div className="jobs-model-group-state"><span className={`jobs-reference-status ${state.tone}`}><JobStateGlyph tone={state.tone} />{state.label}</span>{state.counts && <small title={state.counts}>{state.counts}</small>}</div></td>
      <td className="jobs-reference-attempt">{attempts}</td>
      <td><div className="jobs-reference-time"><strong>{time.time}</strong><small>{time.day} · {group.cards.length} iş</small></div></td>
      <td><span className="jobs-reference-correlation" title={correlationId}>{correlationId}</span></td>
    </tr>
    {open && group.cards.map(card => renderCard(card.job, jobModelCardColour(card), card.variantCount))}
  </>
}
