import { customerRequestNote, finalizedWithLabels, type FinalizedWith } from './return-finalized-with'

type Props = {
  value: FinalizedWith | ''
  customerRequest: string | null | undefined
  disabled?: boolean
  /** Names the claim in the control's accessible name when several are shown (the bulk dialog). */
  subject?: string
  onChange: (value: FinalizedWith | '') => void
}

/** "Para İadesi / Değişim" for one Hepsiburada claim, preselected from what the customer asked for. */
export function ReturnFinalizedWithChoice({ value, customerRequest, disabled, subject, onChange }: Props) {
  return <label className="rr-fld rr-finalized">
    <small>Para İadesi / Değişim</small>
    <select className="rr-in" value={value} disabled={disabled} required aria-invalid={value === '' ? true : undefined}
      aria-label={subject ? `${subject} için Para İadesi / Değişim` : undefined}
      onChange={event => onChange(event.target.value as FinalizedWith | '')}>
      <option value="">Seçin</option>
      <option value="Refund">{finalizedWithLabels.Refund}</option>
      <option value="Change">{finalizedWithLabels.Change}</option>
    </select>
    <em>{customerRequestNote(customerRequest)}</em>
  </label>
}
