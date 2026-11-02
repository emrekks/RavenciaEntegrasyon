import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { useConfirmDialog } from '../../shared/components'
import { mappingSaveErrorMessage } from './mappings/mapping-refresh'
import { changeLine, choicePreview, crossConflictAttribute, crossConflictKey, planUnify, scopeAttribute, scopeCategory, scopesNeedingValueList, unifySummary, type Conflict, type ConflictOption, type Conflicts, type UnifyTarget, type ValueList } from './mapping-conflict-unify'
import './mapping-conflicts-panel.css'

// Value mappings worth a look: a panel value mapped to several marketplace values in one category
// (only one can be sent, so the user keeps one) and a panel value mapped differently across
// categories (the user can pick one and use it in every category, which also makes it the template).
export function MappingConflictsPanel({ connectionId, platformLabel, onNotice, expanded = false }: { connectionId: string; platformLabel: string; onNotice: (value: string) => void; expanded?: boolean }) {
  const client = useQueryClient()
  const conflictsKey = ['mapping-conflicts', connectionId] as const
  const loadConflicts = () => hubApi<Conflicts>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`)
  // Always read fresh: any mapping save elsewhere on the page can add or clear a conflict.
  const conflicts = useQuery({ queryKey: conflictsKey, queryFn: loadConflicts, retry: false, staleTime: 0, refetchOnWindowFocus: true })
  const [busyKey, setBusyKey] = useState('')
  // The marketplace value picked per cross-category row (row key → external id).
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [confirm, confirmElement] = useConfirmDialog()
  const same = conflicts.data?.sameCategory ?? []
  const cross = conflicts.data?.crossCategory ?? []
  if (!same.length && !cross.length) return confirmElement

  async function refreshAfterSave(scopes: Iterable<string>) {
    await client.invalidateQueries({ queryKey: conflictsKey })
    await Promise.all([...new Set(scopes)].map(scope => client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, scope] })))
    await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
  }

  // Removing the other mappings is not undoable, so it is confirmed first and then done against the versions the
  // server holds now, not the ones this list showed when it was loaded.
  async function keep(conflict: Conflict, kept: ConflictOption) {
    const others = conflict.options.filter(item => item.externalId !== kept.externalId)
    const confirmed = await confirm({
      title: 'Diğer Eşlemeler Kaldırılsın mı?',
      message: `“${conflict.localValueName}” (${conflict.categoryName} · ${conflict.attributeName}) için yalnızca “${kept.name}” kalacak; ${others.map(option => `“${option.name}”`).join(', ')} bu kategoriden kaldırılacak.`,
      confirmLabel: 'Kaldır ve Bunu Tut',
      destructive: true
    })
    if (!confirmed) return
    const key = `${conflict.scopeExternalId}:${conflict.localValueId}`
    setBusyKey(key)
    try {
      const fresh = await client.fetchQuery({ queryKey: conflictsKey, queryFn: loadConflicts, staleTime: 0 })
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
      await refreshAfterSave([conflict.scopeExternalId])
    }
  }

  const valueList = (scope: string) => client.fetchQuery({
    queryKey: ['reference-attribute-values', connectionId, scope],
    queryFn: () => hubApi<ValueList>(`/reference-data/categories/${encodeURIComponent(scopeCategory(scope))}/attributes/${encodeURIComponent(scopeAttribute(scope))}/values?connectionId=${encodeURIComponent(connectionId)}`),
    staleTime: 0
  })
  const putValue = (localValueId: string, scope: string, snapshotId: string, externalId: string, version: number | null) =>
    hubApi<{ version: number }>(`/mappings/attribute-values/${localValueId}`, { method: 'PUT', headers: version !== null ? { 'If-Match': `"v${version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: scope, externalId, status: 'VERIFIED', updateTemplate: true }) })

  // Sets the chosen marketplace value in every category where this panel value maps to another one, through the same
  // versioned save as mapping it in each category, and makes it the template for new categories. A category whose
  // current value list does not offer the chosen value is skipped and named, never written with a foreign id.
  async function applyEverywhere(conflict: Conflict, chosenExternalId: string) {
    const rowKey = crossConflictKey(conflict)
    const attributeId = crossConflictAttribute(conflict)
    const chosenName = conflict.options.find(option => option.externalId === chosenExternalId)?.name ?? chosenExternalId
    if (!attributeId) { onNotice('Bu satırın kategorileri okunamadı; sayfayı yenileyip yeniden deneyin.'); return }
    setBusyKey(rowKey)
    const touched = new Set<string>()
    try {
      const fresh = await client.fetchQuery({ queryKey: conflictsKey, queryFn: loadConflicts, staleTime: 0 })
      const current = fresh.crossCategory.find(item => item.localValueId === conflict.localValueId && crossConflictAttribute(item) === attributeId)
      if (!current || !current.options.some(option => option.externalId === chosenExternalId)) {
        onNotice('Bu eşleme bu sırada değişti; liste yenilendi. Güncel duruma göre yeniden seçin.')
        return
      }
      const lists = new Map<string, ValueList | Error>()
      await Promise.all(scopesNeedingValueList(current, chosenExternalId).map(async scope => {
        try { lists.set(scope, await valueList(scope)) } catch (reason) { lists.set(scope, reason instanceof Error ? reason : new Error('')) }
      }))
      const plan = planUnify(current, chosenExternalId, lists, platformLabel)
      const templateChanges = current.templateExternalId !== chosenExternalId
      const skippedText = plan.skipped.map(item => `${item.categoryName} (${item.reason})`).join('; ')
      if (!plan.targets.length && (!templateChanges || !plan.anchor)) {
        onNotice(plan.skipped.length ? `“${chosenName}” hiçbir kategoriye yazılmadı. Atlandı: ${skippedText}.` : `“${conflict.localValueName}” için değişecek kategori yok.`)
        return
      }
      const confirmed = await confirm({
        title: `Hepsinde “${chosenName}” Kullanılsın mı?`,
        message: <div className="mc-unify-preview">
          {plan.targets.length > 0 ? <><p><strong>{unifySummary(current, plan, chosenName)}</strong></p>
            <ul>{plan.targets.map(target => <li key={target.scopeExternalId}>{changeLine(target, chosenName)}</li>)}</ul></>
            : <p>Hiçbir kategoride değer değişmez.</p>}
          {plan.skipped.length > 0 && <><p>Atlanacak:</p><ul>{plan.skipped.map(item => <li key={item.scopeExternalId}>{item.categoryName}: {item.reason}; {item.fromNames.join(', ')} kalır.</li>)}</ul></>}
          <p>“{chosenName}” yeni kategoriler için şablon olur.</p>
        </div>,
        confirmLabel: 'Hepsinde Bunu Kullan'
      })
      if (!confirmed) return

      let puts = 0
      let deletes = 0
      const done: UnifyTarget[] = []
      const failed: Array<{ target: UnifyTarget; message: string }> = []
      for (const target of plan.targets) {
        touched.add(target.scopeExternalId)
        try {
          // The chosen value goes in first, so a category never loses its value when a later step is refused.
          if (target.snapshotId) { await putValue(current.localValueId, target.scopeExternalId, target.snapshotId, chosenExternalId, null); puts++ }
          for (const item of target.remove) {
            await hubApi(`/mappings/attribute-values/${current.localValueId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(target.scopeExternalId)}&externalId=${encodeURIComponent(item.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${item.version}"` } })
            deletes++
          }
          done.push(target)
        } catch (reason) {
          failed.push({ target, message: mappingSaveErrorMessage(reason, 'kaydedilemedi') })
        }
      }
      // A saved value already became the template; with none saved (or only removals, which re-pick the template from
      // what is left), the chosen value is saved again where it is already mapped so it becomes the template.
      let templateProblem = ''
      if (!puts && (templateChanges || deletes > 0) && plan.anchor) {
        const anchor = plan.anchor
        touched.add(anchor.scopeExternalId)
        try {
          const list = await valueList(anchor.scopeExternalId)
          if (!list.items.some(item => item.externalId === chosenExternalId && item.isActive !== false)) templateProblem = `“${chosenName}” şablon yapılamadı: değer listesinde artık yok.`
          else await putValue(current.localValueId, anchor.scopeExternalId, list.snapshotId, chosenExternalId, anchor.version)
        } catch (reason) {
          templateProblem = `“${chosenName}” şablon yapılamadı: ${mappingSaveErrorMessage(reason, 'kaydedilemedi')}`
        }
      }

      const parts: string[] = []
      if (done.length) parts.push(`“${conflict.localValueName}” için ${done.length} kategoride ${chosenName} kullanılıyor (${done.map(target => target.categoryName).join(', ')}).`)
      else if (!failed.length) parts.push(`“${conflict.localValueName}” için kategorilerde değer değişmedi.`)
      if (failed.length) parts.push(`Kaydedilemedi: ${failed.map(item => `${item.target.categoryName} — ${item.message}`).join('; ')}.`)
      if (plan.skipped.length) parts.push(`Atlandı: ${skippedText}.`)
      parts.push(templateProblem || (done.length || !failed.length ? `Yeni kategoriler “${chosenName}” alır.` : ''))
      onNotice(parts.filter(Boolean).join(' '))
      setPicked(value => { const next = { ...value }; delete next[rowKey]; return next })
    } catch (reason) {
      onNotice(mappingSaveErrorMessage(reason, 'Değer eşitlenemedi.'))
    } finally {
      setBusyKey('')
      await refreshAfterSave(touched)
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
        <p>Bazıları bilerek yapılmış olabilir. Yeni kategoriler “Şablon” işaretli değeri alır. Eşitlemek için bir değer seçip “Hepsinde Bunu Kullan”a basın.</p>
        <ul>
          {cross.map(conflict => {
            const rowKey = crossConflictKey(conflict)
            const choosable = crossConflictAttribute(conflict) !== ''
            const selected = picked[rowKey] ?? ''
            const selectedName = conflict.options.find(option => option.externalId === selected)?.name
            const choose = (externalId: string) => setPicked(value => ({ ...value, [rowKey]: externalId }))
            // Arrow keys move the choice like any radio group.
            const step = (index: number, delta: number, group: HTMLElement | null) => {
              const next = conflict.options[(index + delta + conflict.options.length) % conflict.options.length]
              choose(next.externalId)
              group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[conflict.options.indexOf(next)]?.focus()
            }
            return <li key={rowKey} className="mc-cross-row">
              <div className="mapping-conflict-head"><strong>{conflict.localValueName}</strong><span>{conflict.attributeName}</span></div>
              <div className="mc-choice" role="radiogroup" aria-label={`${conflict.localValueName} için kullanılacak ${platformLabel} değeri`}>
                {conflict.options.map((option, index) => {
                  const checked = selected === option.externalId
                  return <button type="button" role="radio" key={option.externalId} aria-checked={checked} tabIndex={checked || (!selected && index === 0) ? 0 : -1} disabled={busyKey !== '' || !choosable}
                    onClick={() => choose(option.externalId)}
                    onKeyDown={event => {
                      if (event.key === 'ArrowRight' || event.key === 'ArrowDown') { event.preventDefault(); step(index, 1, event.currentTarget.parentElement) }
                      if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') { event.preventDefault(); step(index, -1, event.currentTarget.parentElement) }
                    }}
                    title={option.scopes?.length ? option.scopes.map(scope => scope.categoryName).join(', ') : undefined}>
                    {checked && <svg className="mc-choice-check" viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="m3.5 8.5 3 3 6-7" /></svg>}
                    <span className="mc-choice-name">{option.name}</span><small>{option.categories} kategori</small>{option.externalId === conflict.templateExternalId ? <em>Şablon</em> : null}
                  </button>
                })}
              </div>
              <button type="button" className={`mc-apply${selected ? '' : ' secondary'}`} disabled={busyKey !== '' || !selected} onClick={() => void applyEverywhere(conflict, selected)}
                title={selectedName ? `Bütün kategorilerde “${selectedName}” kullanılsın` : 'Önce bir değer seçin'}>
                {busyKey === rowKey ? 'Kaydediliyor…' : 'Hepsinde Bunu Kullan'}
              </button>
              {selectedName && <p className="mc-choice-preview" aria-live="polite">{choicePreview(conflict, selected, selectedName)}</p>}
            </li>
          })}
        </ul>
      </section>}
    </div>
  </details></>
}
