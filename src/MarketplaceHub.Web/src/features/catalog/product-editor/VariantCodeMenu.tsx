import { useEffect, useRef, useState, type KeyboardEvent } from 'react'

// Body of the variant table's Barkod and Stok Kodu header menus: what to do (tab) and to whom (scope), a preview and one
// button that says the result. It only chooses; the editor's own handlers do the work.
export type VariantCodeMenuScope = 'missing' | 'all'
type TabId = 'generate' | 'paste' | 'clear' | 'fromBarcode'
const fmt = (value: number) => value.toLocaleString('tr-TR')

export type VariantCodeMenuProps = {
  noun: 'Barkod' | 'Stok Kodu'
  rowCount: number
  emptyCount: number
  filledCount: number
  /** Generating needs a model code. */
  canGenerate: boolean
  /** First and last code a generation would write, per scope. */
  generatePreview: Record<VariantCodeMenuScope, [string, string] | null>
  onGenerate: (scope: VariantCodeMenuScope) => void
  onPaste: (scope: VariantCodeMenuScope) => void
  onClear: () => void
  /** Stok Kodu only: copy the barcodes into the stock codes. */
  fromBarcode?: { missingCount: number; allCount: number; onApply: (scope: VariantCodeMenuScope) => void }
}

export function VariantCodeMenu(props: VariantCodeMenuProps) {
  const { noun, rowCount, emptyCount, filledCount, canGenerate, generatePreview, onGenerate, onPaste, onClear, fromBarcode } = props
  const lower = noun === 'Barkod' ? 'barkod' : 'stok kodu'
  const accusative = noun === 'Barkod' ? 'Barkodu' : 'Stok Kodunu'
  const tabs: Array<{ id: TabId; label: string }> = [
    { id: 'generate', label: 'Üret' },
    { id: 'paste', label: 'Yapıştır' },
    ...(fromBarcode ? [{ id: 'fromBarcode' as const, label: 'Barkoddan' }] : []),
    { id: 'clear', label: 'Temizle' },
  ]
  const [tab, setTab] = useState<TabId>('generate')
  const [chosenScope, setChosenScope] = useState<VariantCodeMenuScope | null>(null)
  const tabRefs = useRef<Record<string, HTMLButtonElement | null>>({})
  useEffect(() => { tabRefs.current.generate?.focus() }, [])

  const counts: Record<VariantCodeMenuScope, number> = tab === 'fromBarcode' && fromBarcode
    ? { missing: fromBarcode.missingCount, all: fromBarcode.allCount }
    : { missing: emptyCount, all: rowCount }
  const defaultScope: VariantCodeMenuScope = counts.missing > 0 ? 'missing' : 'all'
  const scope: VariantCodeMenuScope = chosenScope && counts[chosenScope] > 0 ? chosenScope : defaultScope
  const count = tab === 'clear' ? filledCount : counts[scope]
  const hasScope = tab !== 'clear'
  const blocked = count < 1 || (tab === 'generate' && !canGenerate)

  const moveTab = (event: KeyboardEvent<HTMLElement>) => {
    const index = tabs.findIndex(item => item.id === tab)
    let next = index
    if (event.key === 'ArrowRight') next = (index + 1) % tabs.length
    else if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length
    else if (event.key === 'Home') next = 0
    else if (event.key === 'End') next = tabs.length - 1
    else return
    event.preventDefault()
    setTab(tabs[next].id)
    tabRefs.current[tabs[next].id]?.focus()
  }

  const preview = ((): { text: string; danger?: boolean; code?: boolean } => {
    if (tab === 'clear') return { text: `${fmt(filledCount)} ${lower} boşalır`, danger: true }
    if (tab === 'paste') return { text: `Panodaki sıra ile ${fmt(count)} varyant` }
    if (tab === 'fromBarcode') return { text: `Barkodu olan satırlarda stok kodu barkod olur · ${fmt(count)} varyant` }
    if (!canGenerate) return { text: 'Önce model kodu girin' }
    const range = generatePreview[scope]
    if (!range) return { text: 'Üretilecek varyant yok' }
    return { text: range[0] === range[1] ? range[0] : `${range[0]} … ${range[1]}`, code: true }
  })()

  const label = ((): string => {
    const number = fmt(count)
    if (tab === 'clear') return `${number} ${accusative} Temizle`
    if (tab === 'fromBarcode') return scope === 'missing' ? `${number} Boş Stok Kodunu Doldur` : `${number} Satıra Barkodu Uygula`
    if (tab === 'paste') return scope === 'missing' ? `${number} Boş ${accusative} Doldur` : `${number} ${accusative} Yapıştır`
    return scope === 'missing' ? `${number} Boş ${accusative} Üret` : `${number} ${accusative} Üret`
  })()

  const run = () => {
    if (blocked) return
    if (tab === 'clear') onClear()
    else if (tab === 'generate') onGenerate(scope)
    else if (tab === 'paste') onPaste(scope)
    else fromBarcode?.onApply(scope)
  }

  const scopeRows: Array<{ id: VariantCodeMenuScope; label: string }> = [{ id: 'all', label: 'Tüm Varyantlar' }, { id: 'missing', label: 'Yalnız Boş Olanlar' }]
  return <div className="variant-code-menu" data-tab={tab}>
    <div className="variant-code-menu-tabs" role="tablist" aria-label={`${noun} işlemi`}>
      {tabs.map(item => <button key={item.id} type="button" role="tab" onKeyDown={moveTab} ref={element => { tabRefs.current[item.id] = element }} aria-selected={tab === item.id} tabIndex={tab === item.id ? 0 : -1} className={tab === item.id ? 'is-active' : undefined} onClick={() => setTab(item.id)}>{item.label}</button>)}
    </div>
    {hasScope && <div className="variant-code-menu-scopes" role="radiogroup" aria-label="Kapsam">
      {scopeRows.map(row => {
        const rowTotal = counts[row.id]
        return <label key={row.id} className={`variant-code-menu-scope${scope === row.id ? ' is-selected' : ''}${rowTotal < 1 ? ' is-disabled' : ''}`}>
          <input type="radio" name="variant-code-scope" value={row.id} checked={scope === row.id} disabled={rowTotal < 1} onChange={() => setChosenScope(row.id)} />
          <span className="variant-code-menu-scope-dot" aria-hidden="true" />
          <span className="variant-code-menu-scope-name">{row.label}</span>
          <small>{fmt(rowTotal)} varyant</small>
        </label>
      })}
    </div>}
    <p className={`variant-code-menu-preview${preview.danger ? ' is-danger' : ''}`} aria-live="polite">{preview.code ? <code>{preview.text}</code> : preview.text}</p>
    <button type="button" className={`variant-code-menu-submit${tab === 'clear' ? ' is-danger' : ''}`} disabled={blocked} onClick={run}>{label}</button>
  </div>
}
