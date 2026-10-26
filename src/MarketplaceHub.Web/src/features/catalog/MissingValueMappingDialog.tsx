import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQueries, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { UiIcon, DialogSurface } from '../../shared/components'
import type { PublicationValueMappingFix } from './publication-attribute-readiness'
import { filterValueCandidates, rankValueCandidates, type ValueCandidate } from './value-mapping-suggestions'
import { mappingSaveErrorMessage, refreshMappingConflicts, saveAllMappings } from '../marketplace/mappings/mapping-refresh'
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
    const targets = fixes.flatMap(fix => {
      const externalId = choices[keyOf(fix)]
      const list = listFor(fix)?.data
      return externalId && list ? [{ fix, externalId, snapshotId: list.snapshotId, scope: `${categoryScope}/${fix.externalAttributeId}` }] : []
    })
    // Every chosen value is attempted; a refused one is reported by name and the rest are still saved. The version
    // is read right before each save, so a mapping changed meanwhile is updated from its current version.
    const result = await saveAllMappings(targets, async target => {
      const existing = (await hubApi<ValueMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(target.scope)}`))
        .find(mapping => mapping.localId === target.fix.localValueId && mapping.externalId === target.externalId)
      await hubApi(`/mappings/attribute-values/${target.fix.localValueId}`, {
        method: 'PUT',
        headers: existing ? { 'If-Match': `"v${existing.version}"` } : {},
        body: JSON.stringify({ connectionId, snapshotId: target.snapshotId, scopeExternalId: target.scope, externalId: target.externalId, status: 'VERIFIED', updateTemplate: true })
      })
    })
    try {
      for (const scope of new Set(targets.map(target => target.scope))) await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, scope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      await refreshMappingConflicts(client, connectionId)
    } finally { setSaving(false) }
    if (result.failures.length) {
      setError(`${result.saved} eşleme kaydedildi, ${result.failures.length} eşleme kaydedilemedi: ${result.failures.map(failure => `${failure.item.fix.valueLabel || failure.item.fix.attributeName}: ${mappingSaveErrorMessage(failure.error, 'kaydedilemedi')}`).join(' · ')}`)
      return
    }
    onSaved(result.saved ? `${result.saved} değer eşlemesi kaydedildi. Diğer kategoriler de bu eşlemeyi kullanacak.` : 'Kaydedilecek seçim yapılmadı.')
    onClose()
  }

  const chosenCount = fixes.filter(fix => choices[keyOf(fix)]).length
  return createPortal(<div className="missing-value-mapping-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !saving) onClose() }}>
    <DialogSurface as="section" onClose={() => { if (!saving) onClose() }} className="missing-value-mapping" aria-labelledby="missing-value-mapping-title">
      <header>
        <div>
          <strong id="missing-value-mapping-title">Eksik Değer Eşlemeleri</strong>
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
          return <div className={`missing-value-mapping-item${chosenName ? ' is-chosen' : ''}`} key={key}>
            <div className="missing-value-mapping-target">
              <span>{fix.attributeName}</span>
              <strong>{fix.valueLabel || 'Seçili değer'}</strong>
              <span className="missing-value-mapping-arrow" aria-hidden="true"><svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M3 8h10M9 4l4 4-4 4" /></svg></span>
              {chosenName ? <em>{chosenName}</em> : <span className="missing-value-mapping-pending">{platformName} değeri seçilmedi</span>}
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
        <button type="button" onClick={() => void save()} disabled={saving || chosenCount === 0}>{saving ? 'Kaydediliyor…' : `Eşlemeleri Kaydet (${chosenCount})`}</button>
      </footer>
    </DialogSurface>
  </div>, document.body)
}
