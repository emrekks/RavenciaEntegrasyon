import { useState } from 'react'
import { DialogSurface, UiIcon } from '../../../shared/components'

export type VariantOptionEditField = {
  name: string
  value: string
  // The panel values this option takes (its category attribute); null when the option has no attribute, then the
  // value is typed.
  values: Array<{ id: string; value: string }> | null
}
export type VariantOptionEditChange = { name: string; from: string; to: string; valueId: string | null }

// "Varyant Düzenleme": the option values of one variant row. The row's option text is never typed into directly;
// values come from the category attribute's list, and the switch applies a change to every row holding the same value.
export function VariantOptionEditDialog({ label, fields, sameValueCount, onClose, onSave }: {
  label: string
  fields: VariantOptionEditField[]
  sameValueCount: (name: string, value: string) => number
  onClose: () => void
  onSave: (changes: VariantOptionEditChange[], applyToAll: boolean) => void
}) {
  const [draft, setDraft] = useState(() => Object.fromEntries(fields.map(field => [field.name, field.value])))
  const [applyToAll, setApplyToAll] = useState(true)
  const changes = fields
    .filter(field => (draft[field.name] ?? '').trim() && draft[field.name].trim() !== field.value.trim())
    .map(field => ({ name: field.name, from: field.value, to: draft[field.name].trim(), valueId: field.values?.find(value => value.value === draft[field.name])?.id ?? null }))
  const affected = changes.reduce((total, change) => Math.max(total, sameValueCount(change.name, change.from)), 0)

  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}>
    <DialogSurface as="section" onClose={onClose} className="workspace-modal variant-option-edit-modal" aria-labelledby="variant-option-edit-title" onMouseDown={event => event.stopPropagation()}>
      <header><div><p className="eyebrow">Varyant Düzenleme</p><h2 id="variant-option-edit-title">{label}</h2><p>Değerler kategori özelliğinin listesinden seçilir. Değişiklik ürünü kaydettiğinizde kalıcı olur.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header>
      {/* No <form>: the dialog sits inside the editor form, and a nested form would submit the page. */}
      <div className="variant-option-edit-body">
        <div className="variant-option-edit-grid">
          {fields.map(field => <div className="variant-option-edit-pair" key={field.name}>
            <label>Seçenek Adı<input value={field.name} readOnly disabled /></label>
            <label>Seçenek Değeri{field.values
              ? <select value={draft[field.name] ?? ''} onChange={event => setDraft(current => ({ ...current, [field.name]: event.target.value }))}>
                {!field.values.some(value => value.value === field.value) && <option value={field.value}>{field.value}</option>}
                {field.values.map(value => <option key={value.id} value={value.value}>{value.value}</option>)}
              </select>
              : <input value={draft[field.name] ?? ''} onChange={event => setDraft(current => ({ ...current, [field.name]: event.target.value }))} />}</label>
          </div>)}
        </div>
        <label className="variant-option-edit-switch marketplace-pricing-switch">
          <span><strong>Tüm Aynı Varyantlarda Düzelt</strong><small>{applyToAll ? (affected > 1 ? `Aynı değeri taşıyan ${affected} varyantın tümü değişir.` : 'Aynı değeri taşıyan tüm varyantlar değişir.') : 'Yalnız bu varyant değişir.'}</small></span>
          <input type="checkbox" role="switch" checked={applyToAll} onChange={event => setApplyToAll(event.target.checked)} aria-label="Tüm aynı varyantlarda düzelt" />
        </label>
        <footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" disabled={!changes.length} onClick={() => onSave(changes, applyToAll)}>Uygula</button></footer>
      </div>
    </DialogSurface>
  </div>
}
