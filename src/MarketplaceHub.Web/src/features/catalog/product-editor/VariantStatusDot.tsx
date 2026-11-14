import { HelpMark } from '../../../shared/components/FieldHelp'
import { variantStatusLabel, type VariantStatus } from './variant-status'

// The variant row's status light: hover, keyboard focus or a tap opens the list of what is wrong (or known) for it.
export function VariantStatusDot({ status, rowLabel }: { status: VariantStatus; rowLabel: string }) {
  const label = variantStatusLabel(status.tone)
  return <HelpMark idPrefix="variant-status" ariaLabel={`${rowLabel}: ${label}`} className={`variant-status-dot is-${status.tone}`} tipClassName="field-help-tip variant-status-tip" iconSize={10} tipWidth={320} edge={12} prefer="above" mark={<span className="variant-status-dot-light" aria-hidden="true" />}>
    <strong>{label}</strong>
    <ul>{status.lines.map(line => <li key={line.text} className={`is-${line.tone}`}>{line.text}</li>)}</ul>
  </HelpMark>
}
