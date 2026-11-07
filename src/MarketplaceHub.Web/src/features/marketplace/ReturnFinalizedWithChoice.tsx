import { PanelSelect } from '../../shared/PanelSelect'
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
    <PanelSelect value={value} disabled={disabled} ariaLabel={subject ? `${subject} için Para İadesi / Değişim` : 'Para İadesi / Değişim'}
      onChange={onChange}
      options={[{ value: 'Refund', label: finalizedWithLabels.Refund }, { value: 'Change', label: finalizedWithLabels.Change }]} />
    <em>{customerRequestNote(customerRequest)}</em>
  </label>
}
