import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import './mapping-conflicts-panel.css'

type ConflictOption = { externalId: string; name: string; version: number; verifiedAt: string | null; categories: number }
type Conflict = { scopeExternalId: string; categoryName: string; attributeName: string; localValueId: string; localValueName: string; options: ConflictOption[]; templateExternalId: string | null }
type Conflicts = { sameCategory: Conflict[]; crossCategory: Conflict[] }

// Value mappings worth a look: a panel value mapped to several marketplace values in one category
// (only one can be sent, so the user keeps one) and a panel value mapped differently across
// categories (shown for information, with the value other categories inherit).
export function MappingConflictsPanel({ connectionId, platformLabel, onNotice }: { connectionId: string; platformLabel: string; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const conflictsKey = ['mapping-conflicts', connectionId] as const
  const conflicts = useQuery({ queryKey: conflictsKey, queryFn: () => hubApi<Conflicts>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 30_000 })
  const [busyKey, setBusyKey] = useState('')
  const same = conflicts.data?.sameCategory ?? []
  const cross = conflicts.data?.crossCategory ?? []
  if (!same.length && !cross.length) return null

  async function keep(conflict: Conflict, kept: ConflictOption) {
    const key = `${conflict.scopeExternalId}:${conflict.localValueId}`
    setBusyKey(key)
    try {
      for (const option of conflict.options.filter(item => item.externalId !== kept.externalId)) {
        await hubApi(`/mappings/attribute-values/${conflict.localValueId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(conflict.scopeExternalId)}&externalId=${encodeURIComponent(option.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${option.version}"` } })
      }
      await client.invalidateQueries({ queryKey: conflictsKey })
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, conflict.scopeExternalId] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onNotice(`“${conflict.localValueName}” için ${conflict.categoryName} kategorisinde yalnızca “${kept.name}” bırakıldı.`)
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Çakışan eşleme kaldırılamadı.')
    } finally { setBusyKey('') }
  }

  return <details className="mapping-conflicts-panel">
    <summary>
      <span>Gözden geçirilecek eşlemeler ({same.length + cross.length})</span>
      <small>Aynı panel değeri birden fazla {platformLabel} değerine bağlanmış.</small>
    </summary>
    <div className="mapping-conflicts-body">
      {same.length > 0 && <section>
        <h4>Aynı kategoride birden fazla değer ({same.length})</h4>
        <p>{platformLabel}’a yalnızca biri gönderilebilir; kalacak değeri seçin, diğerleri bu kategoriden kaldırılır.</p>
        <ul>
          {same.map(conflict => {
            const key = `${conflict.scopeExternalId}:${conflict.localValueId}`
            return <li key={key}>
              <div className="mapping-conflict-head"><strong>{conflict.localValueName}</strong><span>{conflict.categoryName} · {conflict.attributeName}</span></div>
              <div className="mapping-conflict-options">
                {conflict.options.map(option => <button type="button" key={option.externalId} disabled={busyKey !== ''} onClick={() => void keep(conflict, option)} title={`Yalnızca “${option.name}” kalsın`}>
                  {option.name}{option.externalId === conflict.templateExternalId ? <em>Şablon</em> : null}<b>Bunu tut</b>
                </button>)}
              </div>
            </li>
          })}
        </ul>
      </section>}
      {cross.length > 0 && <section>
        <h4>Kategoriler arasında farklı değer ({cross.length})</h4>
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
  </details>
}
