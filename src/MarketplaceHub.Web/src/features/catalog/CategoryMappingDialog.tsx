import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { DialogSurface, UiIcon } from '../../shared/components'
import { ValuePicker, pickerOptions } from '../marketplace/mappings/ValuePicker'
import { mappingSaveErrorMessage } from '../marketplace/mappings/mapping-refresh'
import './missing-value-mapping-dialog.css'

type ReferenceCategories = { snapshotId: string; items: Array<{ externalId: string; name: string; path?: string | null; isLeaf: boolean; isActive: boolean }> }
type CategoryMapping = { externalId: string; version: number; status?: string; snapshotId?: string }

// Why a saved category mapping no longer counts (the publish check reads it the same way), or '' when it does.
function savedCategoryMappingProblem(saved: CategoryMapping | null | undefined, references: ReferenceCategories | undefined, platformName: string) {
  if (!saved || !references) return ''
  const item = references.items.find(candidate => candidate.externalId === saved.externalId)
  if (!item || !item.isActive) return `Kayıtlı kategori artık ${platformName} kategori listesinde yok; yeni bir kategori seçin.`
  if (!item.isLeaf) return `Kayıtlı kategori ${platformName}'da alt kategorileri olan bir kategori; ürün açılabilecek bir alt kategori seçin.`
  if (saved.status && saved.status !== 'VERIFIED') return 'Kayıtlı eşleme doğrulanmamış; kaydederek doğrulayın.'
  if (saved.snapshotId && saved.snapshotId !== references.snapshotId) return `Kayıtlı eşleme eski ${platformName} kategori listesine göre yapılmış; aynı kategoriyi yeniden kaydedin.`
  return ''
}

// "Kategori Eşle" on a publish card: the panel category gets the store's category the user picks (the same save as the
// Eşleştirmeler page). Nothing is matched by name; the list is only searched.
export function CategoryMappingDialog({ connectionId, platformName, categoryId, categoryName, onClose, onSaved }: {
  connectionId: string
  platformName: string
  categoryId: string
  categoryName: string
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const client = useQueryClient()
  const references = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceCategories>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const existing = useQuery({ queryKey: ['category-mapping', categoryId, connectionId], queryFn: () => hubApi<CategoryMapping | null>(`/mappings/categories/${categoryId}?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const [externalId, setExternalId] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  // Only categories a product can be listed in (active leaves), named by their path.
  const options = useMemo(() => pickerOptions((references.data?.items ?? []).filter(item => item.isLeaf && item.isActive).map(item => ({ externalId: item.externalId, name: item.path?.trim() || item.name }))), [references.data])

  useEffect(() => {
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape' && !saving) onClose() }
    document.addEventListener('keydown', close)
    return () => document.removeEventListener('keydown', close)
  }, [onClose, saving])

  // The saved category is shown chosen. Kaydet takes a new choice, or the same one again when the saved mapping no longer
  // counts (not verified, an old list, a category the store dropped); a valid saved mapping needs no save.
  const savedProblem = savedCategoryMappingProblem(existing.data, references.data, platformName)
  const chosen = externalId || existing.data?.externalId || ''
  const savable = Boolean(chosen) && (Boolean(externalId && externalId !== existing.data?.externalId) || !existing.data || Boolean(savedProblem))
  async function save() {
    if (!references.data || !chosen) return
    const externalId = chosen
    setSaving(true); setError('')
    try {
      // The version is read right before the save, so a mapping changed meanwhile is updated from its current version.
      const current = await hubApi<CategoryMapping | null>(`/mappings/categories/${categoryId}?connectionId=${encodeURIComponent(connectionId)}`)
      await hubApi(`/mappings/categories/${categoryId}`, { method: 'PUT', headers: current ? { 'If-Match': `"v${current.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) })
      await Promise.all([
        client.invalidateQueries({ queryKey: ['category-mapping', categoryId, connectionId] }),
        client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }),
        client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      ])
      onSaved(`${categoryName} kategorisi ${platformName} kategorisiyle eşlendi.`)
      onClose()
    } catch (reason) {
      setError(mappingSaveErrorMessage(reason, 'Kategori eşlemesi kaydedilemedi.'))
    } finally { setSaving(false) }
  }

  const listMissing = references.isError
  return createPortal(<div className="missing-value-mapping-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !saving) onClose() }}>
    <DialogSurface as="section" onClose={() => { if (!saving) onClose() }} className="missing-value-mapping category-mapping-dialog" aria-labelledby="category-mapping-dialog-title">
      <header>
        <div>
          <strong id="category-mapping-dialog-title">Kategori Eşle</strong>
          <small>{categoryName} kategorisinin {platformName} karşılığını seçin; bu kategorideki tüm ürünler için geçerli olur.</small>
        </div>
        <button type="button" aria-label="Kapat" onClick={onClose} disabled={saving}><UiIcon name="close" /></button>
      </header>
      <div className="missing-value-mapping-body">
        <div className="missing-value-mapping-item">
          <div className="missing-value-mapping-target"><span>Panel kategorisi</span><strong>{categoryName}</strong></div>
          {references.isLoading || existing.isLoading ? <p className="missing-value-mapping-state">{platformName} kategori listesi yükleniyor…</p>
            : listMissing ? <p className="missing-value-mapping-state is-error">{platformName} kategori listesi henüz alınmamış. Eşleştirmeler sayfasından kategori listesini eşitleyin.</p>
              : <ValuePicker value={chosen} options={options} label={`${categoryName} için ${platformName} kategorisi`} onChange={setExternalId} />}
          {!references.isLoading && !existing.isLoading && existing.data && !externalId && (savedProblem
            ? <p className="missing-value-mapping-state is-error">{savedProblem}</p>
            : <p className="missing-value-mapping-state">Bu eşleme kayıtlı ve geçerli; değiştirmek için başka bir kategori seçin.</p>)}
        </div>
      </div>
      {error && <p role="alert" className="missing-value-mapping-state is-error">{error}</p>}
      <footer>
        <button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button>
        <button type="button" onClick={() => void save()} disabled={saving || !savable || listMissing}>{saving ? 'Kaydediliyor…' : 'Eşlemeyi Kaydet'}</button>
      </footer>
    </DialogSurface>
  </div>, document.body)
}
