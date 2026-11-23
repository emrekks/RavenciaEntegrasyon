import { useState } from 'react'
import { trNumberInput } from '../../../shared/tr-number'
// Desi Hesapla: the desi from the parcel's measures (En × Boy × Yükseklik / 3000).
import { RrModal } from '../../../shared/ui/rr-components'
import type { EditorContext } from './editor-context'
import { calculatorDesi, MEASURE_FIELDS, unreadableMeasureMessage, type Measures } from './editor-number'

export function DesiCalculatorDialog({ editor }: { editor: EditorContext }) {
  if (!editor.desiCalculatorOpen) return null
  return <DesiCalculatorWindow editor={editor} />
}

function DesiCalculatorWindow({ editor }: { editor: EditorContext }) {
  const { setCalculateDesi, setDesiCalculatorOpen, form, updateField } = editor
  const [measures, setMeasures] = useState<Measures>(() => ({ weight: form.weight, width: form.width, length: form.length, height: form.height }))
  const { desi, unreadable } = calculatorDesi(measures)
  const apply = () => {
    for (const field of MEASURE_FIELDS) updateField(field.key, measures[field.key])
    updateField('desi', trNumberInput(Number(desi.toFixed(2))))
    setCalculateDesi(true)
    setDesiCalculatorOpen(false)
  }
  return <RrModal title="Desi Hesapla" hint="En × Boy × Yükseklik / 3000 formülü kullanılır." size="lg" modalClass="desi-calculator-modal" onClose={() => setDesiCalculatorOpen(false)}
    foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" className="rr-btn rr-primary" disabled={!desi} onClick={apply}>Uygula</button></>}>
    <div className="desi-calculator-grid">
      {MEASURE_FIELDS.map(field => <label className="rr-fld" key={field.key}><small>{field.label}</small><input className="rr-in" value={measures[field.key]} aria-invalid={unreadable.includes(field.key) || undefined} onChange={event => { const value = event.target.value; setMeasures(current => ({ ...current, [field.key]: value })) }} type="text" inputMode="decimal" autoComplete="off" /></label>)}
    </div>
    {unreadable.length > 0 && <p className="rr-callout rr-danger" role="alert">{unreadableMeasureMessage}</p>}
    <div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div>
  </RrModal>
}
