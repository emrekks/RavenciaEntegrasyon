import { trNumberInput } from '../../../shared/tr-number'
// Desi Hesapla: the desi from the parcel's measures (En × Boy × Yükseklik / 3000).
import { RrModal } from '../../../shared/ui/rr-components'
import type { EditorContext } from './editor-context'

export function DesiCalculatorDialog({ editor }: { editor: EditorContext }) {
  const { setCalculateDesi, desiCalculatorOpen, setDesiCalculatorOpen, form, updateField, desi } = editor
  if (!desiCalculatorOpen) return null
  return <RrModal title="Desi Hesapla" hint="En × Boy × Yükseklik / 3000 formülü kullanılır." size="lg" modalClass="desi-calculator-modal" onClose={() => setDesiCalculatorOpen(false)}
    foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" className="rr-btn rr-primary" disabled={!desi} onClick={() => { updateField('desi', trNumberInput(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></>}>
    <div className="desi-calculator-grid">
      <label className="rr-fld"><small>Ağırlık (kg)</small><input className="rr-in" value={form.weight} onChange={event => updateField('weight', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label>
      <label className="rr-fld"><small>En (cm)</small><input className="rr-in" value={form.width} onChange={event => updateField('width', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label>
      <label className="rr-fld"><small>Boy (cm)</small><input className="rr-in" value={form.length} onChange={event => updateField('length', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label>
      <label className="rr-fld"><small>Yükseklik (cm)</small><input className="rr-in" value={form.height} onChange={event => updateField('height', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label>
    </div>
    <div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div>
  </RrModal>
}
