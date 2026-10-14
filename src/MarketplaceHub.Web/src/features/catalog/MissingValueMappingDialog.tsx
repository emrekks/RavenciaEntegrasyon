import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQueries, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { UiIcon } from '../../shared/components'
import type { PublicationValueMappingFix } from './publication-attribute-readiness'
import { filterValueCandidates, rankValueCandidates, type ValueCandidate } from './value-mapping-suggestions'
import './missing-value-mapping-dialog.css'

type ValueList = { snapshotId: string; items: Array<ValueCandidate & { isActive: boolean }> }
type ValueMapping = { localId: string; externalId: string; version: number }

// "Şimdi eşle": maps the panel values a publish card reports as unmapped without leaving the
// product page. Each value gets the closest marketplace values as suggestions plus a search over
// the whole list; saving also updates the connection-wide template, so other categories inherit it.
export function MissingValueMappingDialog({ connectionId, platformName, categoryScope, fixes, onClose, onSaved }: {
  connectionId: string
  platformName: string
  categoryScope: string
  fixes: PublicationValueMappingFix[]
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const client = useQueryClient()
  const attributeIds = useMemo(() => [...new Set(fixes.map(fix => fix.externalAttributeId))], [fixes])
  const lists = useQueries({
    queries: attributeIds.map(externalAttributeId => ({
      queryKey: ['reference-attribute-values', connectionId, `${categoryScope}/${externalAttributeId}`],
      queryFn: () => hubApi<ValueList>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`),
      retry: false
    }))
  })
  const [choices, setChoices] = useState<Record<string, string>>({})
  const [searches, setSearches] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const keyOf = (fix: PublicationValueMappingFix) => `${fix.externalAttributeId}:${fix.localValueId}`
  const listFor = (fix: PublicationValueMappingFix) => lists[attributeIds.indexOf(fix.externalAttributeId)]

  useEffect(() => {
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape' && !saving) onClose() }
    document.addEventListener('keydown', close)
    return () => document.removeEventListener('keydown', close)
  }, [onClose, saving])

  async function save() {
    setSaving(true); setError('')
    let saved = 0
    try {
      for (const fix of fixes) {
        const externalId = choices[keyOf(fix)]
        const list = listFor(fix)?.data
        if (!externalId || !list) continue
        const scope = `${categoryScope}/${fix.externalAttributeId}`
        const existing = (await hubApi<ValueMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(scope)}`))
          .find(mapping => mapping.localId === fix.localValueId && mapping.externalId === externalId)
        await hubApi(`/mappings/attribute-values/${fix.localValueId}`, {
          method: 'PUT',
          headers: existing ? { 'If-Match': `"v${existing.version}"` } : {},
          body: JSON.stringify({ connectionId, snapshotId: list.snapshotId, scopeExternalId: scope, externalId, status: 'VERIFIED', updateTemplate: true })
        })
        saved++
        await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, scope] })
      }
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onSaved(saved ? `${saved} değer eşlemesi kaydedildi. Diğer kategoriler de bu eşlemeyi kullanacak.` : 'Kaydedilecek seçim yapılmadı.')
      onClose()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Değer eşlemeleri kaydedilemedi.')
    } finally { setSaving(false) }
  }

  const chosenCount = fixes.filter(fix => choices[keyOf(fix)]).length
  return createPortal(<div className="missing-value-mapping-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !saving) onClose() }}>
    <section className="missing-value-mapping" role="dialog" aria-modal="true" aria-labelledby="missing-value-mapping-title">
      <header>
        <div>
          <strong id="missing-value-mapping-title">Eksik değer eşlemeleri</strong>
          <small>{platformName} değerini seçin; seçiminiz bu özelliği kullanan diğer kategorilere de uygulanır.</small>
        </div>
        <button type="button" aria-label="Kapat" onClick={onClose} disabled={saving}><UiIcon name="close" /></button>
      </header>
      <div className="missing-value-mapping-body">
        {fixes.map(fix => {
          const key = keyOf(fix)
          const list = listFor(fix)
          const items = (list?.data?.items ?? []).filter(item => item.isActive)
          const search = searches[key] ?? ''
          const candidates = search.trim() ? filterValueCandidates(search, items) : rankValueCandidates(fix.valueLabel, items)
          const chosen = choices[key]
          const chosenName = items.find(item => item.externalId === chosen)?.name
          return <div className="missing-value-mapping-item" key={key}>
            <div className="missing-value-mapping-target">
              <span>{fix.attributeName}</span>
              <strong>{fix.valueLabel || 'Seçili değer'}</strong>
              {chosenName && <em>→ {chosenName}</em>}
            </div>
            {list?.isLoading ? <p className="missing-value-mapping-state">{platformName} değer listesi yükleniyor…</p>
              : list?.isError ? <p className="missing-value-mapping-state is-error">{platformName} değer listesi henüz alınmamış. Eşleştirme Ayarları’ndan bu özelliğin değerlerini eşitleyin.</p>
                : <>
                  <input type="search" value={search} placeholder={`${platformName} değerlerinde ara`} aria-label={`${fix.valueLabel} için ${platformName} değeri ara`} onChange={event => setSearches(current => ({ ...current, [key]: event.target.value }))} />
                  <div className="missing-value-mapping-candidates" role="listbox" aria-label={`${fix.valueLabel} için ${platformName} değerleri`}>
                    {candidates.map(candidate => <button type="button" role="option" aria-selected={chosen === candidate.externalId} key={candidate.externalId} className={chosen === candidate.externalId ? 'is-chosen' : undefined} onClick={() => setChoices(current => ({ ...current, [key]: candidate.externalId }))}>{candidate.name}</button>)}
                    {!candidates.length && <p className="missing-value-mapping-state">{search.trim() ? 'Aramayla eşleşen değer yok.' : 'Benzer adlı değer bulunamadı; aramayla seçin.'}</p>}
                  </div>
                </>}
          </div>
        })}
      </div>
      {error && <p role="alert" className="missing-value-mapping-state is-error">{error}</p>}
      <footer>
        <button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button>
        <button type="button" onClick={() => void save()} disabled={saving || chosenCount === 0}>{saving ? 'Kaydediliyor…' : `Eşlemeleri kaydet (${chosenCount})`}</button>
      </footer>
    </section>
  </div>, document.body)
}
