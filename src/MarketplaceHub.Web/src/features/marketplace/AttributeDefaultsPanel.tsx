import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { filterValueCandidates } from '../catalog/value-mapping-suggestions'
import './attribute-defaults-panel.css'

type RemoteAttribute = { externalId: string; name: string; isActive: boolean; isRequired?: boolean | null; allowsCustomValue?: boolean | null }
type AttributeDefault = { externalAttributeId: string; attributeName: string; externalValueId: string | null; valueName: string | null; customValue: string | null; version: number }
type ValueList = { items: Array<{ externalId: string; name: string; isActive: boolean }> }

export function defaultValueLabel(item: AttributeDefault) {
  return item.customValue ?? item.valueName ?? item.externalValueId ?? ''
}

// Connection-wide fixed values (e.g. Menşei = TR, Yaş Grubu = Yetişkin) sent whenever a product
// has no value for that attribute. Shown in the category attribute workspace so the value can be
// picked from this category's marketplace list.
export function AttributeDefaultsPanel({ connectionId, categoryScope, platformLabel, remoteAttributes, onNotice }: {
  connectionId: string
  categoryScope: string
  platformLabel: string
  remoteAttributes: RemoteAttribute[]
  onNotice: (value: string) => void
}) {
  const client = useQueryClient()
  const defaultsKey = ['attribute-defaults', connectionId] as const
  const defaults = useQuery({ queryKey: defaultsKey, queryFn: () => hubApi<AttributeDefault[]>(`/connections/${connectionId}/attribute-defaults`), retry: false })
  const [attributeId, setAttributeId] = useState('')
  const [search, setSearch] = useState('')
  const [valueId, setValueId] = useState('')
  const [customValue, setCustomValue] = useState('')
  const [busy, setBusy] = useState(false)
  const existing = defaults.data ?? []
  const defaultedIds = new Set(existing.map(item => item.externalAttributeId))
  const candidates = remoteAttributes
    .filter(item => item.isActive && !defaultedIds.has(item.externalId))
    .sort((left, right) => Number(right.isRequired === true) - Number(left.isRequired === true) || left.name.localeCompare(right.name, 'tr-TR'))
  const selected = candidates.find(item => item.externalId === attributeId)
  const freeText = selected?.allowsCustomValue === true
  const values = useQuery({
    queryKey: ['reference-attribute-values', connectionId, `${categoryScope}/${attributeId}`],
    queryFn: () => hubApi<ValueList>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(attributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`),
    enabled: Boolean(selected) && !freeText,
    retry: false
  })
  const activeValues = (values.data?.items ?? []).filter(item => item.isActive)
  const shownValues = search.trim() ? filterValueCandidates(search, activeValues) : activeValues.slice(0, 24)
  const chosenValue = activeValues.find(item => item.externalId === valueId)

  function reset() { setAttributeId(''); setSearch(''); setValueId(''); setCustomValue('') }

  async function save() {
    if (!selected) return
    setBusy(true)
    try {
      await hubApi(`/connections/${connectionId}/attribute-defaults/${encodeURIComponent(selected.externalId)}`, {
        method: 'PUT',
        body: JSON.stringify(freeText
          ? { attributeName: selected.name, customValue: customValue.trim() }
          : { attributeName: selected.name, externalValueId: valueId, valueName: chosenValue?.name ?? null })
      })
      await client.invalidateQueries({ queryKey: defaultsKey })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      await client.invalidateQueries({ queryKey: ['category-mappings', connectionId] })
      onNotice(`“${selected.name}” için varsayılan kaydedildi. Değeri boş kalan ürünlerde ${platformLabel}’a bu değer gönderilecek.`)
      reset()
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Varsayılan kaydedilemedi.')
    } finally { setBusy(false) }
  }

  async function remove(item: AttributeDefault) {
    setBusy(true)
    try {
      await hubApi(`/connections/${connectionId}/attribute-defaults/${encodeURIComponent(item.externalAttributeId)}`, { method: 'DELETE' })
      await client.invalidateQueries({ queryKey: defaultsKey })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      await client.invalidateQueries({ queryKey: ['category-mappings', connectionId] })
      onNotice(`“${item.attributeName}” varsayılanı kaldırıldı.`)
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Varsayılan kaldırılamadı.')
    } finally { setBusy(false) }
  }

  const canSave = Boolean(selected) && (freeText ? customValue.trim().length > 0 : Boolean(valueId))
  return <details className="attribute-defaults-panel">
    <summary>
      <span>Platform varsayılanları{existing.length ? ` (${existing.length})` : ''}</span>
      <small>Ürünlerde boş kalan alanlara {platformLabel}’a otomatik gönderilir; ürüne girilen değer her zaman önce gelir.</small>
    </summary>
    <div className="attribute-defaults-body">
      {existing.length > 0 && <ul className="attribute-defaults-list">
        {existing.map(item => <li key={item.externalAttributeId}>
          <span>{item.attributeName}</span>
          <strong>{defaultValueLabel(item)}</strong>
          <button type="button" disabled={busy} onClick={() => void remove(item)}>Kaldır</button>
        </li>)}
      </ul>}
      <div className="attribute-defaults-add">
        <select value={attributeId} aria-label="Varsayılan verilecek özellik" onChange={event => { setAttributeId(event.target.value); setSearch(''); setValueId(''); setCustomValue('') }}>
          <option value="">Özellik seçin…</option>
          {candidates.map(item => <option key={item.externalId} value={item.externalId}>{item.name}{item.isRequired ? ' (zorunlu)' : ''}</option>)}
        </select>
        {selected && freeText && <input value={customValue} maxLength={320} placeholder="Gönderilecek değer" aria-label={`${selected.name} için varsayılan değer`} onChange={event => setCustomValue(event.target.value)} />}
        {selected && !freeText && <>
          <input type="search" value={search} placeholder={`${platformLabel} değerlerinde ara`} aria-label={`${selected.name} değerlerinde ara`} onChange={event => setSearch(event.target.value)} />
          {values.isLoading ? <p className="attribute-defaults-state">Değer listesi yükleniyor…</p>
            : values.isError ? <p className="attribute-defaults-state">Bu özelliğin değer listesi henüz alınmamış; önce değerleri eşitleyin.</p>
              : <div className="attribute-defaults-values" role="listbox" aria-label={`${selected.name} değerleri`}>
                {shownValues.map(item => <button type="button" role="option" aria-selected={valueId === item.externalId} key={item.externalId} className={valueId === item.externalId ? 'is-chosen' : undefined} onClick={() => setValueId(item.externalId)}>{item.name}</button>)}
                {!shownValues.length && <p className="attribute-defaults-state">Eşleşen değer yok.</p>}
              </div>}
        </>}
        {selected && <div className="attribute-defaults-actions">
          <button type="button" className="secondary" disabled={busy} onClick={reset}>Vazgeç</button>
          <button type="button" disabled={busy || !canSave} onClick={() => void save()}>{busy ? 'Kaydediliyor…' : 'Varsayılanı kaydet'}</button>
        </div>}
      </div>
    </div>
  </details>
}
