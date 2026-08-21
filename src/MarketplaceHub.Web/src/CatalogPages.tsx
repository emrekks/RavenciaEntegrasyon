import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from './api'

type PageData<T> = { items: T[]; nextCursor: string | null; hasMore: boolean }
type Versioned = { id: string; version: number }
type Category = Versioned & { name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean }
type Brand = Versioned & { name: string; isActive: boolean }
type Attribute = Versioned & { code: string; name: string; dataType: string; values: Array<{ id: string; value: string }> }
type Variant = Versioned & {
  sku: string; barcode: string | null; modelCode: string | null; optionSignature: string; status: string
  weight: number | null; width: number | null; height: number | null; length: number | null; desi: number | null
  onHand: number; available: number; inventoryVersion: number | null
  offerId: string | null; listPrice: number | null; salePrice: number | null; currency: string | null; offerStatus: string | null
  priceVersion: number | null; offerVersion: number | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null
}
type Product = Versioned & {
  title: string; description: string; brandId: string | null; categoryId: string | null; status: string; updatedAt: string
  variants: Variant[]; primaryImageUrl: string | null; totalStock: number; startingPrice: number | null; currency: string; modelCode: string | null; activePlatforms: string[] | null
  attributes?: Array<{ attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }>
}
type ImportSession = Versioned & { sourceType: string; status: string; totalRows: number; validRows: number; errorRows: number; reviewRows: number; sourceAssetId: string | null }
type Candidate = Versioned & { matchRule: string; safeSummary: string; productId: string | null; variantId: string | null }
type Inventory = Versioned & { variantId: string; sku: string; locationCode: string; onHand: number; reserved: number; available: number }
type TrendyolConnection = { id: string; platformCode: string; displayName: string; externalStoreId: string; status: string }
type PublicationStatus = { productId: string; connectionId: string; profileId: string | null; desiredStatus: string | null; actualStatus: string | null; lastRejectionCode: string | null; lastJobId: string | null; lastJobStatus: string | null; lines: Array<{ variantId: string; sku: string; barcode: string | null; desiredStatus: string; actualStatus: string; rejectionCode: string | null }> }

const key = () => crypto.randomUUID()
const ErrorBox = ({ error }: { error: unknown }) => error ? <div className="error" role="alert">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</div> : null
const Tag = ({ children }: { children: ReactNode }) => <span className="tag">{children}</span>
const money = (value: number | null | undefined, currency = 'TRY') => value == null ? '—' : new Intl.NumberFormat('tr-TR', { style: 'currency', currency }).format(value)
function Page({ title, eyebrow, action, children }: { title: string; eyebrow: string; action?: ReactNode; children: ReactNode }) { return <section className="content stitch-page"><div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1></div>{action}</div>{children}</section> }

type QuickEditMode = 'stock' | 'price' | 'both'

function VariantQuickEditor({ variant, connections, mode = 'both', onChanged }: { variant: Variant; connections: TrendyolConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown> }) {
  const [notice, setNotice] = useState('')
  async function stock(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget); const target = Number(data.get('stock')); const delta = target - variant.onHand
    if (!Number.isFinite(target) || target < 0) return setNotice('Stok sıfır veya daha büyük olmalıdır.')
    if (delta === 0) return setNotice('Stok zaten bu değerde.')
    try { await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Ürün kartı hızlı stok düzenleme', sourceEventId: key() }) }); setNotice('Stok güncellendi.'); await onChanged() } catch (error) { setNotice(error instanceof Error ? error.message : 'Stok güncellenemedi.') }
  }
  async function price(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget); const salePrice = Number(data.get('salePrice')); const listPrice = Number(data.get('listPrice')); const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''
    if (!Number.isFinite(salePrice) || !Number.isFinite(listPrice) || salePrice < 0 || listPrice < salePrice) return setNotice('Liste fiyatı satış fiyatından küçük olamaz.')
    const body = { connectionId, variantId: variant.id, listPrice, salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Ürün kartı hızlı fiyat düzenleme' }
    try {
      if (variant.offerId) {
        if (variant.offerVersion == null) return setNotice('Fiyat sürümü eksik; sayfayı yenileyip tekrar deneyin.')
        await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${variant.offerVersion}"` }, body: JSON.stringify(body) })
      }
      else {
        if (!connectionId) return setNotice('İlk fiyat için aktif platform bağlantısı seçin.')
        await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) })
      }
      setNotice('Fiyat güncellendi.'); await onChanged()
    } catch (error) { setNotice(error instanceof Error ? error.message : 'Fiyat güncellenemedi.') }
  }
  return <div className={`variant-quick-grid ${mode}`}>
    {mode !== 'price' && <form onSubmit={stock}><label>Stok<input name="stock" type="number" min="0" step="1" defaultValue={variant.onHand} /></label><button>Kaydet</button></form>}
    {mode !== 'stock' && <form onSubmit={price}><div className="inline-fields"><label>Liste fiyatı<input name="listPrice" type="number" min="0" step="0.01" defaultValue={variant.listPrice ?? 0} /></label><label>Satış fiyatı<input name="salePrice" type="number" min="0" step="0.01" defaultValue={variant.salePrice ?? 0} /></label></div><button>Kaydet</button></form>}
    {notice && <p className="notice" role="status">{notice}</p>}
  </div>
}

function ProductQuickEditModal({ products, connections, mode = 'both', onChanged, onClose }: { products: Product[]; connections: TrendyolConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown>; onClose: () => void }) {
  const title = mode === 'price' ? 'Hızlı fiyat güncelleme' : mode === 'stock' ? 'Hızlı stok güncelleme' : 'Hızlı fiyat ve stok düzenleme'
  const eyebrow = mode === 'price' ? 'HIZLI FİYAT GÜNCELLEME' : mode === 'stock' ? 'HIZLI STOK GÜNCELLEME' : 'TOPLU DÜZENLEME'
  const variants = products.flatMap(product => product.variants.map(variant => ({ product, variant })))
  const groups = variants.reduce<Record<string, typeof variants>>((result, item) => {
    const color = item.variant.optionSignature?.match(/(?:RENK|Renk|WEB COLOR|Web Color)\s*[:=]\s*([^|_]+)/)?.[1]?.trim() || 'Diğer'
    ;(result[color] ??= []).push(item)
    return result
  }, {})
  const optionValue = (signature: string, labels: string[], fallback: string) => {
    const labelPattern = labels.join('|')
    return signature.match(new RegExp(`(?:${labelPattern})\\s*[:=]\\s*([^|_]+)`, 'i'))?.[1]?.trim() || fallback
  }
  const colorOf = (item: typeof variants[number]) => optionValue(item.variant.optionSignature || '', ['RENK', 'WEB COLOR'], 'Diğer')
  const sizeOf = (item: typeof variants[number]) => optionValue(item.variant.optionSignature || '', ['BEDEN', 'SIZE'], item.variant.optionSignature || 'Ana varyant')
  const colorOptions = Object.keys(groups)
  const [selectionDraft, setSelectionDraft] = useState<string[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [selectionConfirmed, setSelectionConfirmed] = useState(false)
  const [selectedColors, setSelectedColors] = useState<string[]>([])
  const [selectedSizes, setSelectedSizes] = useState<string[]>([])
  const [listPrice, setListPrice] = useState(''); const [salePrice, setSalePrice] = useState(''); const [stockAmount, setStockAmount] = useState('')
  const [stockAction, setStockAction] = useState<'SET' | 'ADD' | 'SUBTRACT'>('SET'); const [notice, setNotice] = useState(''); const [saving, setSaving] = useState(false)
  const selectedSet = new Set(selectionDraft)
  const appliedSelectedSet = new Set(selected)
  const sizeOptions = [...new Set(variants.filter(item => !selectedColors.length || selectedColors.includes(colorOf(item))).map(sizeOf))]
  const toggleColor = (color: string) => {
    const next = selectedColors.includes(color) ? selectedColors.filter(item => item !== color) : [...selectedColors, color]
    setSelectedColors(next)
  }
  const toggleSize = (size: string) => {
    const next = selectedSizes.includes(size) ? selectedSizes.filter(item => item !== size) : [...selectedSizes, size]
    setSelectedSizes(next)
  }
  const toggle = (id: string) => { setSelectionConfirmed(false); setSelectionDraft(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]) }
  const toggleGroup = (items: typeof variants) => { const ids = items.map(item => item.variant.id); const every = ids.every(id => selectedSet.has(id)); setSelectionConfirmed(false); setSelectionDraft(current => every ? current.filter(id => !ids.includes(id)) : [...new Set([...current, ...ids])]) }
  function applyFilterSelection() {
    if (!selectedColors.length && !selectedSizes.length) return setNotice('Önce renk veya beden filtresi seçin.')
    const ids = variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && (!selectedSizes.length || selectedSizes.includes(sizeOf(item)))).map(item => item.variant.id)
    if (!ids.length) return setNotice('Seçtiğiniz filtrelerle eşleşen varyant bulunamadı.')
    setSelectionDraft(ids)
    setSelectionConfirmed(false)
    setNotice(`${ids.length} varyant alttaki listede otomatik işaretlendi.`)
  }
  function confirmSelection() {
    if (!selectionDraft.length) return setNotice('Önce en az bir varyant seçin.')
    setSelected(selectionDraft)
    setSelectionConfirmed(true)
    setNotice(`${selectionDraft.length} varyant fiyat güncellemesi için hazır.`)
  }
  async function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (saving || !selectionConfirmed || !selected.length) return setNotice('Önce seçtiğiniz varyantları onaylayın.')
    const priceRequested = mode !== 'stock' && (listPrice !== '' || salePrice !== ''); const stockRequested = mode !== 'price' && stockAmount !== ''
    if (!priceRequested && !stockRequested) return setNotice('Uygulanacak fiyat veya stok değerini girin.')
    const list = listPrice === '' ? null : Number(listPrice); const sale = salePrice === '' ? null : Number(salePrice); const amount = stockAmount === '' ? null : Number(stockAmount)
    if ((list != null && (!Number.isFinite(list) || list < 0)) || (sale != null && (!Number.isFinite(sale) || sale < 0)) || (list != null && sale != null && list < sale) || (amount != null && (!Number.isFinite(amount) || amount < 0))) return setNotice('Değerleri kontrol edin; negatif fiyat/stok veya hatalı fiyat sıralaması kullanılamaz.')
    setSaving(true); setNotice('Seçilen varyantlar güncelleniyor…')
    try {
      for (const item of variants.filter(value => appliedSelectedSet.has(value.variant.id))) {
        const variant = item.variant
        if (priceRequested) {
          const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''; const nextSale = sale ?? variant.salePrice ?? variant.listPrice ?? 0; const nextList = list ?? variant.listPrice ?? nextSale
          if (nextList < nextSale) throw new Error(`${variant.sku}: liste fiyatı satış fiyatından küçük olamaz.`)
          const body = { connectionId, variantId: variant.id, listPrice: nextList, salePrice: nextSale, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Toplu ürün fiyat düzenleme' }
          if (variant.offerId) { if (variant.offerVersion == null) throw new Error(`${variant.sku}: fiyat sürümü eksik.`); await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${variant.offerVersion}"` }, body: JSON.stringify(body) }) }
          else { if (!connectionId) throw new Error('İlk fiyat için aktif platform bağlantısı bulunamadı.'); await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }) }
        }
        if (stockRequested && amount != null) {
          const target = stockAction === 'SET' ? amount : stockAction === 'ADD' ? variant.onHand + amount : variant.onHand - amount
          if (target < 0) throw new Error(`${variant.sku}: stok sıfırın altına inemez.`)
          const delta = target - variant.onHand
          if (delta !== 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Toplu ürün stok düzenleme', sourceEventId: key() }) })
        }
      }
      setNotice(`${selected.length} varyant güncellendi.`); await onChanged()
    } catch (error) { setNotice(error instanceof Error ? error.message : 'Toplu düzenleme tamamlanamadı.') } finally { setSaving(false) }
  }
  return <div className="workspace-modal-backdrop product-quick-edit-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal product-quick-edit-modal" role="dialog" aria-modal="true" aria-label={title} onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">{eyebrow}</p><h2>{title}</h2><p>Önce varyantları onaylayın, ardından fiyat veya stok değerini tek seferde uygulayın.</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button>
      </header>
      <form onSubmit={apply}>
        <details className="quick-edit-step" open>
          <summary><span><b>1</b> Varyantları seç</span><small>{selectionDraft.length ? `${selectionDraft.length} varyant işaretlendi` : 'Önce renk, sonra beden seçin'}</small><i>⌄</i></summary>
          <div className="quick-edit-filter-row">
            <details className="quick-edit-filter">
              <summary><span>Renk</span><small>{selectedColors.length ? `${selectedColors.length} renk seçildi` : 'Renk seçin'}</small><i>⌄</i></summary>
              <div className="quick-edit-filter-options">{colorOptions.map(color => <label key={color}><input type="checkbox" checked={selectedColors.includes(color)} onChange={() => toggleColor(color)} /><span>{color}</span><small>{groups[color].length} varyant</small></label>)}</div>
            </details>
            <details className="quick-edit-filter">
              <summary><span>Beden</span><small>{selectedSizes.length ? `${selectedSizes.length} beden seçildi` : 'Beden seçin'}</small><i>⌄</i></summary>
              <div className="quick-edit-filter-options">{sizeOptions.map(size => <label key={size}><input type="checkbox" checked={selectedSizes.includes(size)} onChange={() => toggleSize(size)} /><span>{size}</span><small>{variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && sizeOf(item) === size).length} varyant</small></label>)}</div>
            </details>
          </div>
          <div className="quick-edit-filter-action"><p>Örneğin Siyah rengini ve 3XL + 4XL + 5XL bedenlerini seçip alttaki varyantları işaretleyin.</p><button type="button" onClick={applyFilterSelection} disabled={!selectedColors.length && !selectedSizes.length}>Seç</button></div>
          <div className="quick-edit-selection">
            {Object.entries(groups).map(([color, items]) => <details className="quick-edit-color" key={color} open={Object.keys(groups).length === 1}>
              <summary><label onClick={event => event.stopPropagation()}><input type="checkbox" checked={items.every(item => selectedSet.has(item.variant.id))} onChange={() => toggleGroup(items)} /> {color}</label><small>{items.length} varyant · {items.reduce((sum, item) => sum + item.variant.available, 0)} stok</small><b>⌄</b></summary>
              <div className="quick-edit-variants">{items.map(item => <label className="quick-edit-variant" key={item.variant.id}><input type="checkbox" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} /><span><strong>{item.variant.optionSignature || item.product.title}</strong><small>{item.product.title} · Stok kodu: {item.variant.sku}</small></span><em>{item.variant.available} stok</em></label>)}</div>
            </details>)}
          </div>
          <div className="quick-edit-step-action"><p>{selectionConfirmed ? `${selected.length} varyant onaylandı. Seçim değişirse yeniden onaylayın.` : `${selectionDraft.length} varyant seçili. Fiyat düzenlemesine aktarmak için onaylayın.`}</p><button type="button" onClick={confirmSelection}>Seçimi onayla</button></div>
        </details>
        <details className="quick-edit-step quick-edit-pricing-step" open={selectionConfirmed}>
          <summary><span><b>2</b> {mode === 'stock' ? 'Stok değerini düzenle' : mode === 'price' ? 'Fiyatı düzenle' : 'Fiyat ve stok değerini düzenle'}</span><small>{selectionConfirmed ? `${selected.length} onaylı varyanta uygulanacak` : 'Varyant seçimi bekleniyor'}</small><i>⌄</i></summary>
          {selectionConfirmed ? <div className="quick-edit-step-body">
            <div className="quick-edit-selected-list">{variants.filter(item => appliedSelectedSet.has(item.variant.id)).map(item => <span key={item.variant.id}>{item.variant.optionSignature || item.variant.sku}</span>)}</div>
            <div className="quick-edit-fields">
              {mode !== 'stock' && <fieldset><legend>Fiyat</legend><label>Liste fiyatı<input type="number" min="0" step="0.01" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></fieldset>}
              {mode !== 'price' && <fieldset><legend>Stok</legend><label>İşlem<select value={stockAction} onChange={event => setStockAction(event.target.value as typeof stockAction)}><option value="SET">Bu sayıya eşitle</option><option value="ADD">Bu kadar ekle (+)</option><option value="SUBTRACT">Bu kadar çıkar (−)</option></select></label><label>Miktar<input type="number" min="0" step="1" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></fieldset>}
            </div>
          </div> : <p className="quick-edit-step-empty">Fiyat alanlarını açmak için önce varyant seçimini onaylayın.</p>}
        </details>
        {notice && <p className="notice" role="status">{notice}</p>}
        <footer className="quick-edit-footer"><span>{selectionConfirmed ? `${selected.length} varyant seçildi` : 'Varyant seçimi bekleniyor'}</span><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="submit" disabled={saving || !selectionConfirmed}>{saving ? 'Uygulanıyor…' : 'Seçilenlere uygula'}</button></footer>
      </form>
    </section>
  </div>
}

function InlineVariantInputs({ variant, connections, onChanged }: { variant: Variant; connections: TrendyolConnection[]; onChanged: () => Promise<unknown> }) {
  const [price, setPrice] = useState(variant.salePrice ?? variant.listPrice ?? '')
  const [stock, setStock] = useState(variant.onHand)
  const [savingPrice, setSavingPrice] = useState(false); const [savingStock, setSavingStock] = useState(false)
  async function savePrice() {
    const salePrice = Number(price); if (savingPrice || price === '' || !Number.isFinite(salePrice) || salePrice < 0 || salePrice === (variant.salePrice ?? variant.listPrice ?? 0)) return
    const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''
    if (!variant.offerId && !connectionId) return
    const body = { connectionId, variantId: variant.id, listPrice: Math.max(variant.listPrice ?? salePrice, salePrice), salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Varyant satırı fiyat düzenleme' }
    setSavingPrice(true)
    try { if (variant.offerId) { if (variant.offerVersion == null) return; await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${variant.offerVersion}"` }, body: JSON.stringify(body) }) } else await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); await onChanged() } finally { setSavingPrice(false) }
  }
  async function saveStock() {
    const target = Number(stock); const delta = target - variant.onHand; if (savingStock || !Number.isFinite(target) || target < 0 || delta === 0) return
    setSavingStock(true)
    try { await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Varyant satırı stok düzenleme', sourceEventId: key() }) }); await onChanged() } finally { setSavingStock(false) }
  }
  return <><input className="variant-inline-input" aria-label={`${variant.sku} fiyat`} value={price} onChange={event => setPrice(event.target.value === '' ? '' : Number(event.target.value))} onBlur={() => void savePrice()} type="number" min="0" step="0.01" disabled={savingPrice} /><input className="variant-inline-input" aria-label={`${variant.sku} stok`} value={stock} onChange={event => setStock(Number(event.target.value || 0))} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} /></>
}

function ProductColorRows({ product, selected, onSelect, onQuickEdit }: { product: Product; selected: boolean; onSelect: () => void; onQuickEdit: (mode: QuickEditMode) => void }) {
  const platformActive = Boolean(product.activePlatforms?.length)
  return <article className="product-catalog-item color-variant-item">
      <div className="product-catalog-row">
        <input className="product-row-select" type="checkbox" aria-label={`${product.title} seç`} checked={selected} onChange={onSelect} />
        {product.primaryImageUrl ? <img src={product.primaryImageUrl} alt={product.title} /> : <span className="product-list-placeholder">Görsel yok</span>}
        <div className="product-list-identity"><strong>{product.title}</strong><small>Model Kodu: {product.modelCode ?? '—'}</small></div>
        <strong className="product-list-variants">{product.variants.length} varyant</strong>
        <div className="product-list-price"><strong>{money(product.startingPrice, product.currency)}</strong><button type="button" className="product-quick-link" onClick={() => onQuickEdit('price')}>Fiyatı güncelle</button></div>
        <div className="product-list-stock"><strong>{product.totalStock}</strong><button type="button" className="product-quick-link" onClick={() => onQuickEdit('stock')}>Stokları güncelle</button></div>
        <div className="product-list-platforms"><span className={`platform-state-icon${platformActive ? ' active' : ''}`} title={platformActive ? 'Platformla eşleşti' : 'Platformla eşleşmedi'}>TY<i /></span><small>{platformActive ? 'Eşleşti' : 'Eşleşmedi'}</small></div>
        <div className="product-list-status"><Tag>{product.status}</Tag><small>{product.status === 'ACTIVE' ? 'Satışa açık' : product.status === 'DRAFT' ? 'Taslak ürün' : 'Arşivlenmiş'}</small></div>
        <div className="product-list-actions"><Link className="product-edit-link" to={`/products/${product.id}`}>Düzenle</Link></div>
      </div>
    </article>
}

export function ProductsPage() {
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [status, setStatus] = useState(''); const [platform, setPlatform] = useState(''); const [stock, setStock] = useState(''); const [expandedProductId, setExpandedProductId] = useState<string | null>(null); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null)
  const query = useQuery({ queryKey: ['products'], queryFn: () => hubApi<PageData<Product>>('/products?limit=200') })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const products = query.data?.items ?? []; const connections = (connectionsQuery.data?.items ?? []).filter(item => item.status === 'ACTIVE')
  const platforms = useMemo(() => [...new Set(products.flatMap(product => product.activePlatforms ?? []))].sort(), [products])
  const normalized = search.trim().toLocaleLowerCase('tr-TR')
  const visible = products.filter(product => {
    const searchMatch = !normalized || [product.title, product.modelCode ?? '', ...product.variants.flatMap(variant => [variant.sku, variant.barcode ?? ''])].some(value => value.toLocaleLowerCase('tr-TR').includes(normalized))
    const statusMatch = !status || product.status === status; const platformMatch = !platform || product.activePlatforms?.includes(platform)
    const stockMatch = !stock || (stock === 'OUT' ? product.totalStock <= 0 : stock === 'LOW' ? product.totalStock > 0 && product.totalStock <= 5 : product.totalStock > 5)
    return searchMatch && statusMatch && platformMatch && stockMatch
  })
  const selectedProducts = products.filter(product => quickEdit?.productIds.includes(product.id))
  const quickProductIds: string[] | null = null; const setQuickProductIds = (productIds: string[] | null) => setQuickEdit(productIds ? { productIds, mode: 'both' } : null)
  const allVisibleSelected = visible.length > 0 && visible.every(product => selectedProductIds.includes(product.id))
  const refresh = () => client.invalidateQueries({ queryKey: ['products'] })
  function toggleProduct(id: string) { setSelectedProductIds(ids => ids.includes(id) ? ids.filter(item => item !== id) : [...ids, id]) }
  function toggleAllVisible() { setSelectedProductIds(ids => allVisibleSelected ? ids.filter(id => !visible.some(product => product.id === id)) : [...new Set([...ids, ...visible.map(product => product.id)])]) }
  return <Page title="Ürünler" eyebrow="Katalog" action={<Link className="button-link" to="/products/new">+ Yeni Ürün Ekle</Link>}>
    <p className="lede page-lede">Ürün, varyant, stok, fiyat ve pazaryeri yayın durumlarını tek kartta yönetin.</p>
    <div className="product-metrics metrics"><article><small>Toplam ürün</small><strong>{products.length}</strong><span>katalog kaydı</span></article><article><small>Aktif</small><strong>{products.filter(x => x.status === 'ACTIVE').length}</strong><span>ürün</span></article><article><small>Stoksuz</small><strong>{products.filter(x => x.totalStock <= 0).length}</strong><span>aksiyon gerekli</span></article><article><small>Düşük stok</small><strong>{products.filter(x => x.totalStock > 0 && x.totalStock <= 5).length}</strong><span>5 ve altı</span></article></div>
    <div className="product-toolbar"><label className="order-search"><span aria-hidden="true">⌕</span><input aria-label="Ürün ara" placeholder="Ürün adı, model, SKU veya barkod..." value={search} onChange={event => setSearch(event.target.value)} /></label><select aria-label="Ürün durumu" value={status} onChange={event => setStatus(event.target.value)}><option value="">Tüm durumlar</option><option value="ACTIVE">Aktif</option><option value="DRAFT">Taslak</option><option value="ARCHIVED">Arşiv</option></select><select aria-label="Platform filtresi" value={platform} onChange={event => setPlatform(event.target.value)}><option value="">Tüm platformlar</option>{platforms.map(item => <option key={item}>{item}</option>)}</select><select aria-label="Stok filtresi" value={stock} onChange={event => setStock(event.target.value)}><option value="">Tüm stoklar</option><option value="OUT">Stoksuz</option><option value="LOW">Düşük stok</option><option value="OK">Yeterli stok</option></select></div>
    {!query.isLoading && visible.length > 0 && <><div className="product-bulk-bar preferred-product-bulk"><label><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} /> Tümünü seç</label><span>{selectedProductIds.length ? `${selectedProductIds.length} ürün seçildi` : 'Toplu işlem için ürün seçin'}</span><button type="button" disabled={!selectedProductIds.length} onClick={() => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })}>Toplu fiyat ve stok düzenle</button></div><div className="product-catalog-table preferred-product-catalog"><div className="product-catalog-head"><label className="product-select-all"><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} /><span>Ürün Bilgisi</span></label><span>Varyant</span><span>Fiyat</span><span>Stok</span><span>Platform Durumu</span><span>Durum</span><span>İşlem</span></div>{visible.map(product => <ProductColorRows key={product.id} product={product} selected={selectedProductIds.includes(product.id)} onSelect={() => toggleProduct(product.id)} onQuickEdit={mode => setQuickEdit({ productIds: [product.id], mode })} />)}</div></>}
    {quickEdit && <ProductQuickEditModal products={selectedProducts} connections={connections} mode={quickEdit.mode} onChanged={refresh} onClose={() => setQuickEdit(null)} />}
    <ErrorBox error={query.error ?? connectionsQuery.error} />{query.isLoading ? <p>Yükleniyor…</p> : !visible.length ? <div className="empty">Filtrelerle eşleşen ürün yok.</div> : <><div className="product-bulk-bar"><label><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} /> Tümünü seç</label><span>{selectedProductIds.length ? `${selectedProductIds.length} ürün seçildi` : 'Toplu işlem için ürün seçin'}</span><button type="button" disabled={!selectedProductIds.length} onClick={() => setQuickProductIds(selectedProductIds)}>Toplu fiyat ve stok düzenle</button></div><div className="product-catalog-table"><div className="product-catalog-head"><label className="product-select-all"><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} /><span>Ürün</span></label><span>Fiyat</span><span>Stok</span><span>Platform Durumu</span><span>Durum</span><span>İşlem</span></div>{visible.map(product => { const expanded = expandedProductId === product.id; const platformActive = Boolean(product.activePlatforms?.length); const colorGroups = product.variants.reduce<Record<string, Variant[]>>((groups, variant) => { const match = variant.optionSignature?.match(/(?:RENK|Renk|WEB COLOR|Web Color)\s*[:=]\s*([^|_]+)/); const color = match?.[1]?.trim() || 'Diğer'; (groups[color] ??= []).push(variant); return groups }, {}); return <article className={`product-catalog-item${expanded ? ' expanded' : ''}`} key={product.id}><div className="product-catalog-row"><input className="product-row-select" type="checkbox" aria-label={`${product.title} seç`} checked={selectedProductIds.includes(product.id)} onChange={() => toggleProduct(product.id)} /><button type="button" className="product-expand-button" aria-label={`${product.title} varyantlarını ${expanded ? 'kapat' : 'aç'}`} aria-expanded={expanded} onClick={() => setExpandedProductId(expanded ? null : product.id)}>{expanded ? '⌃' : '⌄'}</button>{product.primaryImageUrl ? <img src={product.primaryImageUrl} alt={product.title} /> : <span className="product-list-placeholder">Görsel yok</span>}<div className="product-list-identity"><strong>{product.title}</strong><small>Model Kodu: {product.modelCode ?? '—'}</small><span>{product.variants.length} varyant</span></div><strong className="product-list-price">{money(product.startingPrice, product.currency)}</strong><strong>{product.totalStock}</strong><div className="product-list-platforms"><span className={`platform-state-icon${platformActive ? ' active' : ''}`} title={platformActive ? 'Platformla eşleşti' : 'Platformla eşleşmedi'}>TY<i /></span><small>{platformActive ? 'Eşleşti' : 'Eşleşmedi'}</small></div><div className="product-list-status"><Tag>{product.status}</Tag><small>{product.status === 'ACTIVE' ? 'Satışa açık' : product.status === 'DRAFT' ? 'Taslak ürün' : 'Arşivlenmiş'}</small></div><div className="product-list-actions"><button type="button" onClick={() => setQuickProductIds([product.id])}>Hızlı düzenle</button><Link className="product-edit-link" to={`/products/${product.id}`}>Düzenle</Link></div></div>{expanded && <div className="product-color-groups" aria-label={`${product.title} renk grupları`}>{Object.entries(colorGroups).map(([color, variants]) => <details className="product-color-group" key={color} open={Object.keys(colorGroups).length === 1}><summary><span className={`color-swatch color-${color.toLocaleLowerCase('tr-TR').replaceAll(' ', '-')}`} /><strong>{color}</strong><small>{variants.length} varyant</small><b>⌄</b></summary><div className="product-variant-table" role="table" aria-label={`${color} varyantları`}><div className="product-variant-head" role="row"><span>Varyant bilgisi</span><span>Seçenek</span><span>Durum</span><span>Model kodu</span><span>Fiyat</span><span>Stok</span></div>{variants.map(variant => <div className="product-variant-row" role="row" key={variant.id}><div><strong>Stok Kodu: {variant.sku}</strong><small>Barkod: {variant.barcode ?? '—'}</small></div><strong>{variant.optionSignature ? variant.optionSignature.replaceAll('_', ' · ').replaceAll('|', ' · ').replaceAll('=', ': ') : 'Ana varyant'}</strong><span className={`variant-sale-state ${variant.status === 'ACTIVE' ? 'active' : ''}`}>{variant.status === 'ACTIVE' ? '● Satışta' : variant.status === 'DRAFT' ? 'Taslak' : variant.status}</span><span>{variant.modelCode ?? product.modelCode ?? '—'}</span><strong>{money(variant.salePrice ?? variant.listPrice, variant.currency ?? product.currency)}</strong><strong>{variant.available}</strong></div>)}</div></details>)}</div>}</article> })}</div></>}{quickProductIds && <ProductQuickEditModal products={selectedProducts} connections={connections} onChanged={refresh} onClose={() => setQuickProductIds(null)} />}
  </Page>
}

type CategoryRequirement = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; attribute: Attribute }
type VariantDraft = {
  key: string
  optionSignature: string
  options: Record<string, string>
  attributeValueIds: Record<string, string>
  sku: string
  barcode: string
  stock: number
  salePrice: number
  listPrice: number
}
type ProductAttributePayload = { attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
const MAX_VARIANTS = 1000

function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const editor = useRef<HTMLTextAreaElement>(null)
  function wrap(open: string, close = open) { const field = editor.current; if (!field) return; const start = field.selectionStart; const end = field.selectionEnd; const selected = value.slice(start, end) || 'metin'; const next = `${value.slice(0, start)}${open}${selected}${close}${value.slice(end)}`; onChange(next); window.setTimeout(() => { field.focus(); field.setSelectionRange(start + open.length, start + open.length + selected.length) }) }
  return <div className="rich-text-editor rich-text-editor-pro"><div className="rich-text-editor-head"><strong>Ürün Açıklaması</strong><span>{value.replace(/<[^>]*>/g, '').trim().length} karakter</span></div><div className="rich-text-toolbar" aria-label="Açıklama biçimlendirme araçları"><div className="rich-text-tool-group"><button type="button" title="Kalın" onClick={() => wrap('<strong>', '</strong>')}><b>B</b></button><button type="button" title="İtalik" onClick={() => wrap('<em>', '</em>')}><i>I</i></button><button type="button" title="Altı çizili" onClick={() => wrap('<u>', '</u>')}><u>U</u></button></div><div className="rich-text-tool-group"><button type="button" title="Sola hizala" onClick={() => wrap('<p style="text-align:left">', '</p>')}>≡</button><button type="button" title="Ortala" onClick={() => wrap('<p style="text-align:center">', '</p>')}>≣</button><button type="button" title="Sağa hizala" onClick={() => wrap('<p style="text-align:right">', '</p>')}>≡</button></div><div className="rich-text-tool-group"><select aria-label="Yazı boyutu" defaultValue=""><option value="" disabled>Yazı Boyutu</option><option>Küçük</option><option>Normal</option><option>Büyük</option></select><button type="button" title="Madde listesi" onClick={() => wrap('<ul><li>', '</li></ul>')}>☷</button><button type="button" title="Paragraf" onClick={() => wrap('<p>', '</p>')}>¶</button><button type="button" title="Metin rengi" onClick={() => wrap('<span style="color:#0e5752">', '</span>')}>A</button><button type="button" title="Bağlantı" onClick={() => wrap('<a href="https://">', '</a>')}>🔗</button><button type="button" title="Görsel" onClick={() => wrap('<img src="', '" alt="Ürün görseli" />')}>▣</button></div><div className="rich-text-tool-group"><button type="button" title="Geri al" onClick={() => document.execCommand('undo')}>↶</button><button type="button" title="Yinele" onClick={() => document.execCommand('redo')}>↷</button><button type="button" title="Biçimi temizle" onClick={() => onChange(value.replace(/<[^>]*>/g, ''))}>⌫</button></div></div><textarea ref={editor} value={value} onChange={event => onChange(event.target.value)} required aria-label="Açıklama" placeholder="Ürünün öne çıkan özelliklerini anlatın…" /><details><summary>◉ HTML Göster</summary><iframe className="rich-text-preview" sandbox="" title="Açıklama HTML ön izlemesi" srcDoc={value} /></details></div>
}

function buildVariantMatrix(requirements: CategoryRequirement[], variantAttributeIds: string[], selectedValueIds: Record<string, string[]>, baseSku: string, fallbackListPrice: number, fallbackSalePrice: number, initialStock: number) {
  const axes = variantAttributeIds.map(attributeId => {
    const requirement = requirements.find(item => item.attributeId === attributeId)
    const selected = new Set(selectedValueIds[attributeId] ?? [])
    return { requirement, values: requirement?.attribute.values.filter(value => selected.has(value.id)) ?? [] }
  }).filter(axis => axis.requirement && axis.values.length)
  if (!axes.length) return [] as VariantDraft[]
  const count = axes.reduce((total, axis) => total * axis.values.length, 1)
  if (count > MAX_VARIANTS) throw new Error(`En fazla ${MAX_VARIANTS} varyant oluşturabilirsiniz. Seçili kombinasyon sayısı: ${count}.`)
  const combinations = axes.reduce<Array<{ options: Record<string, string>; attributeValueIds: Record<string, string> }>>((carry, axis) => {
    if (!carry.length) return axis.values.map(value => ({ options: { [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { [axis.requirement!.attributeId]: value.id } }))
    return carry.flatMap(entry => axis.values.map(value => ({ options: { ...entry.options, [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { ...entry.attributeValueIds, [axis.requirement!.attributeId]: value.id } })))
  }, [])
  const prefix = (baseSku || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR')
  return combinations.map((entry, index) => ({
    key: crypto.randomUUID(),
    optionSignature: Object.entries(entry.options).map(([name, value]) => `${name}:${value}`).join('_'),
    options: entry.options,
    attributeValueIds: entry.attributeValueIds,
    sku: `${prefix}-${index + 1}`,
    barcode: '',
    stock: initialStock,
    salePrice: fallbackSalePrice,
    listPrice: fallbackListPrice || fallbackSalePrice
  }))
}

function productAttributePayload(requirement: CategoryRequirement, selectedIds: string[], typedValue: string, sortOrder: number): ProductAttributePayload[] {
  if (selectedIds.length) return selectedIds.map((valueId, index) => ({ attributeId: requirement.attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: sortOrder * 100 + index }))
  const typed = typedValue.trim()
  if (!typed) return []
  if (requirement.attribute.dataType === 'NUMBER') {
    const value = Number(typed)
    if (!Number.isFinite(value)) throw new Error(`${requirement.attribute.name} sayısal olmalıdır.`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: value, booleanValue: null, sortOrder }]
  }
  if (requirement.attribute.dataType === 'BOOLEAN') {
    const normalized = typed.toLocaleLowerCase('tr-TR')
    if (!['true', 'false', 'evet', 'hayır', 'hayir', '1', '0'].includes(normalized)) throw new Error(`${requirement.attribute.name} için evet veya hayır seçin.`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: null, booleanValue: ['true', 'evet', '1'].includes(normalized), sortOrder }]
  }
  return [{ attributeId: requirement.attributeId, valueId: null, textValue: typed, numberValue: null, booleanValue: null, sortOrder }]
}

export function NewProductPage({ editProductId }: { editProductId?: string } = {}) {
  const [error, setError] = useState<unknown>(); const [created, setCreated] = useState<Product>(); const [notice, setNotice] = useState(''); const [submitting, setSubmitting] = useState(false); const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', listPrice: '699.90', salePrice: '549.90', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '2', mediaUrls: '' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([])
  const [bulkStock, setBulkStock] = useState(''); const [bulkSalePrice, setBulkSalePrice] = useState(''); const [bulkListPrice, setBulkListPrice] = useState('')
  const [mediaFiles, setMediaFiles] = useState<File[]>([])
  const productToEdit = useQuery({ queryKey: ['product', editProductId], queryFn: () => hubApi<Product>(`/products/${editProductId}`), enabled: !!editProductId })
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => hubApi<PageData<Category>>('/catalog/categories?limit=200') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => hubApi<PageData<Brand>>('/catalog/brands?limit=200') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const activeConnections = (connections.data?.items ?? []).filter(item => item.status === 'ACTIVE' && item.platformCode === 'TRENDYOL')
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const initialStock = Number(form.initialStock || 0)
  const desi = useMemo(() => { const width = Number(form.width); const length = Number(form.length); const height = Number(form.height); return width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0 }, [form.width, form.length, form.height])
  const mediaUrls = useMemo(() => form.mediaUrls.split(/\r?\n/).map(item => item.trim()).filter(Boolean), [form.mediaUrls])

  useEffect(() => {
    const product = productToEdit.data
    if (!product) return
    const primary = product.variants[0]
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: primary?.sku ?? '', barcode: primary?.barcode ?? '', modelCode: primary?.modelCode ?? product.modelCode ?? '', weight: String(primary?.weight ?? ''), width: String(primary?.width ?? ''), length: String(primary?.length ?? ''), height: String(primary?.height ?? ''), desi: String(primary?.desi ?? 1), listPrice: String(primary?.listPrice ?? primary?.salePrice ?? 0), salePrice: String(primary?.salePrice ?? 0), currency: primary?.currency ?? 'TRY', vatRate: String(primary?.vatRate ?? 10), vatIncluded: primary?.vatInclusion ?? 'INCLUDED', initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: product.primaryImageUrl ?? '' })
    setVariantRows(product.variants.map(variant => ({ key: variant.id, optionSignature: variant.optionSignature || 'Tek Ürün', options: {}, attributeValueIds: {}, sku: variant.sku, barcode: variant.barcode ?? '', stock: variant.onHand, salePrice: variant.salePrice ?? 0, listPrice: variant.listPrice ?? variant.salePrice ?? 0 })))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed)
  }, [productToEdit.data?.id, productToEdit.data?.version])

  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = requirements.data?.find(item => item.attributeId === attributeId)
    if (variantAttributeIds.includes(attributeId)) setVariantRows([])
    setAttributeSelections(current => {
      const values = current[attributeId] ?? []
      if (values.includes(valueId)) return { ...current, [attributeId]: values.filter(item => item !== valueId) }
      const variantAxis = variantAttributeIds.includes(attributeId)
      const singleProductValue = requirement?.attribute.dataType === 'SINGLE_SELECT' && !variantAxis
      return { ...current, [attributeId]: singleProductValue ? [valueId] : [...values, valueId] }
    })
  }
  function toggleVariantAttribute(attributeId: string) {
    setVariantAttributeIds(current => current.includes(attributeId) ? current.filter(item => item !== attributeId) : [...current, attributeId])
    setVariantRows([])
  }
  function generateVariants() {
    try {
      const generated = buildVariantMatrix(requirements.data ?? [], variantAttributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, initialStock)
      setVariantRows(current => {
        const existingSignatures = new Set(current.map(row => row.optionSignature))
        return [...current, ...generated.filter(row => !existingSignatures.has(row.optionSignature))]
      })
      setNotice(generated.length ? `${generated.length} varyant satırı hazırlandı.` : 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.')
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.') }
  }
  function clearVariants() { setVariantRows([]); setNotice('Oluşan varyant satırları temizlendi.') }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'stock' || field === 'salePrice' || field === 'listPrice' ? Number(value || 0) : value })) }
  function swapVariants(sourceKey: string, targetKey: string) {
    if (sourceKey === targetKey) return
    setVariantRows(rows => {
      const sourceIndex = rows.findIndex(row => row.key === sourceKey); const targetIndex = rows.findIndex(row => row.key === targetKey)
      if (sourceIndex < 0 || targetIndex < 0) return rows
      const next = [...rows]; [next[sourceIndex], next[targetIndex]] = [next[targetIndex], next[sourceIndex]]
      return next
    })
  }
  function updateChannel(id: string) { setSelectedChannelIds(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id]) }
  function applyBulk() {
    const stock = bulkStock === '' ? null : Number(bulkStock); const sale = bulkSalePrice === '' ? null : Number(bulkSalePrice); const list = bulkListPrice === '' ? null : Number(bulkListPrice)
    setVariantRows(rows => rows.map(row => ({ ...row, stock: stock == null || !Number.isFinite(stock) ? row.stock : stock, salePrice: sale == null || !Number.isFinite(sale) ? row.salePrice : sale, listPrice: list == null || !Number.isFinite(list) ? row.listPrice : list })))
    setNotice('Toplu stok ve fiyat değerleri varyantlara uygulandı.')
  }

  function rowsForSubmit() {
    if (variantAttributeIds.length && !variantRows.length) throw new Error('Varyant özellikleri seçili. Önce “Ürünleri ekle” ile varyantları oluşturun.')
    return variantRows.length ? variantRows : [{ key: crypto.randomUUID(), optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: (form.baseSku || form.modelCode || form.title || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR'), barcode: form.barcode, stock: initialStock, salePrice: fallbackSalePrice, listPrice: fallbackListPrice }]
  }
  function validate(rows: VariantDraft[]) {
    const issues: string[] = []; const requirementList = requirements.data ?? []
    if (!form.title.trim()) issues.push('Ürün adı zorunludur.'); if (!form.description.trim()) issues.push('Açıklama zorunludur.'); if (!form.categoryId) issues.push('Panel kategorisi zorunludur.')
    for (const requirement of requirementList) {
      const selectedCount = attributeSelections[requirement.attributeId]?.length ?? 0
      if (!variantAttributeIds.includes(requirement.attributeId) && requirement.attribute.dataType === 'SINGLE_SELECT' && selectedCount > 1) issues.push(`${requirement.attribute.name} yalnız bir ürün değeri kabul eder.`)
      if (!requirement.isRequired) continue
      if (variantAttributeIds.includes(requirement.attributeId)) {
        if (rows.some(row => !row.attributeValueIds[requirement.attributeId])) issues.push(`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`)
      } else if (!(attributeSelections[requirement.attributeId]?.length) && !(attributeTextValues[requirement.attributeId] ?? '').trim()) issues.push(`${requirement.attribute.name} zorunludur.`)
    }
    if (rows.length > MAX_VARIANTS) issues.push(`En fazla ${MAX_VARIANTS} varyant oluşturulabilir.`)
    const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
    const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
    const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
    if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
    if (selectedChannelIds.length) {
      if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
      if (!mediaUrls.length && !mediaFiles.length) issues.push('Trendyol yayını için en az bir görsel zorunludur.'); if (mediaUrls.length + mediaFiles.length > 8) issues.push('Trendyol yayını için en fazla 8 görsel kullanılabilir.'); if (mediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
      if (rows.some(row => !row.barcode.trim() || !/^[a-zA-Z0-9._-]+$/.test(row.barcode.trim()))) issues.push('Trendyol yayını için her varyantta geçerli ve benzersiz barkod zorunludur.'); if (rows.some(row => row.salePrice <= 0)) issues.push('Trendyol yayını için satış fiyatı sıfırdan büyük olmalıdır.')
    }
    if (issues.length) throw new Error(issues.join(' '))
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(undefined); setNotice(''); setSubmitting(true); let productCreated: Product | undefined
    try {
      const requirementList = requirements.data ?? []; const rows = rowsForSubmit(); validate(rows)
      const globalAttributes = requirementList.filter(item => !variantAttributeIds.includes(item.attributeId)).flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
      const variantPayload = (row: VariantDraft, index: number) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : Number(form.desi) || 1, options: row.options, attributes: Object.entries(row.attributeValueIds).map(([attributeId, valueId], attributeIndex) => ({ attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: index * 100 + attributeIndex })) })
      const existingVariantIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
      const product = productToEdit.data
        ? await hubApi<Product>(`/products/${productToEdit.data.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${productToEdit.data.version}"` }, body: JSON.stringify({ title: form.title, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes: globalAttributes, variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload), variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({ id: row.key, sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null })) }) })
        : await hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ title: form.title, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes: globalAttributes, variants: rows.map(variantPayload) }) })
      productCreated = product; setCreated(product); const completed = ['ürün']; const warnings: string[] = []
      for (const [index, url] of mediaUrls.entries()) await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: null, url, mediaRole: index === 0 ? 'PRIMARY' : 'GALLERY', sortOrder: index, altText: form.title }) })
      for (const [fileIndex, file] of mediaFiles.entries()) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
      if (mediaUrls.length || mediaFiles.length) completed.push('görseller')
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        const currentStock = productToEdit.data?.variants.find(item => item.id === variant.id)?.onHand ?? 0
        const stockDelta = row.stock - currentStock
        if (stockDelta !== 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: stockDelta, reason: productToEdit.data ? 'Ürün düzenleme stoğu' : 'İlk ürün stoğu', sourceEventId: key() }) })
        for (const connectionId of selectedChannelIds) await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId, variantId: variant.id, listPrice: row.listPrice, salePrice: row.salePrice, currency: form.currency || 'TRY', vatRate: Number(form.vatRate || 0), vatInclusion: form.vatIncluded, roundingMode: 'HALF_EVEN', safetyStock: Number(form.safetyStock || 0), status: 'ACTIVE', reason: 'İlk ürün fiyatı' }) })
      }
      if (rows.some(row => row.stock > 0)) completed.push('stok'); if (selectedChannelIds.length) completed.push('kanal fiyatları')
      for (const connectionId of selectedChannelIds) {
        try {
          await hubApi(`/products/${product.id}/listing-profiles/${connectionId}`, { method: 'PUT', body: JSON.stringify({ titleOverride: null, descriptionOverride: null, externalCategoryId: null, externalBrandId: null, deliveryTimeDays: null, enabled: true }) })
          const jobId = await hubApi<string>(`/products/${product.id}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId }) })
          completed.push(`yayın işi ${jobId}`)
        } catch (reason) { warnings.push(reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.') }
      }
      setNotice(`${completed.join(', ')} kaydedildi.${warnings.length ? ` Yayın uyarısı: ${warnings.join(' ')}` : ''}`)
    } catch (reason) { setError(reason); if (productCreated) setNotice('Ürün oluşturuldu; sonraki stok, fiyat, görsel veya yayın adımlarından biri tamamlanamadı. Ürün detayından devam edin.') } finally { setSubmitting(false) }
  }

  return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><p className="lede page-lede">Kategori özellikleri, varyant kombinasyonları, stok, fiyat ve Trendyol yayın kuyruğu tek ürün çalışma alanında yönetilir.</p><form className="product-creation-workspace" onSubmit={submit}>
    <section className="panel product-step-card"><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün kartının temel başlığı ve katalog bilgileri.</p></div></div><div className="product-step-grid product-basics-grid"><label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label><label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label>Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); setVariantRows([]) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label><label>Model kodu<input value={form.modelCode} onChange={event => updateField('modelCode', event.target.value)} /></label><label>Stok Kodu<input value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder="RAV-BLUZ" /></label><label>Barkod<input value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder="Varyantsız üründe kullanılır" /></label><label className="desi-input-field">Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label><label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label></div></section>{desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><section className="workspace-modal desi-calculator-modal" role="dialog" aria-modal="true" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi hesapla</h2><p>En × Boy × Yükseklik / 3000 formülü kullanılır.</p></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat">×</button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></section></div>}

    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card"><div className="editor-section-title"><span>2</span><div><h2>Fiyat, stok ve vergi</h2><p>Merkezi başlangıç değerleri varyant oluşturulurken satırlara uygulanır.</p></div></div><div className="product-step-grid"><label>Liste fiyatı<input value={form.listPrice} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Satış fiyatı<input value={form.salePrice} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Para birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label><label>KDV oranı<select value={form.vatRate} onChange={event => updateField('vatRate', event.target.value)}><option value="1">%1</option><option value="10">%10</option><option value="20">%20</option></select></label><label>KDV dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label><label>Stok<input value={form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" /></label><label>Güvenlik stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label></div></section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>4</span><div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilir veya internetten erişilebilen HTTPS adresleri ekleyebilirsiniz.</p></div></div><label className="upload-ghost-box product-media-upload"><input type="file" accept="image/jpeg,image/png" multiple onChange={event => setMediaFiles(Array.from(event.target.files ?? []).slice(0, 8))} /><strong>{mediaFiles.length ? `${mediaFiles.length} dosya seçildi` : 'Ürün görsellerini dosya olarak seç'}</strong><small>En fazla 8 adet JPEG veya PNG, dosya başına 10 MB</small></label><label>Görsel URL listesi<textarea value={form.mediaUrls} onChange={event => updateField('mediaUrls', event.target.value)} placeholder="İsteğe bağlı: Her satıra bir HTTPS görsel adresi girin" /></label>{(mediaUrls.length > 0 || mediaFiles.length > 0) && <div className="media-preview-strip">{mediaFiles.map((file, index) => <figure key={`${file.name}-${file.lastModified}`}><img src={URL.createObjectURL(file)} alt={`${form.title || 'Ürün'} ${index + 1}`} /><figcaption>{index === 0 && !mediaUrls.length ? 'Ana görsel' : file.name}</figcaption></figure>)}{mediaUrls.slice(0, 8 - mediaFiles.length).map((url, index) => <figure key={url}><img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} /><figcaption>{index === 0 && !mediaFiles.length ? 'Ana görsel' : `${index + 1}. görsel`}</figcaption></figure>)}</div>}</section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>5</span><div><h2>Ürün özellikleri</h2><p>Bilgiler kategori &amp; özellik eşleme sayfasındaki kategori özellik başlıklarından gelir.</p></div></div><div className="attribute-variant-action"><div><strong>Varyantları oluştur</strong><small>Varyant olacak özellikleri ve değerleri seçtikten sonra kombinasyonları oluşturun.</small></div><div className="attribute-variant-actions"><button type="button" onClick={generateVariants}>Ürünleri ekle</button><button type="button" className="secondary" onClick={clearVariants}>Oluşan varyantları temizle</button></div></div>{!form.categoryId ? <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde o kategoriye bağlanan özellikler burada görünür.</p></div> : requirements.isLoading ? <p>Kategori özellikleri yükleniyor…</p> : requirements.isError ? <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Önce kategori eşleme ekranında ilgili kategorinin özellik başlıklarını hazırlayın.</p></div> : <div className="attribute-builder-list">{(requirements.data ?? []).sort((a, b) => a.displayOrder - b.displayOrder).map(item => <article className="attribute-builder-card" key={item.attributeId}><div className="attribute-builder-head"><label className="attribute-builder-toggle"><input type="checkbox" checked={variantAttributeIds.includes(item.attributeId)} onChange={() => toggleVariantAttribute(item.attributeId)} disabled={!item.attribute.values.length} /> <span>{item.attribute.name}{item.isRequired ? ' *' : ''}</span></label><small>{item.attribute.values.length} değer · {variantAttributeIds.includes(item.attributeId) ? 'varyant özelliği' : 'ürün özelliği'}</small></div>{item.attribute.values.length ? <div className="option-chip-list">{item.attribute.values.map(value => <button type="button" key={value.id} className={`option-chip ${(attributeSelections[item.attributeId] ?? []).includes(value.id) ? 'active' : ''}`} onClick={() => toggleAttributeValue(item.attributeId, value.id)}>{value.value}</button>)}</div> : item.attribute.dataType === 'BOOLEAN' ? <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label> : <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder="Değer girin" /></label>}</article>)}</div>}</section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>6</span><div><h2>Ürün seçenek grupları</h2><p>İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</p></div></div>{variantRows.length > 0 && <div className="variant-bulk-editor"><input value={bulkStock} onChange={event => setBulkStock(event.target.value)} type="number" min="0" placeholder="Tüm stoklar" /><input value={bulkSalePrice} onChange={event => setBulkSalePrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm satış fiyatları" /><input value={bulkListPrice} onChange={event => setBulkListPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm liste fiyatları" /><button type="button" className="secondary" onClick={applyBulk}>Tümüne uygula</button></div>}<div className="variant-table-editor"><div className="variant-table-head"><span aria-hidden="true" /><span>Seçenek</span><span>Barkod</span><span>Stok kodu</span><span>Stok</span><span>Fiyat</span><span>Liste fiyatı</span><span>İşlem</span></div>{variantRows.length ? variantRows.map(row => <div className={`variant-table-row ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key} onDragOver={event => event.preventDefault()} onDragEnter={() => { if (!draggedVariantKey || draggedVariantKey === row.key || dragOverVariantKey === row.key) return; swapVariants(draggedVariantKey, row.key); setDragOverVariantKey(row.key) }}><span className="variant-drag-handle" draggable title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onDragStart={event => { event.dataTransfer.effectAllowed = 'move'; setDraggedVariantKey(row.key); setDragOverVariantKey(null) }} onDragEnd={() => { setDraggedVariantKey(null); setDragOverVariantKey(null) }}>☰</span><input value={row.optionSignature} readOnly /><input value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" /><input value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" /><input value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" /><input value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /><input value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /><button type="button" className="secondary" onClick={() => setVariantRows(rows => rows.filter(item => item.key !== row.key))}>Sil</button></div>) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini işaretleyip “Ürünleri ekle” dediğinizde varyant satırları burada oluşur.</p></div>}</div></section>
    </div><aside className="panel publish-channel-panel"><div className="editor-section-title"><span>7</span><div><h2>Yayınlanacak kanallar</h2><p>Seçilen aktif Trendyol bağlantılarında fiyat teklifi, listing profile ve yayın işi hazırlanır.</p></div></div><div className="channel-choice-list">{activeConnections.map(item => <label key={item.id} className="channel-choice"><input type="checkbox" checked={selectedChannelIds.includes(item.id)} onChange={() => updateChannel(item.id)} /> <span>{item.displayName}</span><small>{selectedChannelIds.includes(item.id) ? 'Seçildi' : 'Seçilmedi'}</small></label>)}{!activeConnections.length && <p>ACTIVE Trendyol bağlantısı bulunamadı.</p>}</div><div className="channel-help"><strong>Güvenli yayın</strong><p>Stage manuel yayın; aktif bağlantı, doğrulanmış kimlik bilgisi, geçerli ürün verisi ve tekrar korumasıyla çalışır. Production yayınında master ve bağlantı dış-yazma anahtarları ayrıca zorunludur.</p></div></aside></div>

    <section className="product-submit-sticky"><div><strong>Ürün kayda hazır</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><button disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Ürünü kaydet'}</button></section>
    <ErrorBox error={error ?? categories.error ?? brands.error ?? connections.error} />{notice && <p className="notice" role="status">{notice}</p>}{created && <p className="success">Oluşturuldu: <Link to={`/products/${created.id}`}>ürünü aç</Link></p>}
  </form></Page>
}

export function ProductDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [notice, setNotice] = useState(''); const [description, setDescription] = useState('')
  if (id) return <NewProductPage editProductId={id} />
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '' }); const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({})
  const query = useQuery({ queryKey: ['product', id], queryFn: () => hubApi<Product>(`/products/${id}`), enabled: !!id })
  useEffect(() => {
    if (!query.data) return
    setForm({ title: query.data.title, description: query.data.description ?? '', brandId: query.data.brandId ?? '', categoryId: query.data.categoryId ?? '' })
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of query.data.attributes ?? []) {
      if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]
      else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue
      else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue)
      else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır'
    }
    setAttributeSelections(selected); setAttributeTextValues(typed)
  }, [query.data?.id, query.data?.version])
  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  function toggleAttributeValue(requirement: CategoryRequirement, valueId: string) { setAttributeSelections(current => { const values = current[requirement.attributeId] ?? []; if (values.includes(valueId)) return { ...current, [requirement.attributeId]: values.filter(item => item !== valueId) }; return { ...current, [requirement.attributeId]: requirement.attribute.dataType === 'SINGLE_SELECT' ? [valueId] : [...values, valueId] } }) }
  async function updateCatalogDetails(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!query.data) return
    try {
      const attributes = (requirements.data ?? []).flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
      // Product edit workspace follows the creation workspace below.
      await hubApi(`/products/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${query.data.version}"` }, body: JSON.stringify({ title: form.title, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes }) })
      setNotice('Ürün bilgileri ve kategori özellikleri güncellendi.'); await refresh()
    } catch (error) { setNotice(error instanceof Error ? error.message : 'Ürün güncellenemedi.') }
  }
  const categories = useQuery({ queryKey: ['categories', 'edit-product'], queryFn: () => hubApi<PageData<Category>>('/catalog/categories?limit=200') })
  const brands = useQuery({ queryKey: ['brands', 'edit-product'], queryFn: () => hubApi<PageData<Brand>>('/catalog/brands?limit=200') })
  const requirements = useQuery({ queryKey: ['category-requirements', 'edit-product', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const connections = useQuery({ queryKey: ['connections', 'product-publication'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const status = useQuery({ queryKey: ['publication-status', id, connectionId], queryFn: () => hubApi<PublicationStatus>(`/products/${id}/publication-status/${connectionId}`), enabled: !!id && !!connectionId, retry: false })
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && item.status === 'ACTIVE') ?? []
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  useEffect(() => { if (query.data) setDescription(query.data.description ?? '') }, [query.data?.id, query.data?.description])
  const refresh = async () => { await client.invalidateQueries({ queryKey: ['product', id] }); await client.invalidateQueries({ queryKey: ['products'] }) }
  async function updateProduct(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/products/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${query.data.version}"` }, body: JSON.stringify({ title: data.get('title'), description, brandId: query.data.brandId, categoryId: query.data.categoryId }) }); setNotice('Ürün bilgileri güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Ürün güncellenemedi.') } }
  async function image(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: query.data.id, variantId: null, url: data.get('imageUrl'), mediaRole: 'PRIMARY', sortOrder: 0, altText: query.data.title }) }); setNotice('Ana görsel güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel güncellenemedi.') } }
  async function uploadImage(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const source = new FormData(event.currentTarget); const file = source.get('file'); if (!(file instanceof File) || !file.size) return; const body = new FormData(); body.set('file', file); body.set('productId', query.data.id); body.set('mediaRole', 'PRIMARY'); body.set('sortOrder', '0'); body.set('altText', query.data.title); try { await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body }); setNotice('Ana görsel dosyadan güncellendi.'); event.currentTarget.reset(); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel yüklenemedi.') } }
  async function run(path: string, body: object) { try { setNotice(''); const jobId = await hubApi<string>(path, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); setNotice(`İş kuyruğa alındı: ${jobId}`); await client.invalidateQueries({ queryKey: ['publication-status', id, connectionId] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.') } }
  // Product edit workspace render point.
  if (query.data) return <Page title="Ürün Düzenle" eyebrow="Katalog"><p className="lede page-lede">Ürün ekleme çalışma alanındaki aynı katalog, özellik, fiyat, stok ve yayın bölümlerinden mevcut kaydı yönetin.</p>{notice && <div role="status" className="notice">{notice}</div>}<div className="product-creation-workspace product-edit-workspace">
    <form id="product-edit-form" onSubmit={updateCatalogDetails}><section className="panel product-step-card"><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün adı, marka, kategori ve açıklamayı ürün eklerkenki alan düzeninde güncelleyin.</p></div></div><div className="product-step-grid product-basics-grid"><label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label><label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label>Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label><label>Model kodu<input value={query.data.modelCode ?? ''} readOnly aria-label="Model kodu" /></label><label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label></div></section>
      <section className="panel product-step-card"><div className="editor-section-title"><span>2</span><div><h2>Ürün özellikleri</h2><p>Kategoriye bağlı değerler kayıtlı seçimleriyle gelir ve ürünle birlikte kaydedilir.</p></div></div>{!form.categoryId ? <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde bağlı özellikler burada görünür.</p></div> : requirements.isLoading ? <p>Kategori özellikleri yükleniyor…</p> : requirements.isError ? <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Özellikler yüklenmeden ürün kaydedilemez.</p></div> : <div className="attribute-builder-list">{(requirements.data ?? []).sort((a, b) => a.displayOrder - b.displayOrder).map(item => <article className="attribute-builder-card edit-attribute-card" key={item.attributeId}><div className="attribute-builder-head"><strong>{item.attribute.name}{item.isRequired ? ' *' : ''}</strong><small>{item.attribute.dataType}</small></div>{item.attribute.values.length ? <div className="option-chip-list">{item.attribute.values.map(value => <button type="button" key={value.id} className={`option-chip ${(attributeSelections[item.attributeId] ?? []).includes(value.id) ? 'active' : ''}`} onClick={() => toggleAttributeValue(item, value.id)}>{value.value}</button>)}</div> : item.attribute.dataType === 'BOOLEAN' ? <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label> : <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} /></label>}</article>)}</div>}</section></form>
    <div className="product-layout-grid"><div className="product-main-stack"><section className="panel product-step-card"><div className="editor-section-title"><span>3</span><div><h2>Fiyat, stok ve vergi</h2><p>Her varyantın stok ve kanal fiyatı kendi sürüm kontrolüyle güncellenir.</p></div></div>{query.data.variants.map(variant => <section className="variant-editor product-edit-variant" key={variant.id}><div><strong>{variant.optionSignature || variant.sku}</strong><span>SKU: {variant.sku} · Barkod: {variant.barcode ?? '—'} · Kullanılabilir: {variant.available}</span></div><VariantQuickEditor variant={variant} connections={activeConnections} onChanged={refresh} /></section>)}</section>
      <section className="panel product-step-card cargo-size-card"><div className="editor-section-title"><span>4</span><div><h2>Kargo ölçüleri ve desi</h2><p>Varyant ölçüleri, ürün ekleme sayfasındaki alanlarla aynı formatta gösterilir.</p></div></div><div className="product-step-grid">{query.data.variants.map(variant => <article className="variant-dimensions" key={variant.id}><strong>{variant.sku}</strong><dl><div><dt>Ağırlık</dt><dd>{variant.weight ?? '—'} kg</dd></div><div><dt>En × Boy × Yükseklik</dt><dd>{variant.width ?? '—'} × {variant.length ?? '—'} × {variant.height ?? '—'} cm</dd></div><div><dt>Desi</dt><dd>{variant.desi ?? '—'}</dd></div></dl></article>)}</div></section>
      <section className="panel product-step-card"><div className="editor-section-title"><span>5</span><div><h2>Görseller</h2><p>Yeni ürün ekranındaki gibi dosyadan veya HTTPS adresinden ana görseli güncelleyin.</p></div></div><div className="product-step-grid"><form onSubmit={uploadImage}><label className="upload-ghost-box product-media-upload"><input name="file" type="file" accept="image/jpeg,image/png" required /><strong>Dosya seç</strong><small>JPEG veya PNG, en fazla 10 MB</small></label><button>Görseli yükle</button></form><form onSubmit={image}><label>Görsel URL<input name="imageUrl" type="url" defaultValue={query.data.primaryImageUrl ?? ''} required /></label><button>Görseli kaydet</button></form></div></section></div>
      <aside className="panel publish-channel-panel"><div className="editor-section-title"><span>6</span><div><h2>Yayınlanacak kanallar</h2><p>Yayın yönetimi ürün ekleme akışındaki güvenli kanal bölümünde kalır.</p></div></div><label>Aktif Trendyol bağlantısı<select aria-label="Ürün Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>{connectionId && <><div className="actions spaced"><button type="button" onClick={() => run(`/products/${id}/publication-jobs`, { connectionId })}>Yeni ürün olarak yayınla</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/update-jobs`, { connectionId })}>Trendyol ürününü güncelle</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: true })}>Trendyol'da arşivle</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: false })}>Arşivden çıkar</button></div>{status.isLoading ? <p>Yayın durumu yükleniyor…</p> : status.isError ? <p className="notice">Henüz listing profili veya yayın durumu yok.</p> : status.data && <dl className="details"><dt>Gerçek durum</dt><dd>{status.data.actualStatus ?? '—'}</dd><dt>Son iş</dt><dd>{status.data.lastJobStatus ?? '—'}</dd><dt>Ret kodu</dt><dd>{status.data.lastRejectionCode ?? '—'}</dd></dl>}</>}</aside></div>
    <section className="product-submit-sticky"><div><strong>Ürün düzenlemeye hazır</strong><p>{query.data.variants.length} varyant · kategori özellikleri kaydedilecek</p></div><button form="product-edit-form">Değişiklikleri kaydet</button></section><ErrorBox error={categories.error ?? brands.error ?? connections.error} /></div></Page>
  /* Legacy product-detail layout retained below only while the unified edit workspace replaces it.
  return <Page title={query.data?.title ?? 'Ürün'} eyebrow="Ürün detayı">{query.isError ? <ErrorBox error={query.error} /> : !query.data ? <p>Yükleniyor…</p> : <>
    {notice && <div role="status" className="notice">{notice}</div>}<div className="detail-grid product-edit-overview"><article className="panel product-detail-hero">{query.data.primaryImageUrl ? <img src={query.data.primaryImageUrl} alt={query.data.title} /> : <span className="product-image-placeholder">Görsel yok</span>}<div><Tag>{query.data.status}</Tag><p>Model: {query.data.modelCode ?? '—'}</p><p>Stok: <strong>{query.data.totalStock}</strong></p><p>Başlangıç fiyatı: <strong>{money(query.data.startingPrice, query.data.currency)}</strong></p><p>Aktif platformlar: {query.data.activePlatforms?.join(', ') || '—'}</p></div></article><form className="panel product-step-card product-edit-form" onSubmit={updateProduct}><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Oluşturma ekranındaki alan yapısıyla ürünü düzenleyin.</p></div></div><label>Ürün adı<input name="title" defaultValue={query.data.title} required /></label><label>Açıklama<RichTextEditor value={description} onChange={setDescription} /></label><button>Bilgileri kaydet</button></form></div>
    <div className="detail-grid product-edit-media"><form className="panel product-step-card" onSubmit={uploadImage}><div className="editor-section-title"><span>2</span><div><h2>Ürün görseli yükle</h2><p>JPEG veya PNG dosyasını doğrudan özel depolamaya yükleyin.</p></div></div><label className="upload-ghost-box product-media-upload"><input name="file" type="file" accept="image/jpeg,image/png" required /><strong>Dosya seç</strong><small>En fazla 10 MB</small></label><button>Görseli yükle</button></form><form className="panel product-step-card" onSubmit={image}><div className="editor-section-title"><span>3</span><div><h2>Görsel adresi</h2><p>İsteğe bağlı olarak HTTPS görsel adresi kullanın.</p></div></div><label>Görsel URL<input name="imageUrl" type="url" defaultValue={query.data.primaryImageUrl ?? ''} required /></label><button>Görseli kaydet</button></form></div>
    <article className="panel"><h2>Varyantlar, stok ve fiyat</h2>{query.data.variants.map(variant => <section className="variant-editor" key={variant.id}><div><strong>{variant.sku}</strong><span>Barkod: {variant.barcode ?? '—'} · Model: {variant.modelCode ?? '—'} · Ölçü: {variant.width ?? '—'} × {variant.length ?? '—'} × {variant.height ?? '—'} cm · Ağırlık: {variant.weight ?? '—'} kg</span></div><VariantQuickEditor variant={variant} connections={activeConnections} onChanged={refresh} /></section>)}</article>
    <article className="panel"><h2>Trendyol yayın yönetimi</h2><p className="notice">Stage bağlantısında manuel yayın ve güncelleme doğrudan sağlayıcıya gönderilir. Production’da aktif bağlantı ve dış-yazma anahtarları gerekir.</p><label>Aktif Trendyol bağlantısı<select aria-label="Ürün Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>{connectionId && <><div className="actions spaced"><button onClick={() => run(`/products/${id}/publication-jobs`, { connectionId })}>Yeni ürün olarak yayınla</button><button className="secondary" onClick={() => run(`/products/${id}/update-jobs`, { connectionId })}>Trendyol ürününü güncelle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: true })}>Trendyol'da arşivle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: false })}>Arşivden çıkar</button></div>{status.isLoading ? <p>Yayın durumu yükleniyor…</p> : status.isError ? <p className="notice">Henüz listing profili veya yayın durumu yok.</p> : status.data && <dl className="details"><dt>Gerçek durum</dt><dd>{status.data.actualStatus ?? '—'}</dd><dt>Son job</dt><dd>{status.data.lastJobStatus ?? '—'}</dd><dt>Ret kodu</dt><dd>{status.data.lastRejectionCode ?? '—'}</dd></dl>}</>}</article>
  </>}</Page> */
}

export function CategoriesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['categories'], queryFn: () => hubApi<PageData<Category>>('/catalog/categories') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name'), parentId: data.get('parentId') || null }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['categories'] }) } catch (reason) { setError(reason) } }
  return <Page title="Kategoriler" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Kategori adı<input name="name" required /></label><label>Üst kategori kimliği<input name="parentId" /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="tree-list">{query.data?.items.map(item => <article key={item.id} style={{ marginLeft: Math.min(item.depth, 6) * 18 }}><div><strong>{item.name}</strong><small>{item.path}</small></div><Tag>{item.isLeaf ? 'LEAF' : 'PARENT'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function BrandsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['brands'], queryFn: () => hubApi<PageData<Brand>>('/catalog/brands') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name') }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['brands'] }) } catch (reason) { setError(reason) } }
  return <Page title="Markalar" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Marka adı<input name="name" required /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><strong>{item.name}</strong><Tag>{item.isActive ? 'ACTIVE' : 'DISABLED'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function AttributesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['attributes'], queryFn: () => hubApi<PageData<Attribute>>('/catalog/attributes') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); const values = String(data.get('values') || '').split(',').map(x => x.trim()).filter(Boolean).map((value, sortOrder) => ({ value, sortOrder })); try { await hubApi('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ code: data.get('code'), name: data.get('name'), dataType: data.get('dataType'), selectionMode: null, unit: null, values }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['attributes'] }) } catch (reason) { setError(reason) } }
  return <Page title="Özellikler" eyebrow="Katalog"><form className="panel form-grid" onSubmit={submit}><label>Kod<input name="code" required /></label><label>Ad<input name="name" required /></label><label>Tip<select name="dataType"><option>TEXT</option><option>NUMBER</option><option>SINGLE_SELECT</option><option>MULTI_SELECT</option><option>BOOLEAN</option></select></label><label>Seçenekler (virgülle)<input name="values" /></label><ErrorBox error={error} /><button>Ekle</button></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><div><strong>{item.name}</strong><small>{item.code}</small></div><Tag>{item.dataType}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function ImportsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['imports'], queryFn: () => hubApi<PageData<ImportSession>>('/imports') })
  async function create(sourceType: string) { try { await hubApi('/imports', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ sourceType, connectionId: null }) }); await client.invalidateQueries({ queryKey: ['imports'] }) } catch (reason) { setError(reason) } }
  return <Page title="İçe aktarımlar" eyebrow="Katalog" action={<div className="actions"><button onClick={() => create('CSV')}>CSV başlat</button><button onClick={() => create('XLSX')}>XLSX başlat</button></div>}><ErrorBox error={error ?? query.error} />{!query.data?.items.length ? <div className="empty">Henüz import oturumu yok.</div> : <div className="table-wrap"><table><thead><tr><th>Kaynak</th><th>Durum</th><th>Satır</th><th></th></tr></thead><tbody>{query.data.items.map(item => <tr key={item.id}><td>{item.sourceType}</td><td><Tag>{item.status}</Tag></td><td>{item.validRows}/{item.totalRows}</td><td><Link to={`/imports/${item.id}`}>İncele</Link></td></tr>)}</tbody></table></div>}</Page>
}

export function ImportDetailPage() {
  const { id } = useParams(); const client = useQueryClient(); const [error, setError] = useState<unknown>(); const session = useQuery({ queryKey: ['import', id], queryFn: () => hubApi<ImportSession>(`/imports/${id}`), enabled: !!id, refetchInterval: 4000 }); const candidates = useQuery({ queryKey: ['candidates', id], queryFn: () => hubApi<PageData<Candidate>>(`/imports/${id}/candidates`), enabled: session.data?.status === 'REVIEW_REQUIRED' })
  async function upload(event: FormEvent<HTMLFormElement>) { event.preventDefault(); try { await hubApi(`/imports/${id}/source-file`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: new FormData(event.currentTarget) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function map(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!session.data) return; const data = new FormData(event.currentTarget); const headers = String(data.get('headers')).split(',').map(x => x.trim()); const fields = String(data.get('fields')).split(',').map(x => x.trim()); try { await hubApi(`/imports/${id}/column-mapping`, { method: 'PUT', headers: { 'If-Match': `"v${session.data.version}"` }, body: JSON.stringify({ profileName: 'Manuel eşleme', variantGroupKey: null, mappings: headers.map((sourceColumn, sortOrder) => ({ sourceColumn, targetField: fields[sortOrder], sortOrder })) }) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function job(kind: 'preview' | 'apply') { try { await hubApi(`/imports/${id}/${kind}-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() } }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function decide(candidate: Candidate, decision: 'CREATE' | 'LINK' | 'SKIP') { try { await hubApi(`/imports/${id}/decisions/${candidate.id}`, { method: 'PUT', headers: { 'If-Match': `"v${candidate.version}"` }, body: JSON.stringify({ decision, productId: decision === 'LINK' ? candidate.productId : null, variantId: decision === 'LINK' ? candidate.variantId : null }) }); await client.invalidateQueries({ queryKey: ['candidates', id] }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  if (!session.data) return <Page title="İçe aktarım" eyebrow="Katalog"><ErrorBox error={session.error} /><p>Yükleniyor…</p></Page>
  return <Page title={`İçe aktarım ${session.data.id.slice(0, 8)}`} eyebrow="Katalog"><div className="metrics">{[['Durum', session.data.status], ['Toplam', session.data.totalRows], ['Geçerli', session.data.validRows], ['Hatalı', session.data.errorRows]].map(([label, value]) => <article key={label}><small>{label}</small><strong>{value}</strong></article>)}</div><ErrorBox error={error} />{session.data.status === 'CREATED' && <div className="detail-grid"><form className="panel" onSubmit={upload}><h2>1. Dosya</h2><input name="file" type="file" accept=".csv,.xlsx" required /><button>Yükle</button></form><form className="panel" onSubmit={map}><h2>2. Kolon eşleme</h2><label>Başlıklar<input name="headers" placeholder="Ürün,SKU,Barkod" required /></label><label>Hedefler<input name="fields" placeholder="title,sku,barcode" required /></label><button>Kaydet</button></form></div>}<div className="actions spaced">{session.data.status === 'CREATED' && session.data.sourceAssetId && <button onClick={() => job('preview')}>Preview oluştur</button>}{session.data.status === 'READY_TO_APPLY' && <button onClick={() => job('apply')}>Kararları uygula</button>}{session.data.errorRows > 0 && <a className="button-link secondary" href={`/api/v1/imports/${id}/errors.csv`}>Hataları indir</a>}</div>{candidates.data?.items.map(candidate => <article className="candidate" key={candidate.id}><div><Tag>{candidate.matchRule}</Tag><code>{candidate.safeSummary}</code></div><div className="actions"><button onClick={() => decide(candidate, 'CREATE')}>Yeni</button>{candidate.productId && <button onClick={() => decide(candidate, 'LINK')}>Eşle</button>}<button className="secondary" onClick={() => decide(candidate, 'SKIP')}>Atla</button></div></article>)}</Page>
}

export function InventoryPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const [connectionId, setConnectionId] = useState(''); const [notice, setNotice] = useState(''); const query = useQuery({ queryKey: ['inventory'], queryFn: () => hubApi<PageData<Inventory>>('/inventory') })
  const connections = useQuery({ queryKey: ['connections', 'inventory-sync'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && item.status === 'ACTIVE') ?? []
  async function adjust(item: Inventory, delta: number) { const reason = window.prompt('Düzeltme nedeni'); if (!reason) return; try { await hubApi(`/inventory/${item.variantId}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason, sourceEventId: key() }) }); await client.invalidateQueries({ queryKey: ['inventory'] }) } catch (failure) { setError(failure) } }
  async function sync() { if (!connectionId) return; try { const jobId = await hubApi<string>(`/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() } }); setNotice(`Birleşik fiyat-stok işi kuyruğa alındı: ${jobId}`) } catch (failure) { setNotice(failure instanceof Error ? failure.message : 'Senkronizasyon başlatılamadı.') } }
  return <Page title="Stok ve fiyat" eyebrow="Envanter" action={<div className="actions"><select aria-label="Fiyat stok Trendyol bağlantısı" value={connectionId} onChange={event => setConnectionId(event.target.value)}><option value="">Trendyol bağlantısı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName}</option>)}</select><button disabled={!connectionId} onClick={() => void sync()}>Fiyat + stok gönder</button></div>}><p className="notice">V1 depo kodu MAIN. Kullanılabilir = max(0, eldeki − rezervasyon − güvenlik stoğu). Trendyol'a fiyat ve stok tek dayanıklı batch ile gönderilir.</p>{notice && <div role="status" className="notice">{notice}</div>}<ErrorBox error={error ?? query.error} /><div className="table-wrap"><table><thead><tr><th>SKU</th><th>Depo</th><th>Eldeki</th><th>Rezerve</th><th>Kullanılabilir</th><th></th></tr></thead><tbody>{query.data?.items.map(item => <tr key={item.id}><td><strong>{item.sku}</strong></td><td>{item.locationCode}</td><td>{item.onHand}</td><td>{item.reserved}</td><td>{item.available}</td><td><div className="actions"><button onClick={() => adjust(item, 1)}>+1</button><button className="secondary" disabled={item.onHand <= 0} onClick={() => adjust(item, -1)}>−1</button></div></td></tr>)}</tbody></table></div></Page>
}
