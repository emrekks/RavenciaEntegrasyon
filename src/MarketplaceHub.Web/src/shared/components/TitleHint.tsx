import type { ReactNode } from 'react'
import { HelpMark } from './FieldHelp'
import './title-hint.css'

// A section's or page's explanation behind a round "?" beside its title, instead of a line of text under it. It opens
// while the pointer is on the mark or it has keyboard focus, and on a tap (a tap elsewhere, Esc or a scroll closes it).
// Place it inside the heading, after the title text: <h2>Görseller<TitleHint label="Görseller">…</TitleHint></h2>.
export function TitleHint({ label, id, children }: { label: string; id?: string; children: ReactNode }) {
  return <HelpMark id={id} idPrefix="title-hint" ariaLabel={`${label} hakkında`} className="title-hint" tipClassName="title-hint-tip" iconSize={16} tipWidth={320} edge={24} prefer="below">{children}</HelpMark>
}
