import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages, type CursorPage } from './api'
import './styles/product-editor.css'
import './styles/products.css'

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
  mediaUrls?: string[]
}
type Product = Versioned & {
  title: string; description: string; brandId: string | null; categoryId: string | null; status: string; updatedAt: string
  variants: Variant[]; primaryImageUrl: string | null; totalStock: number; startingPrice: number | null; currency: string; modelCode: string | null; activePlatforms: string[] | null
  attributes?: Array<{ attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }>
  options?: Array<{ id: string; label: string; values: Array<{ id: string; label: string }> }>
  mediaUrls?: string[]
}
type ProductListFilters = { search: string; status: string; platform: string; stock: string }
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number; platforms: string[] }
type ImportSession = Versioned & { sourceType: string; status: string; totalRows: number; validRows: number; errorRows: number; reviewRows: number; sourceAssetId: string | null }
type Candidate = Versioned & { matchRule: string; safeSummary: string; productId: string | null; variantId: string | null }
type Inventory = Versioned & { variantId: string; sku: string; locationCode: string; onHand: number; reserved: number; available: number }
type TrendyolConnection = { id: string; platformCode: string; displayName: string; externalStoreId: string; status: string }
type AcceptedJob = { jobId: string }

const key = () => crypto.randomUUID()
const isProductPublicationConnection = (item: TrendyolConnection) => item.platformCode.trim().toUpperCase() === 'TRENDYOL' && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())
async function fetchProductPage(limit: number, filters: ProductListFilters, after: string | null) {
  const params = new URLSearchParams({ limit: String(limit) })
  if (after) params.set('after', after)
  if (filters.search) params.set('search', filters.search)
  if (filters.status) params.set('status', filters.status)
  if (filters.platform) params.set('platform', filters.platform)
  if (filters.stock) params.set('stock', filters.stock)
  return hubApi<CursorPage<Product>>(`/products?${params.toString()}`)
}
const ErrorBox = ({ error }: { error: unknown }) => error ? <div className="error" role="alert">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</div> : null
type OperationFeedback = { message: string; kind: 'success' | 'error' | 'info' }
function OperationFeedbackToast({ feedback, onClose }: { feedback: OperationFeedback | null; onClose: () => void }) {
  if (!feedback) return null
  const title = feedback.kind === 'success' ? 'İşlem başarılı' : feedback.kind === 'error' ? 'İşlem başarısız' : 'İşlem sürüyor'
  return <div className={`operation-feedback-toast ${feedback.kind}`} role={feedback.kind === 'error' ? 'alert' : 'status'} aria-live="polite"><span className="operation-feedback-icon" aria-hidden="true">{feedback.kind === 'success' ? '✓' : feedback.kind === 'error' ? '!' : '…'}</span><div><strong>{title}</strong><p>{feedback.message}</p></div><button type="button" onClick={onClose} aria-label="Durum raporunu kapat">×</button></div>
}

function LocalImagePreview({ file, alt, caption, onRemove, onZoom }: { file: File, alt: string, caption: string, onRemove?: () => void, onZoom?: (url: string) => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  return <figure className="image-preview-card"><img src={url} alt={alt} className="clickable-thumb" onClick={() => onZoom?.(url)} title="Büyütmek için tıklayın" />{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}>✕</button>}<figcaption>{caption}</figcaption></figure>;
}
function ImageLightboxModal({ image, onClose }: { image: { url: string; title: string }; onClose: () => void }) {
  return (
    <div className="workspace-modal-backdrop image-lightbox-backdrop" role="presentation" onMouseDown={onClose}>
      <div className="image-lightbox-modal" role="dialog" aria-modal="true" onMouseDown={e => e.stopPropagation()}>
        <button type="button" className="lightbox-close" onClick={onClose} aria-label="Kapat">×</button>
        <div className="lightbox-img-wrap">
          <img src={image.url} alt={image.title} />
        </div>
        {image.title && <div className="lightbox-caption">{image.title}</div>}
      </div>
    </div>
  )
}

type ProductMediaOption = { value: string; label: string; url?: string; file?: File }
type VariantMediaGroup = { id: string; name: string; values: Array<{ id: string; value: string }>; attributeId?: string }
type ParsedVariantOption = { name: string; value: string }

function parseVariantOptionSignature(signature: string): ParsedVariantOption[] {
  return signature.split(/\s*\|\s*|_(?=[^_:=]+\s*[:=])/).flatMap(part => {
    const separatorIndex = part.search(/\s*[:=]/)
    if (separatorIndex < 0) return []
    const name = part.slice(0, separatorIndex).trim()
    const value = part.slice(separatorIndex).replace(/^\s*[:=]\s*/, '').trim()
    return name && value ? [{ name, value }] : []
  })
}

function VariantImageIcon() {
  return <svg className="variant-media-icon" viewBox="0 0 24 24" focusable="false" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2" /><circle cx="8.5" cy="9" r="1.5" /><path d="m4 17 5-5 3 3 2-2 6 6" /></svg>
}

function VariantDragHandleIcon() {
  return <svg className="variant-drag-icon" viewBox="0 0 16 20" focusable="false" aria-hidden="true"><circle cx="5" cy="4" r="1.4" /><circle cx="11" cy="4" r="1.4" /><circle cx="5" cy="10" r="1.4" /><circle cx="11" cy="10" r="1.4" /><circle cx="5" cy="16" r="1.4" /><circle cx="11" cy="16" r="1.4" /></svg>
}

function MediaOptionThumb({ option, selected, onClick }: { option: ProductMediaOption; selected: boolean; onClick: () => void }) {
  const [src, setSrc] = useState(option.url ?? '')
  useEffect(() => {
    if (!option.file) return
    const objectUrl = URL.createObjectURL(option.file)
    setSrc(objectUrl)
    return () => URL.revokeObjectURL(objectUrl)
  }, [option.file])
  return <button type="button" className={`variant-media-option ${selected ? 'selected' : ''}`} aria-label={`${option.label}${selected ? ' seçimini kaldır' : ''}`} aria-pressed={selected} onClick={onClick}>
    <span className="variant-media-option-image">{src ? <img src={src} alt="" /> : <VariantImageIcon />}</span>
    <i aria-hidden="true">{selected ? '✓' : ''}</i>
  </button>
}

function VariantMediaPickerModal({
  mode,
  options,
  selectedRef,
  groups,
  selectedGroupId,
  selectedValueId,
  matchedVariantCount,
  onRefChange,
  onGroupChange,
  onValueChange,
  onApply,
  onClose
}: {
  mode: 'variant' | 'bulk'
  options: ProductMediaOption[]
  selectedRef: string
  groups?: VariantMediaGroup[]
  selectedGroupId?: string
  selectedValueId?: string
  matchedVariantCount?: number
  onRefChange: (value: string) => void
  onGroupChange?: (value: string) => void
  onValueChange?: (value: string) => void
  onApply: () => void
  onClose: () => void
}) {
  const selectedGroup = groups?.find(group => group.id === selectedGroupId)
  const selectedValue = selectedGroup?.values.find(value => value.id === selectedValueId)
  return <div className="workspace-modal-backdrop variant-media-picker-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal variant-media-picker-modal" role="dialog" aria-modal="true" aria-labelledby="variant-media-picker-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">VARYANT GÖRSELİ</p><h2 id="variant-media-picker-title">{mode === 'bulk' ? 'Seçeneklere görsel ata' : 'Varyant görseli seç'}</h2><p>{mode === 'bulk' ? 'Seçenek grubu ve değerini seçin; aynı değere sahip tüm varyantlara tek görsel uygulayın.' : 'Ürün görsellerinden bu varyanta ait ana görseli seçin.'}</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button>
      </header>
      {mode === 'bulk' && groups?.length ? <><div className="variant-media-bulk-fields"><label>Seçenek grubu<select value={selectedGroupId} onChange={event => onGroupChange?.(event.target.value)}><option value="">Seçenek grubu seçin</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label><label>Seçenek değeri<select value={selectedValueId} onChange={event => onValueChange?.(event.target.value)} disabled={!selectedGroup}><option value="">Değer seçin</option>{selectedGroup?.values.map(value => <option key={value.id} value={value.id}>{value.value}</option>)}</select></label></div>{selectedValue && <p className={`variant-media-bulk-match-summary ${matchedVariantCount ? '' : 'is-empty'}`}>{matchedVariantCount ? <><strong>{selectedGroup?.name}: {selectedValue.value}</strong> seçili — {matchedVariantCount} varyant satırına uygulanacak.</> : <>Bu değerle eşleşen varyant satırı bulunamadı.</>}</p>}</> : null}
      <div className="variant-media-picker-grid">
        {options.length ? options.map(option => <MediaOptionThumb key={option.value} option={option} selected={selectedRef === option.value} onClick={() => onRefChange(selectedRef === option.value ? '' : option.value)} />) : <div className="empty small"><strong>Seçilebilir görsel yok</strong><p>Önce ürün görsellerine HTTPS linki veya dosya ekleyin.</p></div>}
      </div>
      <footer><button type="button" className="secondary" onClick={() => onRefChange('')}>Görseli kaldır</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={onApply} disabled={!options.length || (mode === 'bulk' && (!selectedGroupId || !selectedValueId || !matchedVariantCount))}>{mode === 'bulk' ? selectedRef ? 'Seçeneklere uygula' : 'Görselleri kaldır' : 'Görseli kaydet'}</button></footer>
    </section>
  </div>
}
const Tag = ({ children }: { children: ReactNode }) => <span className="tag">{children}</span>
const money = (value: number | null | undefined, currency = 'TRY') => value == null ? '—' : new Intl.NumberFormat('tr-TR', { style: 'currency', currency }).format(value)
function Page({ title, eyebrow, action, className, children }: { title: string; eyebrow: string; action?: ReactNode; className?: string; children: ReactNode }) { const productBack = className?.includes('product-add-page'); return <section className={`content stitch-page ${className ?? ''}`}><div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{productBack && <Link className="product-heading-back" to="/products" aria-label="Ürünlere dön">←</Link>}{title}</h1></div>{action}</div>{children}</section> }

type QuickEditMode = 'stock' | 'price' | 'both'

function ProductQuickEditModal({ products, connections, mode = 'both', onChanged, onClose, onResult }: { products: Product[]; connections: TrendyolConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown>; onClose: () => void; onResult?: (message: string, kind: 'success' | 'error') => void }) {
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
  const [selectedColors, setSelectedColors] = useState<string[]>([])
  const [selectedSizes, setSelectedSizes] = useState<string[]>([])
  const [listPrice, setListPrice] = useState(''); const [salePrice, setSalePrice] = useState(''); const [stockAmount, setStockAmount] = useState('')
  const [stockAction, setStockAction] = useState<'SET' | 'ADD' | 'SUBTRACT'>('SET'); const [notice, setNotice] = useState(''); const [saving, setSaving] = useState(false)
  const selectedSet = new Set(selectionDraft)
  const activeSelection = selectionDraft
  const activeSelectedSet = selectedSet
  const sizeOptions = [...new Set(variants.filter(item => !selectedColors.length || selectedColors.includes(colorOf(item))).map(sizeOf))]
  const toggleColor = (color: string) => {
    const next = selectedColors.includes(color) ? selectedColors.filter(item => item !== color) : [...selectedColors, color]
    setSelectedColors(next)
    applyFilterSelection(next, selectedSizes)
  }
  const toggleSize = (size: string) => {
    const next = selectedSizes.includes(size) ? selectedSizes.filter(item => item !== size) : [...selectedSizes, size]
    setSelectedSizes(next)
    applyFilterSelection(selectedColors, next)
  }
  const toggle = (id: string) => { setSelectionDraft(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]) }
  const toggleGroup = (items: typeof variants) => { const ids = items.map(item => item.variant.id); const every = ids.every(id => selectedSet.has(id)); setSelectionDraft(current => every ? current.filter(id => !ids.includes(id)) : [...new Set([...current, ...ids])]) }
  function applyFilterSelection(colors: string[], sizes: string[]) {
    const ids = !colors.length && !sizes.length ? [] : variants.filter(item => (!colors.length || colors.includes(colorOf(item))) && (!sizes.length || sizes.includes(sizeOf(item)))).map(item => item.variant.id)
    setSelectionDraft(ids)
  }
  async function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (saving || !selectionDraft.length) return setNotice('Önce en az bir varyant seçin.')
    const targetSelection = selectionDraft
    const targetSelectionSet = new Set(targetSelection)
    const priceRequested = mode !== 'stock' && (listPrice !== '' || salePrice !== ''); const stockRequested = mode !== 'price' && stockAmount !== ''
    if (!priceRequested && !stockRequested) return setNotice('Uygulanacak fiyat veya stok değerini girin.')
    const list = listPrice === '' ? null : Number(listPrice); const sale = salePrice === '' ? null : Number(salePrice); const amount = stockAmount === '' ? null : Number(stockAmount)
    if ((list != null && (!Number.isFinite(list) || list < 0)) || (sale != null && (!Number.isFinite(sale) || sale < 0)) || (list != null && sale != null && list < sale) || (amount != null && (!Number.isFinite(amount) || amount < 0))) return setNotice('Değerleri kontrol edin; negatif fiyat/stok veya hatalı fiyat sıralaması kullanılamaz.')
    setSaving(true); setNotice('Seçilen varyantlar güncelleniyor…')
    try {
      for (const item of variants.filter(value => targetSelectionSet.has(value.variant.id))) {
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
      await onChanged(); onResult?.(`${targetSelection.length} varyant başarıyla güncellendi.`, 'success'); onClose()
    } catch (error) { const message = error instanceof Error ? error.message : 'Toplu düzenleme tamamlanamadı.'; setNotice(`Başarısız: ${message}`); onResult?.(message, 'error') } finally { setSaving(false) }
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
          <div className="quick-edit-filter-action"><p>Renk ve beden filtrelerini seçtikçe alttaki eşleşen varyantlar otomatik işaretlenir.</p></div>
          <div className="quick-edit-selection">
            {Object.entries(groups).map(([color, items]) => <details className="quick-edit-color" key={color} open={Object.keys(groups).length === 1}>
              <summary><label onClick={event => event.stopPropagation()}><input type="checkbox" checked={items.every(item => selectedSet.has(item.variant.id))} onChange={() => toggleGroup(items)} /> {color}</label><small>{items.length} varyant · {items.reduce((sum, item) => sum + item.variant.available, 0)} stok</small><b>⌄</b></summary>
              <div className="quick-edit-variants">{items.map(item => <div className="quick-edit-variant" key={item.variant.id}><input type="checkbox" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} /><span><strong>{item.variant.optionSignature || item.product.title}</strong><small>{item.product.title} · Stok kodu: {item.variant.sku}</small></span><QuickEditVariantControls variant={item.variant} connections={connections} onChanged={onChanged} onSelect={() => setSelectionDraft(current => current.includes(item.variant.id) ? current : [...current, item.variant.id])} /></div>)}</div>
            </details>)}
          </div>
          <div className="quick-edit-step-action"><p>{selectionDraft.length ? `${selectionDraft.length} varyant işaretli. Değerleri doğrudan uygulayabilirsiniz.` : 'Önce alt listeden varyant seçin veya üst filtreyi kullanın.'}</p></div>
        </details>
        <details className="quick-edit-step quick-edit-pricing-step" open>
          <summary><span><b>2</b> {mode === 'stock' ? 'Stok değerini düzenle' : mode === 'price' ? 'Fiyatı düzenle' : 'Fiyat ve stok değerini düzenle'}</span><small>{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : 'Varyant seçimi bekleniyor'}</small><i>⌄</i></summary>
          <div className="quick-edit-step-body">
            {activeSelection.length ? <div className="quick-edit-selected-list">{variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => <span key={item.variant.id}>{item.variant.optionSignature || item.variant.sku}</span>)}</div> : <p className="quick-edit-step-empty">Fiyat/stok değerini şimdi girebilirsiniz; uygulamak için en az bir varyantı işaretleyin.</p>}
            <div className="quick-edit-fields">
              {mode !== 'stock' && <fieldset><legend>Fiyat</legend><label>Liste fiyatı<input type="number" min="0" step="0.01" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></fieldset>}
              {mode !== 'price' && <fieldset><legend>Stok</legend><label>İşlem<select value={stockAction} onChange={event => setStockAction(event.target.value as typeof stockAction)}><option value="SET">Bu sayıya eşitle</option><option value="ADD">Bu kadar ekle (+)</option><option value="SUBTRACT">Bu kadar çıkar (−)</option></select></label><label>Miktar<input type="number" min="0" step="1" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></fieldset>}
            </div>
          </div>
        </details>
        {notice && <p className={notice.startsWith('Başarısız:') ? 'error' : 'notice'} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}>{notice}</p>}
        <footer className="quick-edit-footer"><span>{activeSelection.length ? `${activeSelection.length} varyant seçildi` : 'Varyant seçimi bekleniyor'}</span><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="submit" disabled={saving || !selectionDraft.length}>{saving ? 'Uygulanıyor…' : 'Seçilenlere uygula'}</button></footer>
      </form>
    </section>
  </div>
}

function QuickEditVariantControls({ variant, connections, onChanged, onSelect }: { variant: Variant; connections: TrendyolConnection[]; onChanged: () => Promise<unknown>; onSelect: () => void }) {
  const [price, setPrice] = useState(variant.salePrice ?? variant.listPrice ?? '')
  const [stock, setStock] = useState(variant.onHand)
  const [savingPrice, setSavingPrice] = useState(false); const [savingStock, setSavingStock] = useState(false)
  async function savePrice() {
    const salePrice = Number(price); if (savingPrice || price === '' || !Number.isFinite(salePrice) || salePrice < 0 || salePrice === (variant.salePrice ?? variant.listPrice ?? 0)) return
    const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''; const listPrice = Math.max(variant.listPrice ?? salePrice, salePrice)
    if (!variant.offerId && !connectionId) return
    const body = { connectionId, variantId: variant.id, listPrice, salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Hızlı varyant fiyat düzenleme' }
    setSavingPrice(true)
    try { if (variant.offerId) { if (variant.offerVersion == null) return; await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `\"v${variant.offerVersion}\"` }, body: JSON.stringify(body) }) } else await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); await onChanged() } finally { setSavingPrice(false) }
  }
  async function saveStock() {
    const target = Number(stock); const delta = target - variant.onHand; if (savingStock || !Number.isFinite(target) || target < 0 || delta === 0) return
    setSavingStock(true)
    try { await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Hızlı varyant stok düzenleme', sourceEventId: key() }) }); await onChanged() } finally { setSavingStock(false) }
  }
  return <div className="quick-edit-variant-controls" onClick={event => event.stopPropagation()}><label><small>Stok</small><input aria-label={`${variant.sku} stok`} value={stock} onFocus={onSelect} onChange={event => { onSelect(); setStock(Number(event.target.value || 0)) }} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} /></label><label><small>Fiyat</small><input aria-label={`${variant.sku} fiyat`} value={price} onFocus={onSelect} onChange={event => { onSelect(); setPrice(event.target.value === '' ? '' : Number(event.target.value)) }} onBlur={() => void savePrice()} type="number" min="0" step="0.01" disabled={savingPrice} /></label></div>
}

function ProductColorRows({ product, selected, onSelect, onQuickEdit, onImageClick, onDelete }: { product: Product; selected: boolean; onSelect: () => void; onQuickEdit: (mode: QuickEditMode) => void; onImageClick: (url: string, title: string) => void; onDelete: () => void }) {
  const platformActive = Boolean(product.activePlatforms?.length)
  return <article className="product-catalog-item color-variant-item">
      <div className="product-catalog-row">
        <input className="product-row-select" type="checkbox" aria-label={`${product.title} seç`} checked={selected} onChange={onSelect} />
        {product.primaryImageUrl ? <img src={product.primaryImageUrl} alt={product.title} className="product-list-thumb clickable-thumb" onClick={() => onImageClick(product.primaryImageUrl!, product.title)} title="Görseli büyütmek için tıklayın" /> : <span className="product-list-placeholder">Görsel yok</span>}
        <div className="product-list-identity"><strong>{product.title}</strong><small>Model Kodu: <code className="technical-text model-code-value">{product.modelCode ?? '—'}</code></small></div>
        <strong className="product-list-variants">{product.variants.length} varyant</strong>
        <div className="product-list-price clickable-cell" title="Fiyatı hızlı güncellemek için tıklayın" onClick={() => onQuickEdit('price')}><strong>{money(product.startingPrice, product.currency)}</strong></div>
        <div className="product-list-stock clickable-cell" title="Stoğu hızlı güncellemek için tıklayın" onClick={() => onQuickEdit('stock')}><strong>{product.totalStock}</strong></div>
        <div className="product-list-platforms"><span className={`platform-state-icon${platformActive ? ' active' : ''}`} title={platformActive ? 'Platformla eşleşti' : 'Platformla eşleşmedi'}>TY<i /></span><small>{platformActive ? 'Eşleşti' : 'Eşleşmedi'}</small></div>
        <div className={`product-list-status ${product.status === 'ACTIVE' ? 'active' : 'inactive'}`}><Tag>{product.status === 'ACTIVE' ? 'Satışta' : 'Kapalı'}</Tag><small>{product.status === 'ACTIVE' ? 'Aktif' : product.status === 'ARCHIVED' ? 'Pasif' : 'Taslak'}</small></div>
        <div className="product-list-actions"><Link className="product-edit-link" to={`/products/${product.id}`} aria-label={`${product.title} ürününü düzenle`} title="Ürünü düzenle"><span className="product-edit-icon" aria-hidden="true">✎</span></Link><button type="button" className="product-delete-button" onClick={event => { event.stopPropagation(); onDelete() }} aria-label={`${product.title} ürününü sil`} title="Ürünü sil"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16m-10 0V4h4v3m-7 0 1 13h8l1-13m-5 4v6m4-6v6" /></svg></button><span className="product-more-icon" aria-hidden="true">⋮</span></div>
      </div>
    </article>
}

export function ProductsPage() {
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [searchFilter, setSearchFilter] = useState(''); const [status, setStatus] = useState(''); const [platform, setPlatform] = useState(''); const [stock, setStock] = useState(''); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [selectedProductCache, setSelectedProductCache] = useState<Record<string, Product>>({}); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null); const [productToast, setProductToast] = useState<{ message: string; kind: 'success' | 'error' } | null>(null); const [bulkOpen, setBulkOpen] = useState(false); const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [pageCursors, setPageCursors] = useState<Record<string, Record<number, string | null>>>({})
  const productFilters = useMemo<ProductListFilters>(() => ({ search: searchFilter, status, platform, stock }), [searchFilter, status, platform, stock])
  const productFilterKey = JSON.stringify(productFilters)
  const pageCursor = pageCursors[productFilterKey]?.[pageNumber] ?? null
  const query = useQuery({
    queryKey: ['products', productFilters, pageSize, pageNumber, pageCursor],
    queryFn: () => fetchProductPage(pageSize, productFilters, pageCursor),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    refetchOnWindowFocus: true
  })
  const summaryQuery = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<ProductSummary>('/products/summary'), staleTime: 30_000, refetchOnWindowFocus: true })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => loadAllPages<TrendyolConnection>('/connections') })
  const products = query.data?.items ?? []; const connections = (connectionsQuery.data?.items ?? []).filter(item => item.status === 'ACTIVE'); const platforms = summaryQuery.data?.platforms ?? []
  const totalCount = query.data?.totalCount ?? products.length; const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageProducts = currentPage === pageNumber ? products : []
  const selectedProducts = selectedProductIds.map(id => selectedProductCache[id]).filter((product): product is Product => Boolean(product))
  const nextPageCursor = query.data?.nextCursor ?? null
  useEffect(() => { const timer = window.setTimeout(() => setSearchFilter(search.trim()), 250); return () => window.clearTimeout(timer) }, [search])
  useEffect(() => { setPageNumber(1); setPageCursors({}) }, [productFilterKey, pageSize])
  useEffect(() => {
    if (query.data?.totalCount == null) return
    const nextTotalPages = Math.max(1, Math.ceil(query.data.totalCount / pageSize))
    setPageNumber(value => value > nextTotalPages ? nextTotalPages : value)
  }, [pageSize, query.data?.totalCount])
  useEffect(() => {
    if (query.isPlaceholderData || !nextPageCursor || !query.data?.hasMore) return
    const nextPage = currentPage + 1
    setPageCursors(current => current[productFilterKey]?.[nextPage] === nextPageCursor ? current : { ...current, [productFilterKey]: { ...(current[productFilterKey] ?? {}), [nextPage]: nextPageCursor } })
    void client.prefetchQuery({ queryKey: ['products', productFilters, pageSize, nextPage, nextPageCursor], queryFn: () => fetchProductPage(pageSize, productFilters, nextPageCursor), staleTime: 30_000 })
  }, [client, currentPage, nextPageCursor, pageSize, productFilterKey, productFilters, query.data?.hasMore, query.isPlaceholderData])
  useEffect(() => {
    if (!products.length || !selectedProductIds.length) return
    setSelectedProductCache(current => {
      let changed = false; const next = { ...current }
      for (const product of products) if (selectedProductIds.includes(product.id) && next[product.id] !== product) { next[product.id] = product; changed = true }
      return changed ? next : current
    })
  }, [products, selectedProductIds])
  const allVisibleSelected = pageProducts.length > 0 && pageProducts.every(product => selectedProductIds.includes(product.id))
  const refresh = () => client.invalidateQueries({ queryKey: ['products'] })
  function showProductToast(message: string, kind: 'success' | 'error') { setProductToast({ message, kind }); window.setTimeout(() => setProductToast(current => current?.message === message ? null : current), 4000) }
  function toggleProduct(product: Product) {
    const wasSelected = selectedProductIds.includes(product.id)
    setSelectedProductIds(ids => wasSelected ? ids.filter(item => item !== product.id) : [...ids, product.id])
    setSelectedProductCache(current => {
      if (!wasSelected) return { ...current, [product.id]: product }
      const next = { ...current }; delete next[product.id]; return next
    })
  }
  function toggleAllVisible() {
    const pageIds = new Set(pageProducts.map(product => product.id))
    if (allVisibleSelected) {
      setSelectedProductIds(ids => ids.filter(id => !pageIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; pageIds.forEach(id => delete next[id]); return next })
    } else {
      setSelectedProductIds(ids => [...new Set([...ids, ...pageProducts.map(product => product.id)])])
      setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(pageProducts.map(product => [product.id, product])) }))
    }
  }
  function goToNextPage() {
    if (!nextPageCursor || currentPage >= totalPages) return
    const nextPage = currentPage + 1
    setPageCursors(current => current[productFilterKey]?.[nextPage] === nextPageCursor ? current : { ...current, [productFilterKey]: { ...(current[productFilterKey] ?? {}), [nextPage]: nextPageCursor } })
    setPageNumber(nextPage)
  }

  async function bulkSetProductStatus(newStatus: 'ACTIVE' | 'ARCHIVED') {
    setBulkOpen(false)
    const targetCount = selectedProductIds.length
    if (!targetCount) return
    try {
      await hubApi('/products/bulk-status', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productIds: selectedProductIds, status: newStatus }) })
      showProductToast(`${targetCount} ürün durumu “${newStatus === 'ACTIVE' ? 'Satışta' : 'Kapalı'}” olarak güncellendi.`, 'success')
      setSelectedProductIds([])
      setSelectedProductCache({})
      await refresh()
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Toplu durum güncelleme başarısız.', 'error')
    }
  }

  async function deleteProduct(product: Product) {
    if (!window.confirm(`“${product.title}” ürünü yerel katalogdan kalıcı olarak silinsin mi?\n\nBu işlem geri alınamaz. Sipariş geçmişi korunur; ürünün yerel varyant, stok, fiyat ve platform eşleşme kayıtları silinir. Marketplace üzerindeki ilan otomatik olarak silinmez.`)) return
    try {
      await hubApi(`/products/${product.id}`, { method: 'DELETE', headers: { 'If-Match': `\"v${product.version}\"`, 'Idempotency-Key': key() } })
      showProductToast('Ürün yerel katalogdan silindi.', 'success')
      setSelectedProductIds(ids => ids.filter(id => id !== product.id))
      setSelectedProductCache(current => { const next = { ...current }; delete next[product.id]; return next })
      await refresh()
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Ürün silme başarısız.', 'error')
    }
  }

  async function bulkDeleteProducts() {
    setBulkOpen(false)
    const targetCount = selectedProductIds.length
    if (!targetCount || !window.confirm(`${targetCount} ürün yerel katalogdan kalıcı olarak silinsin mi?\n\nBu işlem geri alınamaz. Sipariş geçmişi korunur; seçili ürünlerin yerel varyant, stok, fiyat ve platform eşleşme kayıtları silinir. Marketplace üzerindeki ilanlar otomatik olarak silinmez.`)) return
    try {
      await hubApi('/products/bulk-delete', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productIds: selectedProductIds }) })
      showProductToast(`${targetCount} ürün yerel katalogdan silindi.`, 'success')
      setSelectedProductIds([])
      setSelectedProductCache({})
      await refresh()
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Toplu ürün silme başarısız.', 'error')
    }
  }

  return <Page className="products-page" title="Ürünler" eyebrow="Katalog" action={<Link className="button-link" to="/products/new"><span aria-hidden="true">＋</span> Yeni Ürün Ekle</Link>}>
    <div className="product-metrics metrics"><article className="product-metric-total"><small>Toplam Ürün</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.totalCount ?? 0}</strong><span>katalog kaydı</span></article><article className="product-metric-active"><small>Aktif Ürün</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.activeCount ?? 0}</strong><span>ürün</span></article><article className="product-metric-empty"><small>Stoksuz Ürün</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.outOfStockCount ?? 0}</strong><span>aksiyon gerekli</span></article><article className="product-metric-low"><small>Düşük Stoklu</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.lowStockCount ?? 0}</strong><span>5 ve altı</span></article></div>
    <div className="product-toolbar">
      <div className="bulk-menu-shell">
        <button type="button" className="bulk-action" aria-expanded={bulkOpen} aria-controls="products-bulk-action-menu" onClick={() => setBulkOpen(v => !v)}>
          Toplu işlemler {selectedProductIds.length > 0 ? `(${selectedProductIds.length})` : ''} ⌄
        </button>
        {bulkOpen && (
          <div id="products-bulk-action-menu" className="bulk-action-menu" role="menu">
            {!selectedProductIds.length && <div className="bulk-action-empty" role="status">İşlem yapmak için önce en az bir ürün seçin.</div>}
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'both' }); }}>
              <b>01</b><span>Toplu Fiyat &amp; Stok Düzenle<small>Seçili ürünler</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'price' }); }}>
              <b>02</b><span>Toplu Fiyat Düzenle<small>Fiyatları tek seferde güncelle</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'stock' }); }}>
              <b>03</b><span>Toplu Stok Düzenle<small>Stokları artır / azalt / eşitle</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => void bulkSetProductStatus('ACTIVE')}>
              <b>04</b><span>Toplu Satışa Aç<small>Seçili ürünleri satışta yap</small></span>
            </button>
            <button type="button" role="menuitem" className="destructive" disabled={!selectedProductIds.length} onClick={() => void bulkSetProductStatus('ARCHIVED')}>
              <b>05</b><span>Toplu Satışa Kapat<small>Seçili ürünleri arşive al</small></span>
            </button>
            <button type="button" role="menuitem" className="destructive" disabled={!selectedProductIds.length} onClick={() => void bulkDeleteProducts()}>
              <b>06</b><span>Seçili Ürünleri Sil<small>Yerel katalogdan kalıcı olarak kaldır</small></span>
            </button>
          </div>
        )}
      </div>
      <label className="order-search"><span aria-hidden="true">⌕</span><input aria-label="Ürün ara" placeholder="SKU veya Barkod Ara..." value={search} onChange={event => setSearch(event.target.value)} /></label>
      <select aria-label="Ürün durumu" value={status} onChange={event => setStatus(event.target.value)}><option value="">Tüm Durumlar</option><option value="ACTIVE">Aktif</option><option value="DRAFT">Taslak</option><option value="ARCHIVED">Arşiv</option></select>
      <select aria-label="Platform filtresi" value={platform} onChange={event => setPlatform(event.target.value)}><option value="">Platform Durumu</option>{platforms.map(item => <option key={item}>{item}</option>)}</select>
      <select aria-label="Stok filtresi" value={stock} onChange={event => setStock(event.target.value)}><option value="">Stok Durumu</option><option value="OUT">Stoksuz</option><option value="LOW">Düşük stok</option><option value="OK">Yeterli stok</option></select>
    </div>
    <ErrorBox error={query.error ?? summaryQuery.error ?? connectionsQuery.error} />
    {query.isLoading && !pageProducts.length ? <p>Yükleniyor…</p> : !pageProducts.length ? <div className="empty">Filtrelerle eşleşen ürün yok.</div> : (
      <div className="product-catalog-table preferred-product-catalog">
        <div className="product-catalog-head">
          <label className="product-select-all"><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} /><span>Ürün Detayı</span></label>
          <span>Varyant</span><span>Fiyat</span><span>Stok</span><span>Platform Durumu</span><span>Durum</span><span>İşlem</span>
        </div>
        {pageProducts.map(product => (
          <ProductColorRows key={product.id} product={product} selected={selectedProductIds.includes(product.id)} onSelect={() => toggleProduct(product)} onQuickEdit={mode => setQuickEdit({ productIds: [product.id], mode })} onImageClick={(url, title) => setLightboxImage({ url, title })} onDelete={() => void deleteProduct(product)} />
        ))}
      </div>
    )}
    {totalCount > 0 && <div className="order-pagination"><label>Sayfa başına <select aria-label="Sayfa başına ürün" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100].map(value => <option key={value} value={value}>{value}</option>)}</select> ürün</label><span>Toplam {totalCount.toLocaleString('tr-TR')} kayıttan {(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, totalCount)} arası gösteriliyor</span><div className="product-pagination-controls"><button type="button" aria-label="Önceki sayfa" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>‹</button><b>Sayfa {currentPage} / {totalPages}</b><button type="button" aria-label="Sonraki sayfa" disabled={currentPage >= totalPages || !nextPageCursor} onClick={goToNextPage}>›</button></div></div>}
    {quickEdit && <ProductQuickEditModal products={selectedProducts} connections={connections} mode={quickEdit.mode} onChanged={refresh} onResult={showProductToast} onClose={() => setQuickEdit(null)} />}
    {productToast && <div className={`product-operation-toast ${productToast.kind}`} role={productToast.kind === 'success' ? 'status' : 'alert'}><strong>{productToast.kind === 'success' ? 'Güncellendi' : 'Başarısız'}</strong><span>{productToast.message}</span></div>}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
  </Page>
}

type CategoryRequirement = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; attribute: Attribute }
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
  mediaRef: string
}
type ProductAttributePayload = { attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
const MAX_VARIANTS = 1000
const MAX_PRODUCT_ATTRIBUTES = 3
const MAX_PRODUCT_MEDIA_BYTES = 6 * 1024 * 1024

function RichTextTool({ icon, label, onClick, disabled = false, iconClassName = '' }: { icon: string; label: string; onClick: () => void; disabled?: boolean; iconClassName?: string }) {
  return <button type="button" className="rich-text-tool" title={label} aria-label={label} onClick={onClick} disabled={disabled}><span className={`rich-text-tool-icon ${iconClassName}`} aria-hidden="true">{icon}</span><span className="rich-text-tool-label">{label}</span></button>
}

function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const visualEditor = useRef<HTMLDivElement>(null)
  const lastValue = useRef(value)
  const [htmlMode, setHtmlMode] = useState(false)
  const plainTextLength = value.replace(/<[^>]*>/g, '').trim().length

  useEffect(() => {
    if (!htmlMode && visualEditor.current && lastValue.current !== value) visualEditor.current.innerHTML = value
    lastValue.current = value
  }, [htmlMode, value])

  useEffect(() => {
    if (!htmlMode && visualEditor.current) visualEditor.current.innerHTML = value
  }, [htmlMode])

  function syncVisualValue() {
    const next = visualEditor.current?.innerHTML ?? ''
    lastValue.current = next
    onChange(next === '<br>' ? '' : next)
  }

  function runCommand(command: string, argument?: string) {
    if (htmlMode || !visualEditor.current) return
    visualEditor.current.focus()
    document.execCommand(command, false, argument)
    syncVisualValue()
  }

  function insertLink() {
    const url = window.prompt('Bağlantı adresi', 'https://')
    if (url) runCommand('createLink', url)
  }

  function insertImage() {
    const url = window.prompt('Görsel adresi', 'https://')
    if (url) runCommand('insertImage', url)
  }

  function clearFormatting() {
    runCommand('removeFormat')
    runCommand('formatBlock', 'p')
  }

  return <div className="rich-text-editor rich-text-editor-pro">
    <div className="rich-text-editor-head">
      <div><strong>Ürün Açıklaması</strong><span className="rich-text-mode-note">{htmlMode ? 'HTML kodu düzenleniyor' : 'Görsel düzenleyici'}</span></div>
      <div className="rich-text-editor-meta"><span>{plainTextLength} karakter</span><div className="rich-text-mode-switch" role="group" aria-label="Açıklama görünümü"><button type="button" className={!htmlMode ? 'active' : ''} aria-pressed={!htmlMode} onClick={() => setHtmlMode(false)}><span aria-hidden="true">✦</span> Görsel</button><button type="button" className={htmlMode ? 'active' : ''} aria-pressed={htmlMode} onClick={() => setHtmlMode(true)}><span aria-hidden="true">&lt;/&gt;</span> HTML</button></div></div>
    </div>
    <div className="rich-text-toolbar" aria-label="Açıklama biçimlendirme araçları">
      <div className="rich-text-tool-group" aria-label="Metin biçimi"><RichTextTool icon="B" label="Kalın" onClick={() => runCommand('bold')} disabled={htmlMode} /><RichTextTool icon="I" label="İtalik" onClick={() => runCommand('italic')} disabled={htmlMode} /><RichTextTool icon="U" label="Altı çizili" onClick={() => runCommand('underline')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group rich-text-alignment-group" aria-label="Paragraf hizası"><RichTextTool icon="" iconClassName="alignment-icon alignment-left" label="Sola hizala" onClick={() => runCommand('justifyLeft')} disabled={htmlMode} /><RichTextTool icon="" iconClassName="alignment-icon alignment-center" label="Ortala" onClick={() => runCommand('justifyCenter')} disabled={htmlMode} /><RichTextTool icon="" iconClassName="alignment-icon alignment-right" label="Sağa hizala" onClick={() => runCommand('justifyRight')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="İçerik ekle"><select aria-label="Yazı boyutu" defaultValue="" disabled={htmlMode} onChange={event => { const map: Record<string, string> = { '12': '2', '15': '3', '19': '5' }; runCommand('fontSize', map[event.target.value] ?? '3'); event.currentTarget.value = '' }}><option value="" disabled>Yazı boyutu</option><option value="12">Küçük</option><option value="15">Normal</option><option value="19">Büyük</option></select><RichTextTool icon="•≡" label="Madde listesi" onClick={() => runCommand('insertUnorderedList')} disabled={htmlMode} /><RichTextTool icon="¶" label="Paragraf" onClick={() => runCommand('formatBlock', 'p')} disabled={htmlMode} /><RichTextTool icon="A̲" label="Metin rengi" onClick={() => runCommand('foreColor', '#bec2ff')} disabled={htmlMode} /><RichTextTool icon="↗" label="Bağlantı ekle" onClick={insertLink} disabled={htmlMode} /><RichTextTool icon="▧" label="Görsel ekle" onClick={insertImage} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="Düzenleme"><RichTextTool icon="↶" label="Geri al" onClick={() => runCommand('undo')} disabled={htmlMode} /><RichTextTool icon="↷" label="Yinele" onClick={() => runCommand('redo')} disabled={htmlMode} /><RichTextTool icon="⌫" label="Biçimi temizle" onClick={clearFormatting} disabled={htmlMode} /></div>
    </div>
    {htmlMode ? <textarea className="rich-text-html-editor" value={value} onChange={event => { lastValue.current = event.target.value; onChange(event.target.value) }} aria-label="Açıklama HTML kodu" placeholder="<p>Ürünün öne çıkan özelliklerini anlatın…</p>" spellCheck={false} /> : <div ref={visualEditor} className="rich-text-canvas" contentEditable role="textbox" aria-multiline="true" aria-label="Açıklama" data-placeholder="Ürünün öne çıkan özelliklerini anlatın…" suppressContentEditableWarning dangerouslySetInnerHTML={{ __html: value }} onInput={syncVisualValue} />}
    <div className="rich-text-editor-foot"><span>{htmlMode ? 'HTML olarak düzenleyin; Görsel seçeneğine dönünce biçimlendirilmiş çıktı burada görünür.' : 'Metni biçimlendirin veya HTML seçeneğiyle kaynak kodunu düzenleyin.'}</span><b aria-hidden="true">⌘</b></div>
  </div>
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
    listPrice: fallbackListPrice || fallbackSalePrice,
    mediaRef: ''
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

function CategoryAttributeMappingPanel({
  categoryId,
  requirements,
  isLoading,
  isError,
  attributeSelections,
  attributeTextValues,
  onToggleValue,
  onTextChange
}: {
  categoryId: string
  requirements: CategoryRequirement[]
  isLoading: boolean
  isError: boolean
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  onToggleValue: (attributeId: string, valueId: string) => void
  onTextChange: (attributeId: string, value: string) => void
}) {
  const attributes = requirements.filter(item => item.role === 'ATTRIBUTE')
  const dataTypeLabels: Record<string, string> = {
    SINGLE_SELECT: 'Tek seçim',
    MULTI_SELECT: 'Çoklu seçim',
    BOOLEAN: 'Evet / hayır',
    NUMBER: 'Sayısal değer',
    TEXT: 'Metin'
  }

  return <section className="panel product-step-card product-category-mapping-card">
    <div className="editor-section-title">
      <span>3</span>
      <div>
        <h2>Kategori özellikleri</h2>
        <p>Eşleştirme ayarlarında oluşturulan başlıklara bu ürünün değerlerini atayın.</p>
      </div>
    </div>
    <p className="category-mapping-note"><span aria-hidden="true">↗</span> Yayınlama sırasında seçtiğiniz değerler ilgili pazaryerinin formatına dönüştürülür.</p>
    {!categoryId ? (
      <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde eşlenmiş özellik başlıkları burada görünür.</p></div>
    ) : isLoading ? (
      <p>Kategori özellikleri yükleniyor…</p>
    ) : isError ? (
      <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Özellik başlıklarını Kategori Eşleştirme ekranında hazırlayın.</p></div>
    ) : attributes.length ? (
      <div className="category-attribute-mapping-list">
        {attributes.map(item => {
          const selectedValues = attributeSelections[item.attributeId] ?? []
          return <article className={`category-attribute-field ${item.isRequired ? 'required' : ''}`} key={item.attributeId}>
            <div className="category-attribute-field-head">
              <div>
                <strong>{item.attribute.name}{item.isRequired ? ' *' : ''}</strong>
                <small>{dataTypeLabels[item.attribute.dataType] ?? item.attribute.dataType}{item.isRequired ? ' · Zorunlu' : ' · İsteğe bağlı'}</small>
              </div>
              {selectedValues.length > 0 && <span className="category-attribute-count">{selectedValues.length} seçildi</span>}
            </div>
            {item.attribute.values.length ? (
              <div className="category-attribute-values">
                {item.attribute.values.map(value => <button type="button" key={value.id} className={`category-attribute-value ${selectedValues.includes(value.id) ? 'active' : ''}`} onClick={() => onToggleValue(item.attributeId, value.id)}>{value.value}</button>)}
              </div>
            ) : item.attribute.dataType === 'BOOLEAN' ? (
              <select aria-label={`${item.attribute.name} değeri`} value={attributeTextValues[item.attributeId] ?? ''} onChange={event => onTextChange(item.attributeId, event.target.value)}>
                <option value="">Değer seçin</option>
                <option value="evet">Evet</option>
                <option value="hayır">Hayır</option>
              </select>
            ) : (
              <input aria-label={`${item.attribute.name} değeri`} value={attributeTextValues[item.attributeId] ?? ''} onChange={event => onTextChange(item.attributeId, event.target.value)} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder={item.allowsCustomValue ? 'Değer girin' : 'Özellik değerini girin'} />
            )}
          </article>
        })}
      </div>
    ) : (
      <div className="empty small"><strong>Bu kategori için özellik başlığı yok</strong><p>Kategori Eşleştirme ekranından önce özellik başlıklarını oluşturun.</p></div>
    )}
  </section>
}

export function NewProductPage({ editProductId }: { editProductId?: string } = {}) {
  const client = useQueryClient();
  const [error, setError] = useState<unknown>(); const [created, setCreated] = useState<Product>(); const [, setNotice] = useState(''); const [feedback, setFeedback] = useState<OperationFeedback | null>(null); const [submitting, setSubmitting] = useState(false); const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', listPrice: '699.90', salePrice: '549.90', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '2', mediaUrls: '', status: 'ACTIVE' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([])
  const initializedEditProductKey = useRef<string | null>(null)
  const [wizardStep, setWizardStep] = useState<1 | 2>(1)
  const [scheduledPublishOpen, setScheduledPublishOpen] = useState(false)
  const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null)
  const [variantMediaModal, setVariantMediaModal] = useState<{ mode: 'variant' | 'bulk'; rowKey?: string; draftRef: string; groupId: string; valueId: string } | null>(null)
  const [expandedOptionGroupIds, setExpandedOptionGroupIds] = useState<Record<string, boolean>>({})
  const [bulkStock, setBulkStock] = useState(''); const [bulkSalePrice, setBulkSalePrice] = useState(''); const [bulkListPrice, setBulkListPrice] = useState('')
  const [mediaFiles, setMediaFiles] = useState<File[]>([])
  const [mediaUrlSettingsOpen, setMediaUrlSettingsOpen] = useState(false)
  const feedbackTimer = useRef<number | null>(null)
  const initialEditMediaUrl = useRef('')
  function showFeedback(message: string, kind: OperationFeedback['kind']) {
    if (feedbackTimer.current !== null) window.clearTimeout(feedbackTimer.current)
    setFeedback({ message, kind })
    feedbackTimer.current = window.setTimeout(() => setFeedback(null), kind === 'info' ? 7000 : 5500)
  }
  function handleMediaFiles(files: File[]) {
    const accepted: File[] = []
    const rejected: string[] = []
    for (const file of files) {
      const isImage = file.type === 'image/jpeg' || file.type === 'image/png'
      if (!isImage) { rejected.push(`${file.name}: yalnız JPEG veya PNG kabul edilir.`); continue }
      if (file.size <= 0 || file.size > MAX_PRODUCT_MEDIA_BYTES) { rejected.push(`${file.name}: dosya başına en fazla 6 MB olabilir.`); continue }
      accepted.push(file)
    }
    if (accepted.length) setMediaFiles(current => [...current, ...accepted])
    if (rejected.length) {
      const message = rejected.length === 1 ? rejected[0] : `${rejected.length} görsel eklenemedi. Dosya türü ve 6 MB sınırını kontrol edin.`
      setNotice(message); showFeedback(message, 'error')
    } else if (accepted.length) {
      const message = `${accepted.length} görsel seçildi.`; setNotice(message); showFeedback(message, 'info')
    }
  }
  useEffect(() => () => { if (feedbackTimer.current !== null) window.clearTimeout(feedbackTimer.current) }, [])
  const productToEdit = useQuery({ queryKey: ['product', editProductId], queryFn: () => hubApi<Product>(`/products/${editProductId}`), enabled: !!editProductId })
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => loadAllPages<TrendyolConnection>('/connections') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const initialStock = Number(form.initialStock || 0)
  const desi = useMemo(() => { const width = Number(form.width); const length = Number(form.length); const height = Number(form.height); return width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0 }, [form.width, form.length, form.height])
  const mediaUrls = useMemo(() => form.mediaUrls.split(/\r?\n/).map(item => item.trim()).filter(Boolean), [form.mediaUrls])

  const allRequirements = useMemo(() => (requirements.data ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder), [requirements.data])
  const optionRequirements = useMemo(() => allRequirements.filter(item => item.role === 'OPTION').slice(0, 2), [allRequirements])
  useEffect(() => {
    const optionIds = optionRequirements.map(item => item.attributeId)
    setVariantAttributeIds(current => current.filter(id => optionIds.includes(id)))
  }, [optionRequirements])
  const visibleOptionRequirements = optionRequirements

  useEffect(() => {
    const product = productToEdit.data
    if (!product) return
    const productKey = `${product.id}:${product.version}`
    if (initializedEditProductKey.current === productKey) return
    initializedEditProductKey.current = productKey
    const primary = product.variants[0]
    const savedMediaUrls = product.mediaUrls?.length ? product.mediaUrls : (product.primaryImageUrl ? [product.primaryImageUrl] : [])
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: primary?.sku ?? '', barcode: primary?.barcode ?? '', modelCode: primary?.modelCode ?? product.modelCode ?? '', weight: String(primary?.weight ?? ''), width: String(primary?.width ?? ''), length: String(primary?.length ?? ''), height: String(primary?.height ?? ''), desi: String(primary?.desi ?? 1), listPrice: String(primary?.listPrice ?? primary?.salePrice ?? 0), salePrice: String(primary?.salePrice ?? 0), currency: primary?.currency ?? 'TRY', vatRate: String(primary?.vatRate ?? 10), vatIncluded: primary?.vatInclusion ?? 'INCLUDED', initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: savedMediaUrls.join('\n'), status: product.status || 'ACTIVE' })
    initialEditMediaUrl.current = savedMediaUrls.join('\n')
    setMediaFiles([])
    setVariantRows(product.variants.map(variant => ({ key: variant.id, optionSignature: variant.optionSignature || 'Tek Ürün', options: {}, attributeValueIds: {}, sku: variant.sku, barcode: variant.barcode ?? '', stock: variant.onHand, salePrice: variant.salePrice ?? 0, listPrice: variant.listPrice ?? variant.salePrice ?? 0, mediaRef: variant.mediaUrls?.[0] ? `url|${variant.mediaUrls[0]}` : '' })))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed); setVariantAttributeIds([])
  }, [productToEdit.data?.id, productToEdit.data?.version])

  useEffect(() => {
    // Saved option groups describe the product's existing variants; they are
    // not an active selection for the next variant-generation action. Keep
    // option values available, but make the user opt in explicitly.
    if (!allRequirements.length) return
    const optionIds = new Set(allRequirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
    setAttributeSelections(current => {
      const next = { ...current }
      let changed = false
      for (const attributeId of optionIds) {
        if (next[attributeId]?.length) {
          delete next[attributeId]
          changed = true
        }
      }
      return changed ? next : current
    })
    setVariantAttributeIds(current => current.filter(id => !optionIds.has(id)))
  }, [allRequirements])

  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = allRequirements.find(item => item.attributeId === attributeId)
    const alreadySelected = (attributeSelections[attributeId] ?? []).includes(valueId)
    if (!alreadySelected && requirement?.role === 'OPTION' && !variantAttributeIds.includes(attributeId) && variantAttributeIds.length >= 2) {
      const message = 'Bir ürün en fazla 2 seçenek grubuyla varyantlanabilir.'; setNotice(message); showFeedback(message, 'error'); return
    }
    setAttributeSelections(current => {
      const values = current[attributeId] ?? []
      const nextValues = values.includes(valueId) ? values.filter(item => item !== valueId) : requirement?.role === 'ATTRIBUTE' && requirement.attribute.dataType === 'SINGLE_SELECT' ? [valueId] : [...values, valueId]
      if (requirement?.role === 'OPTION') {
        setVariantAttributeIds(currentAxes => nextValues.length ? currentAxes.includes(attributeId) ? currentAxes : [...currentAxes, attributeId] : currentAxes.filter(id => id !== attributeId))
      }
      if (values.includes(valueId)) return { ...current, [attributeId]: nextValues }
      const selectedOptionalAttributeCount = allRequirements.filter(item => item.role === 'ATTRIBUTE' && !item.isRequired && (current[item.attributeId]?.length ?? 0) > 0).length
      if (requirement?.role === 'ATTRIBUTE' && !requirement.isRequired && values.length === 0 && selectedOptionalAttributeCount >= MAX_PRODUCT_ATTRIBUTES) { const message = `Bir üründe en fazla ${MAX_PRODUCT_ATTRIBUTES} isteğe bağlı ürün özelliği kullanılabilir.`; setNotice(message); showFeedback(message, 'error'); return current }
      return { ...current, [attributeId]: nextValues }
    })
  }
  function generateVariants() {
    try {
      const generated = buildVariantMatrix(requirements.data ?? [], variantAttributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, initialStock)
      if (!generated.length) {
        const message = 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.'; setNotice(message); showFeedback(message, 'error')
        return
      }
      setVariantRows(current => {
        const existingMap = new Map(current.map(row => [row.optionSignature, row]))
        const merged = generated.map(gen => {
          const match = existingMap.get(gen.optionSignature)
          if (match) {
            existingMap.delete(gen.optionSignature)
            return match
          }
          return gen
        })
        return [...merged, ...Array.from(existingMap.values())]
      })
      const message = `${generated.length} varyant satırı hazırlandı.`; setNotice(message); showFeedback(message, 'success')
    } catch (reason) { const message = reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.'; setNotice(message); showFeedback(message, 'error') }
  }
  function clearVariants() { setVariantRows([]); const message = 'Oluşan varyant satırları temizlendi.'; setNotice(message); showFeedback(message, 'success') }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'stock' || field === 'salePrice' || field === 'listPrice' ? Number(value || 0) : value })) }
  function updateVariantMedia(keyValue: string, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, mediaRef: value })) }
  function swapVariants(sourceKey: string, targetKey: string) {
    if (sourceKey === targetKey) return
    setVariantRows(rows => {
      const sourceIndex = rows.findIndex(row => row.key === sourceKey); const targetIndex = rows.findIndex(row => row.key === targetKey)
      if (sourceIndex < 0 || targetIndex < 0) return rows
      const next = [...rows]; [next[sourceIndex], next[targetIndex]] = [next[targetIndex], next[sourceIndex]]
      return next
    })
  }
  function updateChannel(id: string) {
    const selected = selectedChannelIds.includes(id)
    setSelectedChannelIds(current => selected ? current.filter(item => item !== id) : [...current, id])
    showFeedback(selected ? 'Yayın kanalı seçimden çıkarıldı.' : 'Yayın kanalı seçildi.', 'info')
  }
  function applyBulk() {
    const stock = bulkStock === '' ? null : Number(bulkStock); const sale = bulkSalePrice === '' ? null : Number(bulkSalePrice); const list = bulkListPrice === '' ? null : Number(bulkListPrice)
    setVariantRows(rows => rows.map(row => ({ ...row, stock: stock == null || !Number.isFinite(stock) ? row.stock : stock, salePrice: sale == null || !Number.isFinite(sale) ? row.salePrice : sale, listPrice: list == null || !Number.isFinite(list) ? row.listPrice : list })))
    const message = 'Toplu stok ve fiyat değerleri varyantlara uygulandı.'; setNotice(message); showFeedback(message, 'success')
  }

  function openVariantMediaPicker(rowKey: string) {
    const row = variantRows.find(item => item.key === rowKey)
    setVariantMediaModal({ mode: 'variant', rowKey, draftRef: row?.mediaRef ?? '', groupId: '', valueId: '' })
  }
  function openBulkVariantMediaPicker() {
    const groups = bulkMediaGroups
    if (!groups.length) {
      const message = 'Önce seçenek grubu ve en az bir seçenek değeri seçin.'
      setNotice(message); showFeedback(message, 'error')
      return
    }
    const group = groups[0]
    setVariantMediaModal({ mode: 'bulk', draftRef: '', groupId: group.id, valueId: group.values[0]?.id ?? '' })
  }
  function rowOptionValue(row: VariantDraft, group: Pick<VariantMediaGroup, 'name'>) {
    const direct = row.options[group.name]
    if (direct) return direct
    return parseVariantOptionSignature(row.optionSignature).find(option => option.name.toLocaleLowerCase('tr-TR') === group.name.trim().toLocaleLowerCase('tr-TR'))?.value ?? ''
  }
  function rowMatchesVariantMediaValue(row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) {
    return Boolean((group.attributeId && row.attributeValueIds[group.attributeId] === value.id) || rowOptionValue(row, group).trim().toLocaleLowerCase('tr-TR') === value.value.trim().toLocaleLowerCase('tr-TR'))
  }
  function applyVariantMediaSelection() {
    if (!variantMediaModal) return
    if (variantMediaModal.mode === 'variant' && variantMediaModal.rowKey) {
      updateVariantMedia(variantMediaModal.rowKey, variantMediaModal.draftRef)
      setVariantMediaModal(null)
      showFeedback(variantMediaModal.draftRef ? 'Varyant görseli seçildi.' : 'Varyant görseli kaldırıldı.', 'success')
      return
    }
    const group = bulkMediaGroups.find(item => item.id === variantMediaModal.groupId)
    const value = group?.values.find(item => item.id === variantMediaModal.valueId)
    if (!group || !value) return
    const matchingRows = variantRows.filter(row => rowMatchesVariantMediaValue(row, group, value))
    if (!matchingRows.length) {
      const message = `${group.name}: ${value.value} seçeneğine bağlı varyant satırı bulunamadı.`
      setNotice(message); showFeedback(message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => rowMatchesVariantMediaValue(row, group, value) ? { ...row, mediaRef: variantMediaModal.draftRef } : row))
    setVariantMediaModal(null)
    const action = variantMediaModal.draftRef ? 'görsel uygulandı' : 'görsel kaldırıldı'
    const message = `${group.name}: ${value.value} seçeneğindeki ${matchingRows.length} varyant satırında ${action}.`
    setNotice(message); showFeedback(message, 'success')
  }

  function rowsForSubmit(requireCompleteCatalog = true) {
    if (requireCompleteCatalog && variantAttributeIds.length && !variantRows.length) throw new Error('Varyant özellikleri seçili. Önce “Ürünleri ekle” ile varyantları oluşturun.')
    if (editProductId && !variantRows.length) return []
    return variantRows.length ? variantRows : [{ key: crypto.randomUUID(), optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: (form.baseSku || form.modelCode || form.title || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR'), barcode: form.barcode, stock: initialStock, salePrice: fallbackSalePrice, listPrice: fallbackListPrice, mediaRef: '' }]
  }
  function validate(rows: VariantDraft[], requireCompleteCatalog = true) {
    const issues: string[] = []; const requirementList = requirements.data ?? []
    if (requireCompleteCatalog && (variantAttributeIds.length > 2 || variantAttributeIds.some(id => requirementList.find(item => item.attributeId === id)?.role !== 'OPTION'))) issues.push('Varyant için en fazla 2 Seçenek Eşitleme başlığı kullanılabilir.')
    const selectedOptionalProductAttributes = requirementList.filter(item => item.role === 'ATTRIBUTE' && !item.isRequired && ((attributeSelections[item.attributeId]?.length ?? 0) > 0 || Boolean((attributeTextValues[item.attributeId] ?? '').trim()))).length
    if (requireCompleteCatalog && selectedOptionalProductAttributes > MAX_PRODUCT_ATTRIBUTES) issues.push(`Bir üründe en fazla ${MAX_PRODUCT_ATTRIBUTES} isteğe bağlı ürün özelliği kullanılabilir.`)
    if (!form.title.trim()) issues.push('Ürün adı zorunludur.')
    if (requireCompleteCatalog && !form.description.trim()) issues.push('Açıklama zorunludur.')
    if (requireCompleteCatalog) {
      for (const requirement of requirementList) {
        const selectedCount = attributeSelections[requirement.attributeId]?.length ?? 0
        if (!variantAttributeIds.includes(requirement.attributeId) && requirement.attribute.dataType === 'SINGLE_SELECT' && selectedCount > 1) issues.push(`${requirement.attribute.name} yalnız bir ürün değeri kabul eder.`)
        if (requirement.role === 'OPTION' || !requirement.isRequired) continue
        if (variantAttributeIds.includes(requirement.attributeId)) {
          if (rows.some(row => !row.attributeValueIds[requirement.attributeId])) issues.push(`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`)
        } else if (!(attributeSelections[requirement.attributeId]?.length) && !(attributeTextValues[requirement.attributeId] ?? '').trim()) issues.push(`${requirement.attribute.name} zorunludur.`)
      }
    }
    if (rows.length > MAX_VARIANTS) issues.push(`En fazla ${MAX_VARIANTS} varyant oluşturulabilir.`)
    const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
    const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
    const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
    if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
     if (!form.desi.trim() || !Number.isFinite(Number(form.desi)) || Number(form.desi) <= 0) issues.push('Desi sıfırdan büyük olmalıdır.')
     if (requireCompleteCatalog && selectedChannelIds.length) {
      if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
      if (!mediaUrls.length && !mediaFiles.length) issues.push('Trendyol yayını için en az bir HTTPS görsel adresi zorunludur.'); if (!mediaUrls.length && mediaFiles.length) issues.push('Yerel dosya katalogda önizleme içindir; Trendyol yayını için en az bir herkese açık HTTPS görsel adresi ekleyin.'); if (mediaUrls.length + mediaFiles.length > 8) issues.push('Trendyol yayını için en fazla 8 görsel kullanılabilir.'); if (mediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
      if (rows.some(row => !row.barcode.trim() || !/^[a-zA-Z0-9._-]+$/.test(row.barcode.trim()))) issues.push('Trendyol yayını için her varyantta geçerli ve benzersiz barkod zorunludur.'); if (rows.some(row => row.salePrice <= 0)) issues.push('Trendyol yayını için satış fiyatı sıfırdan büyük olmalıdır.')
    }
    if (issues.length) throw new Error(issues.join(' '))
  }

  function handleInvalid(event: FormEvent<HTMLFormElement>) {
    const target = event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
    const message = target.validationMessage || 'Lütfen zorunlu alanları doldurun.'
    setError(new Error(message))
    setNotice(message)
    showFeedback(message, 'error')
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const submitter = (event.nativeEvent as SubmitEvent).submitter
    const submitData = new FormData(event.currentTarget)
    const saveAndStay = submitter?.getAttribute('data-submit-intent') === 'save'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'save')
      || submitData.get('intent') === 'save'
    const requireCompleteCatalog = !editProductId || !saveAndStay
    if (wizardStep !== 2 && !saveAndStay) {
      setWizardStep(2)
      return
    }
    setError(undefined); setNotice(''); showFeedback(editProductId ? 'Ürün değişiklikleri kaydediliyor…' : 'Ürün oluşturuluyor…', 'info'); setSubmitting(true); let productCreated: Product | undefined
    try {
      if (requireCompleteCatalog && form.categoryId && requirements.isLoading) throw new Error('Kategori özellikleri yükleniyor. Kaydetmeden önce kısa süre bekleyin.')
      if (requireCompleteCatalog && form.categoryId && requirements.isError) throw new Error('Kategori özellikleri alınamadı. Önce kategori eşleştirmesini kontrol edin.')
      const requirementList = requirements.data ?? []; const rows = rowsForSubmit(requireCompleteCatalog); validate(rows, requireCompleteCatalog)
      const globalAttributes = requirementList.filter(item => !variantAttributeIds.includes(item.attributeId)).flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
      // Do not overwrite existing assignments while a newly selected category's
      // requirements are still loading (or failed). The edit form may be saved
      // without optional mapping data.
      const shouldPersistAttributes = !editProductId || Boolean(form.categoryId && requirements.isSuccess)
      const variantPayload = (row: VariantDraft, index: number) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : Number(form.desi) || 1, options: row.options, attributes: Object.entries(row.attributeValueIds).map(([attributeId, valueId], attributeIndex) => ({ attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: index * 100 + attributeIndex })) })
      const existingVariantIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
      const product = productToEdit.data
        ? await hubApi<Product>(`/products/${productToEdit.data.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${productToEdit.data.version}"` }, body: JSON.stringify({ title: form.title, status: form.status, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, ...(shouldPersistAttributes ? { attributes: globalAttributes } : {}), variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload), variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({ id: row.key, sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null })) }) })
        : await hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ title: form.title, status: form.status, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes: globalAttributes, variants: rows.map(variantPayload) }) })
      productCreated = product; setCreated(product); const completed = ['ürün']; const warnings: string[] = []
      const mediaUrlsToPersist = editProductId && form.mediaUrls.trim() === initialEditMediaUrl.current.trim() ? [] : mediaUrls
      if (editProductId && form.mediaUrls.trim() !== initialEditMediaUrl.current.trim()) await hubApi(`/files/product-media?productId=${encodeURIComponent(product.id)}`, { method: 'DELETE', headers: { 'Idempotency-Key': key() } })
      for (const [index, url] of mediaUrlsToPersist.entries()) await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: null, url, mediaRole: index === 0 ? 'PRIMARY' : 'GALLERY', sortOrder: index, altText: form.title }) })
      for (const [fileIndex, file] of mediaFiles.entries()) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        await hubApi(`/files/product-media-variant?productId=${encodeURIComponent(product.id)}&variantId=${encodeURIComponent(variant.id)}`, { method: 'DELETE', headers: { 'Idempotency-Key': key() } })
        if (row.mediaRef.startsWith('url|')) {
          await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: variant.id, url: row.mediaRef.slice(4), mediaRole: 'PRIMARY', sortOrder: 0, altText: `${form.title} · ${row.optionSignature}` }) })
        } else if (row.mediaRef.startsWith('file|')) {
          const file = mediaFiles[Number(row.mediaRef.slice(5))]
          if (file) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('variantId', variant.id); data.set('mediaRole', 'PRIMARY'); data.set('sortOrder', '0'); data.set('altText', `${form.title} · ${row.optionSignature}`); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
        }
      }
      if (mediaUrls.length || mediaFiles.length) completed.push('görseller')
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
          let listingProfileVersion: number | undefined
          try {
            const listingProfile = await hubApi<{ version: number }>(`/products/${product.id}/listing-profiles/${connectionId}`)
            listingProfileVersion = listingProfile.version
          } catch (reason) {
            if (!(reason instanceof ApiRequestError) || reason.status !== 404) throw reason
          }
          await hubApi(`/products/${product.id}/listing-profiles/${connectionId}`, { method: 'PUT', headers: listingProfileVersion == null ? {} : { 'If-Match': `"v${listingProfileVersion}"` }, body: JSON.stringify({ titleOverride: null, descriptionOverride: null, externalCategoryId: null, externalBrandId: null, deliveryTimeDays: null, enabled: true }) })
          const accepted = await hubApi<AcceptedJob>(`/products/${product.id}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId }) })
          completed.push(`yayın işi ${accepted.jobId}`)
        } catch (reason) { warnings.push(reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.') }
      }
      const message = `${completed.join(', ')} kaydedildi.${warnings.length ? ` Yayın uyarısı: ${warnings.join(' ')}` : ''}`
      setNotice(message); showFeedback(message, warnings.length ? 'info' : 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kayıt tamamlanamadı.'
      const feedbackMessage = productCreated ? `Ürün kaydedildi ancak sonraki işlem tamamlanamadı: ${message}` : message
      setError(reason); setNotice(feedbackMessage); showFeedback(feedbackMessage, 'error')
    } finally { setSubmitting(false) }
  }

  const publishConnections = (connections.data?.items ?? []).filter(isProductPublicationConnection)
  const platformCards = publishConnections.map(connection => ({
    code: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    name: connection.displayName,
    initial: connection.displayName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R',
    tone: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    connection
  }))
  const selectedPublishConnections = publishConnections.filter(item => selectedChannelIds.includes(item.id))
  const assignedMediaUrls = [...new Set(variantRows.flatMap(row => row.mediaRef.startsWith('url|') ? [row.mediaRef.slice(4)] : []))]
  const mediaChoices: ProductMediaOption[] = ([...new Set([...mediaUrls, ...assignedMediaUrls])].map((url, index) => ({ value: `url|${url}`, label: `${index + 1}. ${url}`, url })) as ProductMediaOption[]).concat(mediaFiles.map((file, index) => ({ value: `file|${index}`, label: `Dosya · ${file.name}`, file })))
  const bulkMediaGroups = useMemo<VariantMediaGroup[]>(() => {
    const groups: VariantMediaGroup[] = []
    const names = new Set<string>()
    const addGroup = (group: VariantMediaGroup) => {
      const name = group.name.trim().toLocaleLowerCase('tr-TR')
      if (!group.values.length || names.has(name)) return
      names.add(name)
      groups.push(group)
    }
    // Existing products expose their persisted option groups separately from
    // category requirements. Prefer these IDs/values so imported variants such
    // as “Beden: Tek Ebat” can be targeted even when the current category no
    // longer exposes the original option requirement.
    for (const option of productToEdit.data?.options ?? []) {
      addGroup({ id: `product-option:${option.id}`, name: option.label, values: option.values.map(value => ({ id: value.id, value: value.label })) })
    }
    for (const item of optionRequirements) {
      const selectedIds = attributeSelections[item.attributeId] ?? []
      const values = item.attribute.values
        .filter(value => selectedIds.includes(value.id) || variantRows.some(row => row.attributeValueIds[item.attributeId] === value.id || rowOptionValue(row, { name: item.attribute.name }).trim().toLocaleLowerCase('tr-TR') === value.value.trim().toLocaleLowerCase('tr-TR')))
        .map(value => ({ id: value.id, value: value.value }))
      addGroup({ id: `category-attribute:${item.attributeId}`, name: item.attribute.name, attributeId: item.attributeId, values })
    }
    const signatureValues = new Map<string, { name: string; values: Map<string, string> }>()
    for (const row of variantRows) {
      for (const option of parseVariantOptionSignature(row.optionSignature)) {
        const groupKey = option.name.trim().toLocaleLowerCase('tr-TR')
        const group = signatureValues.get(groupKey) ?? { name: option.name.trim(), values: new Map<string, string>() }
        group.values.set(option.value.trim().toLocaleLowerCase('tr-TR'), option.value.trim())
        signatureValues.set(groupKey, group)
      }
    }
    for (const group of signatureValues.values()) {
      addGroup({ id: `variant-signature:${group.name.toLocaleLowerCase('tr-TR')}`, name: group.name, values: [...group.values].map(([id, value]) => ({ id, value })) })
    }
    return groups
  }, [attributeSelections, optionRequirements, productToEdit.data?.options, variantRows])
  const selectedBulkMediaGroup = variantMediaModal?.mode === 'bulk' ? bulkMediaGroups.find(group => group.id === variantMediaModal.groupId) : undefined
  const selectedBulkMediaValue = selectedBulkMediaGroup?.values.find(value => value.id === variantMediaModal?.valueId)
  const selectedBulkMediaMatchCount = selectedBulkMediaGroup && selectedBulkMediaValue ? variantRows.filter(row => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, selectedBulkMediaValue)).length : 0
  const hasBasicProductData = Boolean(form.title.trim() && form.description.trim() && form.brandId && form.modelCode.trim() && form.barcode.trim())
  const mediaCount = mediaUrls.length + mediaFiles.length
  const hasProductMedia = mediaCount > 0
  const hasVariantData = variantAttributeIds.length === 0 || variantRows.length > 0
  const productChecks = [
    { title: 'Temel Ürün Verileri', detail: hasBasicProductData ? 'İsim, açıklama, marka ve barkod bilgileri eksiksiz.' : 'İsim, açıklama, marka, model veya barkod bilgisi eksik.', ok: hasBasicProductData },
    { title: 'Görsel Kalitesi', detail: hasProductMedia ? `${mediaCount} adet ürün görseli eklendi.` : 'En az bir yüksek çözünürlüklü görsel ekleyin.', ok: hasProductMedia },
    { title: 'Varyant Bilgileri', detail: hasVariantData ? 'Varyant yapısı yayınlanmaya hazır.' : 'Seçilen seçenekler için varyant satırlarını oluşturun.', ok: hasVariantData }
  ]

  return <Page className={`product-add-page${editProductId ? ' product-edit-page' : ''}`} title={editProductId ? "Ürün Düzenle" : "Yeni Ürün Ekle"} eyebrow="Katalog"><p className="lede page-lede">Ürün bilgilerini ve varyantları hazırlayın; yayınlama adımında kanalları seçip gönderim kuyruğunu başlatın.</p><div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-progress" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span>1</span><strong>Ürün bilgileri ve varyantlar</strong></button><i aria-hidden="true" /><button type="button" className={wizardStep === 2 ? 'active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span>2</span><strong>Yayınlama</strong></button></div></div></div><form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} onSubmit={submit} onInvalidCapture={handleInvalid} noValidate>
    <div className="product-top-layout">
      <section className="panel product-step-card product-basics-card">
        <div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün kartının temel başlığı ve katalog bilgileri.</p></div></div>
        <div className="product-step-grid product-basics-grid">
          <label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label>
          <label>Satış durumu<select value={form.status} onChange={event => updateField('status', event.target.value)}><option value="ACTIVE">Satışa Açık</option><option value="ARCHIVED">Satışa Kapalı</option><option value="DRAFT">Taslak</option></select></label>
          <label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
          <label>Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); if (!editProductId) setVariantRows([]) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label>
          <label>Model kodu<input className="technical-field model-code-value" value={form.modelCode} onChange={event => updateField('modelCode', event.target.value)} /></label>
          <label>Stok Kodu<input className="technical-field sku-value" value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder="RAV-BLUZ" /></label>
          <label>Barkod<input className="technical-field barcode-value" value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder="Varyantsız üründe kullanılır" /></label>
          <label className="desi-input-field">Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label>
          <label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label>
        </div>
      </section>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div><h2>Fiyat, stok ve vergi</h2><p>Başlangıç değerleri varyantlara uygulanır.</p></div></div>
          <div className="product-step-grid">
            <label>Liste fiyatı<input value={form.listPrice} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Satış fiyatı<input value={form.salePrice} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Para birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label>
            <label>KDV oranı<select value={form.vatRate} onChange={event => updateField('vatRate', event.target.value)}><option value="1">%1</option><option value="10">%10</option><option value="20">%20</option></select></label>
            <label>KDV dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label>
            <label>Stok<input value={form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" /></label>
            <label>Güvenlik stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label>
          </div>
        </section>
        <CategoryAttributeMappingPanel
          categoryId={form.categoryId}
          requirements={allRequirements}
          isLoading={requirements.isLoading}
          isError={requirements.isError}
          attributeSelections={attributeSelections}
          attributeTextValues={attributeTextValues}
          onToggleValue={toggleAttributeValue}
          onTextChange={(attributeId, value) => setAttributeTextValues(current => ({ ...current, [attributeId]: value }))}
        />
      </div>
    </div>
    {desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><section className="workspace-modal desi-calculator-modal" role="dialog" aria-modal="true" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi hesapla</h2><p>En × Boy × Yükseklik / 3000 formülü kullanılır.</p></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat">×</button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></section></div>}

    {mediaUrlSettingsOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setMediaUrlSettingsOpen(false)}><section className="workspace-modal product-media-url-modal" role="dialog" aria-modal="true" aria-labelledby="product-media-url-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="product-media-url-title">Link ile görsel ekle</h2><p>Her satıra bir HTTPS adresi yazın. Eklenen görseller varyant seçimlerinde de kullanılabilir.</p></div><button type="button" className="modal-close" onClick={() => setMediaUrlSettingsOpen(false)} aria-label="Pencereyi kapat">×</button></header><label className="product-media-url-field">Görsel URL listesi<textarea id="product-media-urls" aria-describedby="media-url-help" value={form.mediaUrls} onChange={event => updateField('mediaUrls', event.target.value)} placeholder="Örn. https://site.com/gorsel-1.jpg&#10;https://site.com/gorsel-2.png" autoFocus /><small id="media-url-help" className="field-help">İlk adres ürünün genel ana görselidir. Varyant görseli seçimi aşağıdaki tabloda yapılır.</small></label><footer><span>{mediaUrls.length} adres kayıtlı</span><button type="button" onClick={() => setMediaUrlSettingsOpen(false)}>Tamam</button></footer></section></div>}
    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card product-media-card"><div className="editor-section-title"><span>4</span><div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilir veya internetten erişilebilen HTTPS adresleri ekleyebilirsiniz.</p></div><button type="button" className="product-media-link-button" onClick={() => setMediaUrlSettingsOpen(true)} aria-label="Link ile görsel ekle" title="Link ile görsel ekle"><span aria-hidden="true">↗</span>{mediaUrls.length > 0 && <b>{mediaUrls.length}</b>}</button></div><label className="upload-ghost-box product-media-upload"><input type="file" accept="image/jpeg,image/png" multiple onChange={event => { handleMediaFiles(Array.from(event.target.files ?? [])); event.currentTarget.value = '' }} /><strong>{mediaFiles.length ? `${mediaFiles.length} dosya seçildi` : 'Ürün görsellerini dosya olarak seç'}</strong><small>Adet sınırı yok · JPEG veya PNG · dosya başına en fazla 6 MB</small></label>{(mediaUrls.length > 0 || mediaFiles.length > 0) && <div className="media-preview-strip">{mediaFiles.map((file, index) => <LocalImagePreview key={`${file.name}-${file.lastModified}-${index}`} file={file} alt={`${form.title || 'Ürün'} ${index + 1}`} caption={index === 0 && !mediaUrls.length ? 'Ana görsel' : file.name} onRemove={() => setMediaFiles(files => files.filter((_, i) => i !== index))} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} />)}{mediaUrls.map((url, index) => <figure key={`${url}-${index}`} className="image-preview-card"><img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} className="clickable-thumb" onClick={() => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} title="Büyütmek için tıklayın" /><button type="button" className="image-remove-btn" title="Görseli kaldır" onClick={e => { e.stopPropagation(); const next = mediaUrls.filter((_, i) => i !== index).join('\n'); updateField('mediaUrls', next) }}>✕</button><figcaption>{index === 0 && !mediaFiles.length ? 'Ana görsel' : `${index + 1}. görsel`}</figcaption></figure>)}</div>}
      </section>

      <section className="panel product-step-card">
        <div className="editor-section-title">
          <span>5</span>
          <div>
            <h2>Ürün seçenekleri</h2>
            <p>Seçenek grubu ve değerlerini burada seçin. Kayıtlı ürün değerleri otomatik işaretlenmez; seçimleriniz yalnızca “Ürünleri ekle” tıklanınca varyant satırlarına eklenir.</p>
          </div>
        </div>
        <div className="attribute-variant-action">
          <div>
            <strong>Varyantları oluştur</strong>
             <small>{variantAttributeIds.length ? `${variantAttributeIds.map(id => allRequirements.find(item => item.attributeId === id)?.attribute.name).filter(Boolean).join(' × ')} · ${variantAttributeIds.reduce((total, id) => total * Math.max(1, attributeSelections[id]?.length ?? 0), 1)} kombinasyon` : 'Önce seçenek grubunu ve değerlerini işaretleyin.'}</small>
          </div>
          <div className="attribute-variant-actions">
            <button type="button" onClick={generateVariants}>Ürünleri ekle</button>
          </div>
        </div>
        {!form.categoryId ? (
          <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde o kategoriye bağlanan özellikler burada görünür.</p></div>
        ) : requirements.isLoading ? (
          <p>Kategori özellikleri yükleniyor…</p>
        ) : requirements.isError ? (
          <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Önce kategori eşleme ekranında ilgili kategorinin özellik başlıklarını hazırlayın.</p></div>
        ) : (
          <div className="attribute-builder-list">
            {visibleOptionRequirements.map((item, index) => {
              const expandable = item.attribute.values.length > 0
              const expanded = !expandable || (expandedOptionGroupIds[item.attributeId] ?? index === 0)
              return (
              <article className={`attribute-builder-card ${expanded ? 'is-open' : ''}`} key={item.attributeId}>
                <button type="button" className="attribute-builder-disclosure" aria-expanded={expanded} aria-controls={`option-values-${item.attributeId}`} disabled={!expandable} onClick={() => setExpandedOptionGroupIds(current => ({ ...current, [item.attributeId]: !expanded }))}>
                  <span className="attribute-builder-disclosure-copy"><strong>{item.attribute.name}</strong><small>{item.attribute.values.length} değer · seçenek grubu</small></span>
                  <span className="attribute-builder-disclosure-meta"><small className={variantAttributeIds.includes(item.attributeId) ? 'attribute-builder-selected' : ''}>{(attributeSelections[item.attributeId]?.length ?? 0) > 0 ? `${attributeSelections[item.attributeId].length} değer seçildi` : 'Değer seçin'}</small>{expandable && <i aria-hidden="true">⌄</i>}</span>
                </button>
                {expanded && <div id={`option-values-${item.attributeId}`} className="attribute-builder-values">
                {item.attribute.values.length ? (
                  <div className="option-chip-list">
                    {item.attribute.values.map(value => {
                      const isSelected = (attributeSelections[item.attributeId] ?? []).includes(value.id)
                      return (
                        <button
                          type="button"
                          key={value.id}
                          className={`option-chip ${isSelected ? 'active' : ''}`}
                          onClick={() => toggleAttributeValue(item.attributeId, value.id)}
                        >
                          {value.value}
                        </button>
                      )
                    })}
                  </div>
                ) : item.attribute.dataType === 'BOOLEAN' ? (
                  <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label>
                ) : (
                  <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder="Değer girin" /></label>
                )}
                </div>}
              </article>
              )
            })}
            {!visibleOptionRequirements.length && (
              <div className="empty small" style={{ gridColumn: '1 / -1' }}>
                <p>Bu ürün için kayıtlı seçenek grubu bulunamadı. Seçenek Eşitleme ekranından seçenek grubu oluşturabilirsiniz.</p>
              </div>
            )}
          </div>
        )}
      </section>

      <section className="panel product-step-card">
        <div className="editor-section-title"><span>6</span><div><h2>Ürün seçenek grupları</h2><p>İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</p></div></div>
        {variantRows.length > 0 && <>
          <div className="variant-bulk-editor"><input value={bulkStock} onChange={event => setBulkStock(event.target.value)} type="number" min="0" placeholder="Tüm stoklar" /><input value={bulkSalePrice} onChange={event => setBulkSalePrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm satış fiyatları" /><input value={bulkListPrice} onChange={event => setBulkListPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm liste fiyatları" /><button type="button" className="secondary" onClick={applyBulk}>Tümüne uygula</button></div>
            <div className="variant-table-toolbar"><span>Varyant görsellerini tek tek veya seçenek değerine göre toplu atayın.</span><div className="variant-table-toolbar-actions"><button type="button" className="secondary variant-clear-button" onClick={clearVariants}>Oluşan varyantları temizle</button><button type="button" className="secondary variant-media-bulk-button" onClick={openBulkVariantMediaPicker} title="Seçenek değerine görsel ata"><VariantImageIcon /> Seçeneklere görsel ata</button></div></div>
        </>}
           <div className="variant-table-editor"><div className="variant-table-head"><span>#</span><span>Seçenek</span><span>Barkod</span><span>Stok kodu</span><span>Stok</span><span>Fiyat</span><span>Liste fiyatı</span><span>Varyant görseli</span><span>İşlem</span></div>{variantRows.length ? variantRows.map((row, index) => <div className={`variant-table-row ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key} onDragOver={event => event.preventDefault()} onDragEnter={() => { if (!draggedVariantKey || draggedVariantKey === row.key || dragOverVariantKey === row.key) return; swapVariants(draggedVariantKey, row.key); setDragOverVariantKey(row.key) }}><div className="variant-row-lead"><span className="variant-row-number">{index + 1}</span><span className="variant-drag-handle" draggable title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onDragStart={event => { event.dataTransfer.effectAllowed = 'move'; setDraggedVariantKey(row.key); setDragOverVariantKey(null) }} onDragEnd={() => { setDraggedVariantKey(null); setDragOverVariantKey(null) }}><VariantDragHandleIcon /></span></div><input value={row.optionSignature} readOnly /><input className="technical-field barcode-value" value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" /><input className="technical-field sku-value" value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" /><input value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" /><input value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /><input value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /><div className="variant-media-cell"><button type="button" className={`variant-media-button ${row.mediaRef ? 'has-media' : ''}`} onClick={() => openVariantMediaPicker(row.key)} aria-label={`${row.optionSignature} görseli seç`} title="Varyant görselini seç"><VariantImageIcon />{row.mediaRef && <i aria-hidden="true">✓</i>}</button></div><button type="button" className="secondary" onClick={() => setVariantRows(rows => rows.filter(item => item.key !== row.key))}>Sil</button></div>) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini seçip “Ürünleri ekle” dediğinizde varyant satırları burada oluşur.</p></div>}</div>
      </section>
    </div></div>

    {!editProductId && <section className="panel product-advanced-fields-card"><div className="editor-section-title"><div><h2>Katalog ayrıntıları</h2><p>Stok kodu, barkod ve kargo bilgisini ürün kaydına ekleyin.</p></div></div><div className="product-step-grid"><label>Stok kodu<input className="technical-field sku-value" value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder="RAV-BLUZ" /></label><label>Barkod<input className="technical-field barcode-value" value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder="Varyantsız üründe kullanılır" /></label><label>Satış durumu<select value={form.status} onChange={event => updateField('status', event.target.value)}><option value="ACTIVE">Satışa Açık</option><option value="ARCHIVED">Satışa Kapalı</option><option value="DRAFT">Taslak</option></select></label><label>Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label></div></section>}

    <section className="product-publish-step" aria-label={editProductId ? 'Ürün yayınlama ve güncelleme' : 'Ürün yayınlama'}><div className="product-publish-layout"><div className="product-publish-main"><div className="publish-platform-grid">{platformCards.length ? platformCards.map(card => { const selected = selectedChannelIds.includes(card.connection.id); return <article className={`publish-platform-card ${selected ? 'selected' : ''}`} key={card.connection.id}><button type="button" className="publish-platform-card-head" onClick={() => updateChannel(card.connection.id)} aria-pressed={selected}><span className={`publish-platform-mark ${card.tone}`}>{card.initial}</span><span><strong>{card.name}</strong><small>{selected ? 'Yayın için seçildi' : 'Bağlantı hazır'}</small></span><i className={`publish-platform-toggle ${selected ? 'on' : ''}`} aria-hidden="true"><b /></i></button><dl className="publish-platform-facts"><div><dt>Mağaza</dt><dd>{card.connection.externalStoreId || '—'}</dd></div><div><dt>Platform</dt><dd>{card.connection.platformCode}</dd></div><div><dt>Durum</dt><dd className="success">Aktif bağlantı</dd></div></dl></article> }) : <div className="publish-connections-empty"><strong>Aktif bağlantı bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Platformları yönet <span aria-hidden="true">→</span></Link></div>}</div><details className="scheduled-publish-panel" open={scheduledPublishOpen} onToggle={event => setScheduledPublishOpen((event.currentTarget as HTMLDetailsElement).open)}><summary><span><b aria-hidden="true">◷</b> Yayın kuyruğu</span><strong aria-hidden="true">⌄</strong></summary><div className="scheduled-publish-info"><strong>{editProductId ? 'Güncelleme ve yayın kuyruğu hazır' : 'Otomatik sıraya alma aktif'}</strong><p>{editProductId ? 'Değişiklikler kaydedildikten sonra seçtiğiniz aktif platformlarda yayın veya güncelleme işi oluşturulur.' : 'Ürün oluşturulduktan sonra seçtiğiniz aktif platformlarda yayın kuyruğuna alınır.'}</p><small>Planlı tarih ve saat seçimi platform bağlantısı desteklediğinde etkinleşecektir.</small></div></details></div><aside className="publish-checklist-panel"><div className="publish-checklist-heading"><span aria-hidden="true">☷</span><div><h2>Kontrol Listesi</h2><p>Yayınlamadan önce son kontroller</p></div></div><div className="publish-checklist-items">{productChecks.map(check => <article className={check.ok ? 'complete' : 'incomplete'} key={check.title}><span aria-hidden="true">{check.ok ? '✓' : '!'}</span><div><strong>{check.title}</strong><p>{check.detail}</p></div></article>)}{selectedPublishConnections.length === 0 && <article className="publish-check-warning"><span aria-hidden="true">!</span><div><strong>Yayın platformu seçilmedi</strong><p>Ürünü yayınlamak istediğiniz aktif platformları seçin.</p></div></article>}</div><div className="publish-checklist-footer"><span>Yayınlanacak Platform</span><strong>{selectedPublishConnections.length}</strong><button type="submit" disabled={submitting}>{submitting ? (editProductId ? 'Kaydediliyor…' : 'Ürün oluşturuluyor…') : (editProductId ? 'Değişiklikleri kaydet' : '🚀 Ürünü Oluştur')}</button><small>{editProductId ? 've seçili platformları güncelle' : 've seçili platformlarda yayınla'}</small></div></aside></div></section>

    <section className="product-submit-sticky"><div><strong>{editProductId ? 'Ürün düzenlemeye hazır' : 'Ürün bilgileri hazır'}</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><div className="product-submit-actions">{editProductId && <button type="submit" name="intent" value="save" className="secondary" data-submit-intent="save" form="product-creation-form" disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Kaydet'}</button>}<button type="button" onClick={() => setWizardStep(2)}>Yayınlamaya devam et <span aria-hidden="true">→</span></button></div></section>
    <ErrorBox error={error ?? categories.error ?? brands.error ?? connections.error} />{created && <p className="success">Oluşturuldu: <Link to={`/products/${created.id}`}>ürünü aç</Link></p>}
    <OperationFeedbackToast feedback={feedback} onClose={() => { setFeedback(null); setNotice('') }} />
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
    {variantMediaModal && <VariantMediaPickerModal mode={variantMediaModal.mode} options={mediaChoices} selectedRef={variantMediaModal.draftRef} groups={variantMediaModal.mode === 'bulk' ? bulkMediaGroups : undefined} selectedGroupId={variantMediaModal.groupId} selectedValueId={variantMediaModal.valueId} matchedVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaMatchCount : undefined} onRefChange={value => setVariantMediaModal(current => current ? { ...current, draftRef: value } : current)} onGroupChange={groupId => setVariantMediaModal(current => { const group = bulkMediaGroups.find(item => item.id === groupId); return current ? { ...current, groupId, valueId: group?.values[0]?.id ?? '' } : current })} onValueChange={valueId => setVariantMediaModal(current => current ? { ...current, valueId } : current)} onApply={applyVariantMediaSelection} onClose={() => setVariantMediaModal(null)} />}
  </form></Page>
}

export function ProductDetailPage() {
  const { id } = useParams()
  if (!id) return <p className="unknown">Ürün kimliği bulunamadı.</p>
  return <NewProductPage editProductId={id} />
}

/*
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', status: 'ACTIVE' }); const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({})
  const query = useQuery({ queryKey: ['product', id], queryFn: () => hubApi<Product>(`/products/${id}`), enabled: !!id })
  useEffect(() => {
    if (!query.data) return
    setForm({ title: query.data.title, description: query.data.description ?? '', brandId: query.data.brandId ?? '', categoryId: query.data.categoryId ?? '', status: query.data.status || 'ACTIVE' })
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
  const categories = useQuery({ queryKey: ['categories', 'edit-product'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  const brands = useQuery({ queryKey: ['brands', 'edit-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  const requirements = useQuery({ queryKey: ['category-requirements', 'edit-product', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const connections = useQuery({ queryKey: ['connections', 'product-publication'], queryFn: () => loadAllPages<TrendyolConnection>('/connections') })
  const status = useQuery({ queryKey: ['publication-status', id, connectionId], queryFn: () => hubApi<PublicationStatus>(`/products/${id}/publication-status/${connectionId}`), enabled: !!id && !!connectionId, retry: false })
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && item.status === 'ACTIVE') ?? []
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  useEffect(() => { if (query.data) setDescription(query.data.description ?? '') }, [query.data?.id, query.data?.description])
  const refresh = async () => { await client.invalidateQueries({ queryKey: ['product', id] }); await client.invalidateQueries({ queryKey: ['products'] }) }
  async function updateProduct(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/products/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${query.data.version}"` }, body: JSON.stringify({ title: data.get('title'), description, brandId: query.data.brandId, categoryId: query.data.categoryId }) }); setNotice('Ürün bilgileri güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Ürün güncellenemedi.') } }
  async function image(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: query.data.id, variantId: null, url: data.get('imageUrl'), mediaRole: 'PRIMARY', sortOrder: 0, altText: query.data.title }) }); setNotice('Ana görsel güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel güncellenemedi.') } }
  async function uploadImage(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const source = new FormData(event.currentTarget); const file = source.get('file'); if (!(file instanceof File) || !file.size) return; const body = new FormData(); body.set('file', file); body.set('productId', query.data.id); body.set('mediaRole', 'PRIMARY'); body.set('sortOrder', '0'); body.set('altText', query.data.title); try { await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body }); setNotice('Ana görsel dosyadan güncellendi.'); event.currentTarget.reset(); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel yüklenemedi.') } }
  async function run(path: string, body: object) { try { setNotice(''); const accepted = await hubApi<AcceptedJob>(path, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); setNotice(`İş kuyruğa alındı: ${accepted.jobId}`); await client.invalidateQueries({ queryKey: ['publication-status', id, connectionId] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.') } }
  // Product edit workspace render point.
  if (query.data) return <Page title="Ürün Düzenle" eyebrow="Katalog"><p className="lede page-lede">Ürün ekleme çalışma alanındaki aynı katalog, özellik, fiyat, stok ve yayın bölümlerinden mevcut kaydı yönetin.</p>{notice && <div role="status" className="notice">{notice}</div>}<div className="product-creation-workspace product-edit-workspace">
    <form id="product-edit-form" onSubmit={updateCatalogDetails}><section className="panel product-step-card"><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün adı, marka, kategori ve açıklamayı ürün eklerkenki alan düzeninde güncelleyin.</p></div></div><div className="product-step-grid product-basics-grid"><label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label><label>Satış durumu<select value={form.status} onChange={event => updateField('status', event.target.value)}><option value="ACTIVE">Satışa Açık</option><option value="ARCHIVED">Satışa Kapalı</option><option value="DRAFT">Taslak</option></select></label><label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label>Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label><label>Model kodu<input className="technical-field model-code-value" value={query.data.modelCode ?? ''} readOnly aria-label="Model kodu" /></label><label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label></div></section>
      <section className="panel product-step-card"><div className="editor-section-title"><span>2</span><div><h2>Ürün özellikleri</h2><p>Kategoriye bağlı değerler kayıtlı seçimleriyle gelir ve ürünle birlikte kaydedilir.</p></div></div>{!form.categoryId ? <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde bağlı özellikler burada görünür.</p></div> : requirements.isLoading ? <p>Kategori özellikleri yükleniyor…</p> : requirements.isError ? <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Özellikler yüklenmeden ürün kaydedilemez.</p></div> : <div className="attribute-builder-list">{(requirements.data ?? []).sort((a, b) => a.displayOrder - b.displayOrder).map(item => <article className="attribute-builder-card edit-attribute-card" key={item.attributeId}><div className="attribute-builder-head"><strong>{item.attribute.name}{item.isRequired ? ' *' : ''}</strong><small>{item.attribute.dataType}</small></div>{item.attribute.values.length ? <div className="option-chip-list">{item.attribute.values.map(value => <button type="button" key={value.id} className={`option-chip ${(attributeSelections[item.attributeId] ?? []).includes(value.id) ? 'active' : ''}`} onClick={() => toggleAttributeValue(item, value.id)}>{value.value}</button>)}</div> : item.attribute.dataType === 'BOOLEAN' ? <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label> : <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} /></label>}</article>)}</div>}</section></form>
     <div className="product-layout-grid"><div className="product-main-stack"><section className="panel product-step-card"><div className="editor-section-title"><span>3</span><div><h2>Fiyat, stok ve vergi</h2><p>Her varyantın stok ve kanal fiyatı kendi sürüm kontrolüyle güncellenir.</p></div></div>{query.data.variants.map(variant => <section className="variant-editor product-edit-variant" key={variant.id}><div><strong>{variant.optionSignature || <code className="technical-text sku-value">{variant.sku}</code>}</strong><span>SKU: <code className="technical-text sku-value">{variant.sku}</code> · Barkod: <code className="technical-text barcode-value">{variant.barcode ?? '—'}</code> · Kullanılabilir: {variant.available}</span></div><VariantQuickEditor variant={variant} connections={activeConnections} onChanged={refresh} /></section>)}</section>
       <section className="panel product-step-card cargo-size-card"><div className="editor-section-title"><span>4</span><div><h2>Kargo ölçüleri ve desi</h2><p>Varyant ölçüleri, ürün ekleme sayfasındaki alanlarla aynı formatta gösterilir.</p></div></div><div className="product-step-grid">{query.data.variants.map(variant => <article className="variant-dimensions" key={variant.id}><strong><code className="technical-text sku-value">{variant.sku}</code></strong><dl><div><dt>Ağırlık</dt><dd>{variant.weight ?? '—'} kg</dd></div><div><dt>En × Boy × Yükseklik</dt><dd>{variant.width ?? '—'} × {variant.length ?? '—'} × {variant.height ?? '—'} cm</dd></div><div><dt>Desi</dt><dd>{variant.desi ?? '—'}</dd></div></dl></article>)}</div></section>
      <section className="panel product-step-card"><div className="editor-section-title"><span>5</span><div><h2>Görseller</h2><p>Yeni ürün ekranındaki gibi dosyadan veya HTTPS adresinden ana görseli güncelleyin.</p></div></div><div className="product-step-grid"><form onSubmit={uploadImage}><label className="upload-ghost-box product-media-upload"><input name="file" type="file" accept="image/jpeg,image/png" required /><strong>Dosya seç</strong><small>JPEG veya PNG, en fazla 10 MB</small></label><button>Görseli yükle</button></form><form onSubmit={image}><label>Görsel URL<input name="imageUrl" type="url" defaultValue={query.data.primaryImageUrl ?? ''} required /></label><button>Görseli kaydet</button></form></div></section></div>
      <aside className="panel publish-channel-panel"><div className="editor-section-title"><span>6</span><div><h2>Yayınlanacak kanallar</h2><p>Yayın yönetimi ürün ekleme akışındaki güvenli kanal bölümünde kalır.</p></div></div><label>Aktif Trendyol bağlantısı<select aria-label="Ürün Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>{connectionId && <><div className="actions spaced"><button type="button" onClick={() => run(`/products/${id}/publication-jobs`, { connectionId })}>Yeni ürün olarak yayınla</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/update-jobs`, { connectionId })}>Trendyol ürününü güncelle</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: true })}>Trendyol'da arşivle</button><button type="button" className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: false })}>Arşivden çıkar</button></div>{status.isLoading ? <p>Yayın durumu yükleniyor…</p> : status.isError ? <p className="notice">Henüz listing profili veya yayın durumu yok.</p> : status.data && <dl className="details"><dt>Gerçek durum</dt><dd>{status.data.actualStatus ?? '—'}</dd><dt>Son iş</dt><dd>{status.data.lastJobStatus ?? '—'}</dd><dt>Ret kodu</dt><dd>{status.data.lastRejectionCode ?? '—'}</dd></dl>}</>}</aside></div>
    <section className="product-submit-sticky"><div><strong>Ürün düzenlemeye hazır</strong><p>{query.data.variants.length} varyant · kategori özellikleri kaydedilecek</p></div><button form="product-edit-form">Değişiklikleri kaydet</button></section><ErrorBox error={categories.error ?? brands.error ?? connections.error} /></div></Page>
  /* Legacy product-detail layout retained below only while the unified edit workspace replaces it.
  return <Page title={query.data?.title ?? 'Ürün'} eyebrow="Ürün detayı">{query.isError ? <ErrorBox error={query.error} /> : !query.data ? <p>Yükleniyor…</p> : <>
    {notice && <div role="status" className="notice">{notice}</div>}<div className="detail-grid product-edit-overview"><article className="panel product-detail-hero">{query.data.primaryImageUrl ? <img src={query.data.primaryImageUrl} alt={query.data.title} /> : <span className="product-image-placeholder">Görsel yok</span>}<div><Tag>{query.data.status === 'ACTIVE' ? 'Satışta' : 'Kapalı'}</Tag><p>Model: {query.data.modelCode ?? '—'}</p><p>Stok: <strong>{query.data.totalStock}</strong></p><p>Başlangıç fiyatı: <strong>{money(query.data.startingPrice, query.data.currency)}</strong></p><p>Aktif platformlar: {query.data.activePlatforms?.join(', ') || '—'}</p></div></article><form className="panel product-step-card product-edit-form" onSubmit={updateProduct}><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Oluşturma ekranındaki alan yapısıyla ürünü düzenleyin.</p></div></div><label>Ürün adı<input name="title" defaultValue={query.data.title} required /></label><label>Açıklama<RichTextEditor value={description} onChange={setDescription} /></label><button>Bilgileri kaydet</button></form></div>
    <div className="detail-grid product-edit-media"><form className="panel product-step-card" onSubmit={uploadImage}><div className="editor-section-title"><span>2</span><div><h2>Ürün görseli yükle</h2><p>JPEG veya PNG dosyasını doğrudan özel depolamaya yükleyin.</p></div></div><label className="upload-ghost-box product-media-upload"><input name="file" type="file" accept="image/jpeg,image/png" required /><strong>Dosya seç</strong><small>En fazla 10 MB</small></label><button>Görseli yükle</button></form><form className="panel product-step-card" onSubmit={image}><div className="editor-section-title"><span>3</span><div><h2>Görsel adresi</h2><p>İsteğe bağlı olarak HTTPS görsel adresi kullanın.</p></div></div><label>Görsel URL<input name="imageUrl" type="url" defaultValue={query.data.primaryImageUrl ?? ''} required /></label><button>Görseli kaydet</button></form></div>
    <article className="panel"><h2>Varyantlar, stok ve fiyat</h2>{query.data.variants.map(variant => <section className="variant-editor" key={variant.id}><div><strong>{variant.sku}</strong><span>Barkod: {variant.barcode ?? '—'} · Model: {variant.modelCode ?? '—'} · Ölçü: {variant.width ?? '—'} × {variant.length ?? '—'} × {variant.height ?? '—'} cm · Ağırlık: {variant.weight ?? '—'} kg</span></div><VariantQuickEditor variant={variant} connections={activeConnections} onChanged={refresh} /></section>)}</article>
    <article className="panel"><h2>Trendyol yayın yönetimi</h2><p className="notice">Stage bağlantısında manuel yayın ve güncelleme doğrudan sağlayıcıya gönderilir. Production’da aktif bağlantı ve dış-yazma anahtarları gerekir.</p><label>Aktif Trendyol bağlantısı<select aria-label="Ürün Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>{connectionId && <><div className="actions spaced"><button onClick={() => run(`/products/${id}/publication-jobs`, { connectionId })}>Yeni ürün olarak yayınla</button><button className="secondary" onClick={() => run(`/products/${id}/update-jobs`, { connectionId })}>Trendyol ürününü güncelle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: true })}>Trendyol'da arşivle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: false })}>Arşivden çıkar</button></div>{status.isLoading ? <p>Yayın durumu yükleniyor…</p> : status.isError ? <p className="notice">Henüz listing profili veya yayın durumu yok.</p> : status.data && <dl className="details"><dt>Gerçek durum</dt><dd>{status.data.actualStatus ?? '—'}</dd><dt>Son job</dt><dd>{status.data.lastJobStatus ?? '—'}</dd><dt>Ret kodu</dt><dd>{status.data.lastRejectionCode ?? '—'}</dd></dl>}</>}</article>
  </>}</Page> */

export function CategoriesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['categories'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name'), parentId: data.get('parentId') || null }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['categories'] }) } catch (reason) { setError(reason) } }
  return <Page title="Kategoriler" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Kategori adı<input name="name" required /></label><label>Üst kategori kimliği<input name="parentId" /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="tree-list">{query.data?.items.map(item => <article key={item.id} style={{ marginLeft: Math.min(item.depth, 6) * 18 }}><div><strong>{item.name}</strong><small>{item.path}</small></div><Tag>{item.isLeaf ? 'LEAF' : 'PARENT'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function BrandsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['brands'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name') }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['brands'] }) } catch (reason) { setError(reason) } }
  return <Page title="Markalar" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Marka adı<input name="name" required /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><strong>{item.name}</strong><Tag>{item.isActive ? 'ACTIVE' : 'DISABLED'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function AttributesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['attributes'], queryFn: () => loadAllPages<Attribute>('/catalog/attributes') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); const values = String(data.get('values') || '').split(',').map(x => x.trim()).filter(Boolean).map((value, sortOrder) => ({ value, sortOrder })); try { await hubApi('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ code: data.get('code'), name: data.get('name'), dataType: data.get('dataType'), selectionMode: null, unit: null, values }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['attributes'] }) } catch (reason) { setError(reason) } }
  return <Page title="Özellikler" eyebrow="Katalog"><form className="panel form-grid" onSubmit={submit}><label>Kod<input name="code" required /></label><label>Ad<input name="name" required /></label><label>Tip<select name="dataType"><option>TEXT</option><option>NUMBER</option><option>SINGLE_SELECT</option><option>MULTI_SELECT</option><option>BOOLEAN</option></select></label><label>Seçenekler (virgülle)<input name="values" /></label><ErrorBox error={error} /><button>Ekle</button></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><div><strong>{item.name}</strong><small>{item.code}</small></div><Tag>{item.dataType}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function ImportsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['imports'], queryFn: () => loadAllPages<ImportSession>('/imports') })
  async function create(sourceType: string) { try { await hubApi('/imports', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ sourceType, connectionId: null }) }); await client.invalidateQueries({ queryKey: ['imports'] }) } catch (reason) { setError(reason) } }
  return <Page title="İçe aktarımlar" eyebrow="Katalog" action={<div className="actions"><button onClick={() => create('CSV')}>CSV başlat</button><button onClick={() => create('XLSX')}>XLSX başlat</button></div>}><ErrorBox error={error ?? query.error} />{!query.data?.items.length ? <div className="empty">Henüz import oturumu yok.</div> : <div className="table-wrap"><table><thead><tr><th>Kaynak</th><th>Durum</th><th>Satır</th><th></th></tr></thead><tbody>{query.data.items.map(item => <tr key={item.id}><td>{item.sourceType}</td><td><Tag>{item.status}</Tag></td><td>{item.validRows}/{item.totalRows}</td><td><Link to={`/imports/${item.id}`}>İncele</Link></td></tr>)}</tbody></table></div>}</Page>
}

export function ImportDetailPage() {
  const { id } = useParams(); const client = useQueryClient(); const [error, setError] = useState<unknown>(); const session = useQuery({ queryKey: ['import', id], queryFn: () => hubApi<ImportSession>(`/imports/${id}`), enabled: !!id, refetchInterval: 4000 }); const candidates = useQuery({ queryKey: ['candidates', id], queryFn: () => loadAllPages<Candidate>(`/imports/${id}/candidates`), enabled: session.data?.status === 'REVIEW_REQUIRED' })
  async function upload(event: FormEvent<HTMLFormElement>) { event.preventDefault(); try { await hubApi(`/imports/${id}/source-file`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: new FormData(event.currentTarget) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function map(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!session.data) return; const data = new FormData(event.currentTarget); const headers = String(data.get('headers')).split(',').map(x => x.trim()); const fields = String(data.get('fields')).split(',').map(x => x.trim()); try { await hubApi(`/imports/${id}/column-mapping`, { method: 'PUT', headers: { 'If-Match': `"v${session.data.version}"` }, body: JSON.stringify({ profileName: 'Manuel eşleme', variantGroupKey: null, mappings: headers.map((sourceColumn, sortOrder) => ({ sourceColumn, targetField: fields[sortOrder], sortOrder })) }) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function job(kind: 'preview' | 'apply') { try { await hubApi(`/imports/${id}/${kind}-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() } }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function decide(candidate: Candidate, decision: 'CREATE' | 'LINK' | 'SKIP') { try { await hubApi(`/imports/${id}/decisions/${candidate.id}`, { method: 'PUT', headers: { 'If-Match': `"v${candidate.version}"` }, body: JSON.stringify({ decision, productId: decision === 'LINK' ? candidate.productId : null, variantId: decision === 'LINK' ? candidate.variantId : null }) }); await client.invalidateQueries({ queryKey: ['candidates', id] }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  if (!session.data) return <Page title="İçe aktarım" eyebrow="Katalog"><ErrorBox error={session.error} /><p>Yükleniyor…</p></Page>
  return <Page title={`İçe aktarım ${session.data.id.slice(0, 8)}`} eyebrow="Katalog"><div className="metrics">{[['Durum', session.data.status], ['Toplam', session.data.totalRows], ['Geçerli', session.data.validRows], ['Hatalı', session.data.errorRows]].map(([label, value]) => <article key={label}><small>{label}</small><strong>{value}</strong></article>)}</div><ErrorBox error={error} />{session.data.status === 'CREATED' && <div className="detail-grid"><form className="panel" onSubmit={upload}><h2>1. Dosya</h2><input name="file" type="file" accept=".csv,.xlsx" required /><button>Yükle</button></form><form className="panel" onSubmit={map}><h2>2. Kolon eşleme</h2><label>Başlıklar<input name="headers" placeholder="Ürün,SKU,Barkod" required /></label><label>Hedefler<input name="fields" placeholder="title,sku,barcode" required /></label><button>Kaydet</button></form></div>}<div className="actions spaced">{session.data.status === 'CREATED' && session.data.sourceAssetId && <button onClick={() => job('preview')}>Preview oluştur</button>}{session.data.status === 'READY_TO_APPLY' && <button onClick={() => job('apply')}>Kararları uygula</button>}{session.data.errorRows > 0 && <a className="button-link secondary" href={`/api/v1/imports/${id}/errors.csv`}>Hataları indir</a>}</div>{candidates.data?.items.map(candidate => <article className="candidate" key={candidate.id}><div><Tag>{candidate.matchRule}</Tag><code>{candidate.safeSummary}</code></div><div className="actions"><button onClick={() => decide(candidate, 'CREATE')}>Yeni</button>{candidate.productId && <button onClick={() => decide(candidate, 'LINK')}>Eşle</button>}<button className="secondary" onClick={() => decide(candidate, 'SKIP')}>Atla</button></div></article>)}</Page>
}

export function InventoryPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const [connectionId, setConnectionId] = useState(''); const [notice, setNotice] = useState(''); const query = useQuery({ queryKey: ['inventory'], queryFn: () => loadAllPages<Inventory>('/inventory') })
  const connections = useQuery({ queryKey: ['connections', 'inventory-sync'], queryFn: () => loadAllPages<TrendyolConnection>('/connections') })
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && item.status === 'ACTIVE') ?? []
  async function adjust(item: Inventory, delta: number) { const reason = window.prompt('Düzeltme nedeni'); if (!reason) return; try { await hubApi(`/inventory/${item.variantId}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason, sourceEventId: key() }) }); await client.invalidateQueries({ queryKey: ['inventory'] }) } catch (failure) { setError(failure) } }
  async function sync() { if (!connectionId) return; try { const accepted = await hubApi<AcceptedJob>(`/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() } }); setNotice(`Birleşik fiyat-stok işi kuyruğa alındı: ${accepted.jobId}`) } catch (failure) { setNotice(failure instanceof Error ? failure.message : 'Senkronizasyon başlatılamadı.') } }
  return <Page title="Stok ve fiyat" eyebrow="Envanter" action={<div className="actions"><select aria-label="Fiyat stok Trendyol bağlantısı" value={connectionId} onChange={event => setConnectionId(event.target.value)}><option value="">Trendyol bağlantısı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName}</option>)}</select><button disabled={!connectionId} onClick={() => void sync()}>Fiyat + stok gönder</button></div>}><p className="notice">V1 depo kodu MAIN. Kullanılabilir = max(0, eldeki − rezervasyon − güvenlik stoğu). Trendyol'a fiyat ve stok tek dayanıklı batch ile gönderilir.</p>{notice && <div role="status" className="notice">{notice}</div>}<ErrorBox error={error ?? query.error} /><div className="table-wrap"><table><thead><tr><th>SKU</th><th>Depo</th><th>Eldeki</th><th>Rezerve</th><th>Kullanılabilir</th><th></th></tr></thead><tbody>{query.data?.items.map(item => <tr key={item.id}><td><strong>{item.sku}</strong></td><td>{item.locationCode}</td><td>{item.onHand}</td><td>{item.reserved}</td><td>{item.available}</td><td><div className="actions"><button onClick={() => adjust(item, 1)}>+1</button><button className="secondary" disabled={item.onHand <= 0} onClick={() => adjust(item, -1)}>−1</button></div></td></tr>)}</tbody></table></div></Page>
}
