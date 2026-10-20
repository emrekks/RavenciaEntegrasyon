import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { useConfirmDialog } from '../../shared/components'
import { mappingSaveErrorMessage } from './mappings/mapping-refresh'
import './mapping-conflicts-panel.css'

type ConflictOption = { externalId: string; name: string; version: number; verifiedAt: string | null; categories: number }
type Conflict = { scopeExternalId: string; categoryName: string; attributeName: string; localValueId: string; localValueName: string; options: ConflictOption[]; templateExternalId: string | null }
type Conflicts = { sameCategory: Conflict[]; crossCategory: Conflict[] }

// Value mappings worth a look: a panel value mapped to several marketplace values in one category
// (only one can be sent, so the user keeps one) and a panel value mapped differently across
// categories (shown for information, with the value other categories inherit).
export function MappingConflictsPanel({ connectionId, platformLabel, onNotice, expanded = false }: { connectionId: string; platformLabel: string; onNotice: (value: string) => void; expanded?: boolean }) {
  const client = useQueryClient()
  const conflictsKey = ['mapping-conflicts', connectionId] as const
  // Always read fresh: any mapping save elsewhere on the page can add or clear a conflict.
  const conflicts = useQuery({ queryKey: conflictsKey, queryFn: () => hubApi<Conflicts>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 0, refetchOnWindowFocus: true })
  const [busyKey, setBusyKey] = useState('')
  const [confirm, confirmElement] = useConfirmDialog()
  const same = conflicts.data?.sameCategory ?? []
  const cross = conflicts.data?.crossCategory ?? []
  if (!same.length && !cross.length) return confirmElement

  // Removing the other mappings is not undoable, so it is confirmed first and then done against the versions the
  // server holds now, not the ones this list showed when it was loaded.
  async function keep(conflict: Conflict, kept: ConflictOption) {
    const others = conflict.options.filter(item => item.externalId !== kept.externalId)
    const confirmed = await confirm({
      title: 'Diğer eşlemeler kaldırılsın mı?',
      message: `“${conflict.localValueName}” (${conflict.categoryName} · ${conflict.attributeName}) için yalnızca “${kept.name}” kalacak; ${others.map(option => `“${option.name}”`).join(', ')} bu kategoriden kaldırılacak.`,
      confirmLabel: 'Kaldır ve Bunu Tut',
      destructive: true
    })
    if (!confirmed) return
    const key = `${conflict.scopeExternalId}:${conflict.localValueId}`
    setBusyKey(key)
    try {
      const fresh = await client.fetchQuery({ queryKey: conflictsKey, queryFn: () => hubApi<Conflicts>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`), staleTime: 0 })
      const current = fresh.sameCategory.find(item => item.scopeExternalId === conflict.scopeExternalId && item.localValueId === conflict.localValueId)
      if (!current || !current.options.some(option => option.externalId === kept.externalId)) {
        onNotice('Bu eşleme bu sırada değişti; liste yenilendi. Güncel duruma göre yeniden seçin.')
        return
      }
      for (const option of current.options.filter(item => item.externalId !== kept.externalId)) {
        await hubApi(`/mappings/attribute-values/${current.localValueId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(current.scopeExternalId)}&externalId=${encodeURIComponent(option.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${option.version}"` } })
      }
      onNotice(`“${conflict.localValueName}” için ${conflict.categoryName} kategorisinde yalnızca “${kept.name}” bırakıldı.`)
    } catch (reason) {
      onNotice(mappingSaveErrorMessage(reason, 'Çakışan eşleme kaldırılamadı.'))
    } finally {
      setBusyKey('')
      await client.invalidateQueries({ queryKey: conflictsKey })
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, conflict.scopeExternalId] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
    }
  }

  return <>{confirmElement}<details className="mapping-conflicts-panel" open={expanded || undefined}>
    <summary>
      <span className="panel-summary-title">Gözden Geçirilecek Eşlemeler</span>
      <b className="panel-summary-count is-warning" aria-label={`${same.length + cross.length} kayıt`}>{same.length + cross.length}</b>
      <small>Aynı panel değeri birden fazla {platformLabel} değerine bağlanmış.</small>
    </summary>
    <div className="mapping-conflicts-body">
      {same.length > 0 && <section>
        <h4>Aynı Kategoride Birden Fazla Değer <span className="mc-count">{same.length}</span></h4>
        <p>{platformLabel}’a yalnızca biri gönderilebilir; kalacak değeri seçin, diğerleri bu kategoriden kaldırılır.</p>
        <ul>
          {same.map(conflict => {
            const key = `${conflict.scopeExternalId}:${conflict.localValueId}`
            return <li key={key}>
              <div className="mapping-conflict-head"><strong>{conflict.localValueName}</strong><span>{conflict.categoryName} · {conflict.attributeName}</span></div>
              <div className="mapping-conflict-options">
                {conflict.options.map(option => <button type="button" key={option.externalId} disabled={busyKey !== ''} onClick={() => void keep(conflict, option)} title={`Yalnızca “${option.name}” kalsın`}>
                  {option.name}{option.externalId === conflict.templateExternalId ? <em>Şablon</em> : null}<b>Bunu Tut</b>
                </button>)}
              </div>
            </li>
          })}
        </ul>
      </section>}
      {cross.length > 0 && <section>
        <h4>Kategoriler Arasında Farklı Değer <span className="mc-count">{cross.length}</span></h4>
        <p>Bazıları bilerek yapılmış olabilir. Yeni kategoriler “Şablon” işaretli değeri alır; değiştirmek için o değeri istediğiniz kategoride yeniden kaydedin.</p>
        <ul>
          {cross.map(conflict => <li key={`${conflict.attributeName}:${conflict.localValueId}`}>
            <div className="mapping-conflict-head"><strong>{conflict.localValueName}</strong><span>{conflict.attributeName}</span></div>
            <div className="mapping-conflict-options is-info">
              {conflict.options.map(option => <span key={option.externalId}>{option.name} <small>{option.categories} kategori</small>{option.externalId === conflict.templateExternalId ? <em>Şablon</em> : null}</span>)}
            </div>
          </li>)}
        </ul>
      </section>}
    </div>
  </details></>
}
