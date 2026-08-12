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

function VariantQuickEditor({ variant, connections, onChanged }: { variant: Variant; connections: TrendyolConnection[]; onChanged: () => Promise<unknown> }) {
  const [notice, setNotice] = useState('')
  async function stock(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget); const target = Number(data.get('stock')); const delta = target - variant.onHand
    if (!Number.isFinite(target) || target < 0) return setNotice('Stok sıfır veya daha büyük olmalıdır.')
    if (delta === 0) return setNotice('Stok zaten bu değerde.')
    try { await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: String(data.get('reason') || 'Ürün kartı hızlı stok düzenleme'), sourceEventId: key() }) }); setNotice('Stok güncellendi.'); await onChanged() } catch (error) { setNotice(error instanceof Error ? error.message : 'Stok güncellenemedi.') }
  }
  async function price(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget); const salePrice = Number(data.get('salePrice')); const listPrice = Number(data.get('listPrice')); const connectionId = String(data.get('connectionId') || '')
    if (!Number.isFinite(salePrice) || !Number.isFinite(listPrice) || salePrice < 0 || listPrice < salePrice) return setNotice('Liste fiyatı satış fiyatından küçük olamaz.')
    const body = { connectionId, variantId: variant.id, listPrice, salePrice, currency: String(data.get('currency') || 'TRY'), vatRate: Number(data.get('vatRate') || 0), vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: Number(data.get('safetyStock') || 0), status: variant.offerStatus || 'ACTIVE', reason: String(data.get('reason') || 'Ürün kartı hızlı fiyat düzenleme') }
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
  return <div className="variant-quick-grid">
    <form onSubmit={stock}><strong>Stok</strong><label>MAIN stok<input name="stock" type="number" min="0" step="1" defaultValue={variant.onHand} /></label><input name="reason" placeholder="Değişiklik nedeni" /><button>Stoğu kaydet</button></form>
    <form onSubmit={price}><strong>Fiyat</strong>{!variant.offerId && <label>Platform<select name="connectionId" defaultValue=""><option value="">Bağlantı seçin</option>{connections.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}</select></label>}<div className="inline-fields"><label>Liste<input name="listPrice" type="number" min="0" step="0.01" defaultValue={variant.listPrice ?? 0} /></label><label>Satış<input name="salePrice" type="number" min="0" step="0.01" defaultValue={variant.salePrice ?? 0} /></label></div><div className="inline-fields"><label>KDV %<input name="vatRate" type="number" min="0" step="0.01" defaultValue={variant.vatRate ?? 20} /></label><label>Güvenlik stoğu<input name="safetyStock" type="number" min="0" step="1" defaultValue={variant.safetyStock ?? 0} /></label></div><input name="currency" defaultValue={variant.currency ?? 'TRY'} maxLength={3} /><input name="reason" placeholder="Değişiklik nedeni" /><button>Fiyatı kaydet</button></form>
    {notice && <p className="notice" role="status">{notice}</p>}
  </div>
}

export function ProductsPage() {
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [status, setStatus] = useState(''); const [platform, setPlatform] = useState(''); const [stock, setStock] = useState('')
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
  const refresh = () => client.invalidateQueries({ queryKey: ['products'] })
  return <Page title="Ürünler" eyebrow="Katalog" action={<Link className="button-link" to="/products/new">+ Yeni Ürün Ekle</Link>}>
    <p className="lede page-lede">Ürün, varyant, stok, fiyat ve pazaryeri yayın durumlarını tek kartta yönetin.</p>
    <div className="product-metrics metrics"><article><small>Toplam ürün</small><strong>{products.length}</strong><span>katalog kaydı</span></article><article><small>Aktif</small><strong>{products.filter(x => x.status === 'ACTIVE').length}</strong><span>ürün</span></article><article><small>Stoksuz</small><strong>{products.filter(x => x.totalStock <= 0).length}</strong><span>aksiyon gerekli</span></article><article><small>Düşük stok</small><strong>{products.filter(x => x.totalStock > 0 && x.totalStock <= 5).length}</strong><span>5 ve altı</span></article></div>
    <div className="product-toolbar"><label className="order-search"><span aria-hidden="true">⌕</span><input aria-label="Ürün ara" placeholder="Ürün adı, model, SKU veya barkod..." value={search} onChange={event => setSearch(event.target.value)} /></label><select aria-label="Ürün durumu" value={status} onChange={event => setStatus(event.target.value)}><option value="">Tüm durumlar</option><option value="ACTIVE">Aktif</option><option value="DRAFT">Taslak</option><option value="ARCHIVED">Arşiv</option></select><select aria-label="Platform filtresi" value={platform} onChange={event => setPlatform(event.target.value)}><option value="">Tüm platformlar</option>{platforms.map(item => <option key={item}>{item}</option>)}</select><select aria-label="Stok filtresi" value={stock} onChange={event => setStock(event.target.value)}><option value="">Tüm stoklar</option><option value="OUT">Stoksuz</option><option value="LOW">Düşük stok</option><option value="OK">Yeterli stok</option></select></div>
    <ErrorBox error={query.error ?? connectionsQuery.error} />{query.isLoading ? <p>Yükleniyor…</p> : !visible.length ? <div className="empty">Filtrelerle eşleşen ürün yok.</div> : <div className="product-card-list">{visible.map(product => <article className="product-operation-card" key={product.id}><div className="product-card-main">{product.primaryImageUrl ? <img src={product.primaryImageUrl} alt={product.title} /> : <span className="product-image-placeholder">Görsel yok</span>}<div className="product-card-copy"><div className="card-title-row"><div><h2>{product.title}</h2><p>Model: {product.modelCode ?? '—'} · {product.variants.length} varyant</p></div><Tag>{product.status}</Tag></div><div className="product-card-facts"><span><small>Genel stok</small><strong>{product.totalStock}</strong></span><span><small>Başlangıç fiyatı</small><strong>{money(product.startingPrice, product.currency)}</strong></span><span><small>Platformlar</small><strong>{product.activePlatforms?.join(', ') || 'Henüz aktif değil'}</strong></span></div></div><details className="card-menu"><summary aria-label="Ürün işlemleri">⋮</summary><div><Link to={`/products/${product.id}`}>Ürünü düzenle</Link><a href={`#quick-${product.id}`}>Hızlı stok/fiyat</a></div></details></div><details id={`quick-${product.id}`} className="product-variant-dropdown"><summary>Varyantları ve hızlı düzenlemeyi aç</summary>{product.variants.map(variant => <section key={variant.id} className="variant-editor"><div><strong>{variant.sku}</strong><span>Barkod: {variant.barcode ?? '—'} · {variant.optionSignature || 'Ana varyant'} · Kullanılabilir: {variant.available}</span></div><VariantQuickEditor variant={variant} connections={connections} onChanged={refresh} /></section>)}</details></article>)}</div>}
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
const MAX_VARIANTS = 100

function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const editor = useRef<HTMLTextAreaElement>(null)
  function wrap(open: string, close = open) { const field = editor.current; if (!field) return; const start = field.selectionStart; const end = field.selectionEnd; const selected = value.slice(start, end) || 'metin'; const next = `${value.slice(0, start)}${open}${selected}${close}${value.slice(end)}`; onChange(next); window.setTimeout(() => { field.focus(); field.setSelectionRange(start + open.length, start + open.length + selected.length) }) }
  return <div className="rich-text-editor"><div className="rich-text-toolbar" aria-label="Açıklama biçimlendirme araçları"><button type="button" onClick={() => wrap('<strong>', '</strong>')}><b>Kalın</b></button><button type="button" onClick={() => wrap('<em>', '</em>')}><i>İtalik</i></button><button type="button" onClick={() => wrap('<p>', '</p>')}>Paragraf</button><button type="button" onClick={() => wrap('<ul><li>', '</li></ul>')}>Liste</button><button type="button" onClick={() => wrap('<br>\n', '')}>Satır</button></div><textarea ref={editor} value={value} onChange={event => onChange(event.target.value)} required aria-label="Açıklama" /><details><summary>HTML ön izlemesi</summary><iframe className="rich-text-preview" sandbox="" title="Açıklama HTML ön izlemesi" srcDoc={value} /></details></div>
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

export function NewProductPage() {
  const [error, setError] = useState<unknown>(); const [created, setCreated] = useState<Product>(); const [notice, setNotice] = useState(''); const [submitting, setSubmitting] = useState(false); const [calculateDesi, setCalculateDesi] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', listPrice: '699.90', salePrice: '549.90', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '2', mediaUrls: '' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([])
  const [bulkStock, setBulkStock] = useState(''); const [bulkSalePrice, setBulkSalePrice] = useState(''); const [bulkListPrice, setBulkListPrice] = useState('')
  const [mediaFiles, setMediaFiles] = useState<File[]>([])
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => hubApi<PageData<Category>>('/catalog/categories?limit=200') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => hubApi<PageData<Brand>>('/catalog/brands?limit=200') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const activeConnections = (connections.data?.items ?? []).filter(item => item.status === 'ACTIVE' && item.platformCode === 'TRENDYOL')
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const initialStock = Number(form.initialStock || 0)
  const desi = useMemo(() => { const width = Number(form.width); const length = Number(form.length); const height = Number(form.height); return width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0 }, [form.width, form.length, form.height])
  const mediaUrls = useMemo(() => form.mediaUrls.split(/\r?\n/).map(item => item.trim()).filter(Boolean), [form.mediaUrls])

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
      setVariantRows(generated)
      setNotice(generated.length ? `${generated.length} varyant satırı hazırlandı.` : 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.')
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.') }
  }
  function clearVariants() { setVariantRows([]); setNotice('Oluşan varyant satırları temizlendi.') }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'stock' || field === 'salePrice' || field === 'listPrice' ? Number(value || 0) : value })) }
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
      const product = await hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ title: form.title, description: form.description, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes: globalAttributes, variants: rows.map((row, index) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : Number(form.desi) || 1, options: row.options, attributes: Object.entries(row.attributeValueIds).map(([attributeId, valueId], attributeIndex) => ({ attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: index * 100 + attributeIndex })) })) }) })
      productCreated = product; setCreated(product); const completed = ['ürün']; const warnings: string[] = []
      for (const [index, url] of mediaUrls.entries()) await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: null, url, mediaRole: index === 0 ? 'PRIMARY' : 'GALLERY', sortOrder: index, altText: form.title }) })
      for (const [fileIndex, file] of mediaFiles.entries()) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
      if (mediaUrls.length || mediaFiles.length) completed.push('görseller')
      for (const [index, variant] of product.variants.entries()) {
        const row = rows[index]
        if (row.stock > 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: row.stock, reason: 'İlk ürün stoğu', sourceEventId: key() }) })
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
    <section className="panel product-step-card"><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün kartının temel başlığı ve katalog bilgileri.</p></div></div><div className="product-step-grid product-basics-grid"><label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label><label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label>Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); setVariantRows([]) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label><label>Model kodu<input value={form.modelCode} onChange={event => updateField('modelCode', event.target.value)} /></label><label>Temel SKU<input value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder="RAV-BLUZ" /></label><label>Barkod<input value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder="Varyantsız üründe kullanılır" /></label><label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label></div></section>

    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card"><div className="editor-section-title"><span>2</span><div><h2>Fiyat, stok ve vergi</h2><p>Merkezi başlangıç değerleri varyant oluşturulurken satırlara uygulanır.</p></div></div><div className="product-step-grid"><label>Liste fiyatı<input value={form.listPrice} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Satış fiyatı<input value={form.salePrice} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Para birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label><label>KDV oranı<select value={form.vatRate} onChange={event => updateField('vatRate', event.target.value)}><option value="1">%1</option><option value="10">%10</option><option value="20">%20</option></select></label><label>KDV dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label><label>Stok<input value={form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" /></label><label>Güvenlik stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label></div></section>

      <section className="panel product-step-card cargo-size-card"><div className="editor-section-title"><span>3</span><div><h2>Kargo ölçüleri ve desi</h2><p>Doğrudan desi girin veya ölçülerden otomatik hesaplamayı açın.</p></div></div>{calculateDesi ? <div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : '—'}</strong></div></div> : <div className="manual-desi-field"><label>Desi<input value={form.desi} onChange={event => updateField('desi', event.target.value)} type="number" min="0.01" step="0.01" required /></label><small>Varsayılan değer 1’dir.</small></div>}<label className="desi-calculation-toggle"><input type="checkbox" checked={calculateDesi} onChange={event => setCalculateDesi(event.target.checked)} /><span><strong>Desiyi ölçülerden hesapla</strong><small>En × boy × yükseklik / 3000 formülü kullanılır.</small></span></label></section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>4</span><div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilir veya internetten erişilebilen HTTPS adresleri ekleyebilirsiniz.</p></div></div><label className="upload-ghost-box product-media-upload"><input type="file" accept="image/jpeg,image/png" multiple onChange={event => setMediaFiles(Array.from(event.target.files ?? []).slice(0, 8))} /><strong>{mediaFiles.length ? `${mediaFiles.length} dosya seçildi` : 'Ürün görsellerini dosya olarak seç'}</strong><small>En fazla 8 adet JPEG veya PNG, dosya başına 10 MB</small></label><label>Görsel URL listesi<textarea value={form.mediaUrls} onChange={event => updateField('mediaUrls', event.target.value)} placeholder="İsteğe bağlı: Her satıra bir HTTPS görsel adresi girin" /></label>{(mediaUrls.length > 0 || mediaFiles.length > 0) && <div className="media-preview-strip">{mediaFiles.map((file, index) => <figure key={`${file.name}-${file.lastModified}`}><img src={URL.createObjectURL(file)} alt={`${form.title || 'Ürün'} ${index + 1}`} /><figcaption>{index === 0 && !mediaUrls.length ? 'Ana görsel' : file.name}</figcaption></figure>)}{mediaUrls.slice(0, 8 - mediaFiles.length).map((url, index) => <figure key={url}><img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} /><figcaption>{index === 0 && !mediaFiles.length ? 'Ana görsel' : `${index + 1}. görsel`}</figcaption></figure>)}</div>}</section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>5</span><div><h2>Ürün özellikleri</h2><p>Bilgiler kategori &amp; özellik eşleme sayfasındaki kategori özellik başlıklarından gelir.</p></div></div>{!form.categoryId ? <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde o kategoriye bağlanan özellikler burada görünür.</p></div> : requirements.isLoading ? <p>Kategori özellikleri yükleniyor…</p> : requirements.isError ? <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Önce kategori eşleme ekranında ilgili kategorinin özellik başlıklarını hazırlayın.</p></div> : <div className="attribute-builder-list">{(requirements.data ?? []).sort((a, b) => a.displayOrder - b.displayOrder).map(item => <article className="attribute-builder-card" key={item.attributeId}><div className="attribute-builder-head"><label className="attribute-builder-toggle"><input type="checkbox" checked={variantAttributeIds.includes(item.attributeId)} onChange={() => toggleVariantAttribute(item.attributeId)} disabled={!item.attribute.values.length} /> <span>{item.attribute.name}{item.isRequired ? ' *' : ''}</span></label><small>{item.attribute.values.length} değer · {variantAttributeIds.includes(item.attributeId) ? 'varyant özelliği' : 'ürün özelliği'}</small></div>{item.attribute.values.length ? <div className="option-chip-list">{item.attribute.values.map(value => <button type="button" key={value.id} className={`option-chip ${(attributeSelections[item.attributeId] ?? []).includes(value.id) ? 'active' : ''}`} onClick={() => toggleAttributeValue(item.attributeId, value.id)}>{value.value}</button>)}</div> : item.attribute.dataType === 'BOOLEAN' ? <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label> : <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder="Değer girin" /></label>}</article>)}</div>}</section>

      <section className="panel product-step-card"><div className="editor-section-title"><span>6</span><div><h2>Ürün seçenek grupları</h2><p>İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</p></div></div><div className="variant-toolbar"><button type="button" onClick={generateVariants}>Ürünleri ekle</button><button type="button" className="secondary" onClick={clearVariants}>Oluşan varyantları temizle</button><span>{variantRows.length}/{MAX_VARIANTS} varyant</span></div>{variantRows.length > 0 && <div className="variant-bulk-editor"><input value={bulkStock} onChange={event => setBulkStock(event.target.value)} type="number" min="0" placeholder="Tüm stoklar" /><input value={bulkSalePrice} onChange={event => setBulkSalePrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm satış fiyatları" /><input value={bulkListPrice} onChange={event => setBulkListPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm liste fiyatları" /><button type="button" className="secondary" onClick={applyBulk}>Tümüne uygula</button></div>}<div className="variant-table-editor"><div className="variant-table-head"><span>Seçenek</span><span>Barkod</span><span>Stok kodu</span><span>Stok</span><span>Fiyat</span><span>Liste fiyatı</span><span>İşlem</span></div>{variantRows.length ? variantRows.map(row => <div className="variant-table-row" key={row.key}><input value={row.optionSignature} readOnly /><input value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" /><input value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" /><input value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" /><input value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /><input value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /><button type="button" className="secondary" onClick={() => setVariantRows(rows => rows.filter(item => item.key !== row.key))}>Sil</button></div>) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini işaretleyip “Ürünleri ekle” dediğinizde varyant satırları burada oluşur.</p></div>}</div></section>
    </div><aside className="panel publish-channel-panel"><div className="editor-section-title"><span>7</span><div><h2>Yayınlanacak kanallar</h2><p>Seçilen aktif Trendyol bağlantılarında fiyat teklifi, listing profile ve yayın işi hazırlanır.</p></div></div><div className="channel-choice-list">{activeConnections.map(item => <label key={item.id} className="channel-choice"><input type="checkbox" checked={selectedChannelIds.includes(item.id)} onChange={() => updateChannel(item.id)} /> <span>{item.displayName}</span><small>{selectedChannelIds.includes(item.id) ? 'Seçildi' : 'Seçilmedi'}</small></label>)}{!activeConnections.length && <p>ACTIVE Trendyol bağlantısı bulunamadı.</p>}</div><div className="channel-help"><strong>Güvenli yayın</strong><p>Stage manuel yayın; aktif bağlantı, doğrulanmış kimlik bilgisi, geçerli ürün verisi ve tekrar korumasıyla çalışır. Production yayınında master ve bağlantı dış-yazma anahtarları ayrıca zorunludur.</p></div></aside></div>

    <section className="product-submit-sticky"><div><strong>Ürün kayda hazır</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><button disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Ürünü kaydet'}</button></section>
    <ErrorBox error={error ?? categories.error ?? brands.error ?? connections.error} />{notice && <p className="notice" role="status">{notice}</p>}{created && <p className="success">Oluşturuldu: <Link to={`/products/${created.id}`}>ürünü aç</Link></p>}
  </form></Page>
}

export function ProductDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [notice, setNotice] = useState(''); const [description, setDescription] = useState('')
  const query = useQuery({ queryKey: ['product', id], queryFn: () => hubApi<Product>(`/products/${id}`), enabled: !!id })
  const connections = useQuery({ queryKey: ['connections', 'product-publication'], queryFn: () => hubApi<PageData<TrendyolConnection>>('/connections?limit=200') })
  const status = useQuery({ queryKey: ['publication-status', id, connectionId], queryFn: () => hubApi<PublicationStatus>(`/products/${id}/publication-status/${connectionId}`), enabled: !!id && !!connectionId, retry: false })
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && item.status === 'ACTIVE') ?? []
  useEffect(() => { if (query.data) setDescription(query.data.description ?? '') }, [query.data?.id, query.data?.description])
  const refresh = async () => { await client.invalidateQueries({ queryKey: ['product', id] }); await client.invalidateQueries({ queryKey: ['products'] }) }
  async function updateProduct(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/products/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${query.data.version}"` }, body: JSON.stringify({ title: data.get('title'), description, brandId: query.data.brandId, categoryId: query.data.categoryId }) }); setNotice('Ürün bilgileri güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Ürün güncellenemedi.') } }
  async function image(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: query.data.id, variantId: null, url: data.get('imageUrl'), mediaRole: 'PRIMARY', sortOrder: 0, altText: query.data.title }) }); setNotice('Ana görsel güncellendi.'); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel güncellenemedi.') } }
  async function uploadImage(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const source = new FormData(event.currentTarget); const file = source.get('file'); if (!(file instanceof File) || !file.size) return; const body = new FormData(); body.set('file', file); body.set('productId', query.data.id); body.set('mediaRole', 'PRIMARY'); body.set('sortOrder', '0'); body.set('altText', query.data.title); try { await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body }); setNotice('Ana görsel dosyadan güncellendi.'); event.currentTarget.reset(); await refresh() } catch (error) { setNotice(error instanceof Error ? error.message : 'Görsel yüklenemedi.') } }
  async function run(path: string, body: object) { try { setNotice(''); const jobId = await hubApi<string>(path, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); setNotice(`İş kuyruğa alındı: ${jobId}`); await client.invalidateQueries({ queryKey: ['publication-status', id, connectionId] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.') } }
  return <Page title={query.data?.title ?? 'Ürün'} eyebrow="Ürün detayı">{query.isError ? <ErrorBox error={query.error} /> : !query.data ? <p>Yükleniyor…</p> : <>
    {notice && <div role="status" className="notice">{notice}</div>}<div className="detail-grid product-edit-overview"><article className="panel product-detail-hero">{query.data.primaryImageUrl ? <img src={query.data.primaryImageUrl} alt={query.data.title} /> : <span className="product-image-placeholder">Görsel yok</span>}<div><Tag>{query.data.status}</Tag><p>Model: {query.data.modelCode ?? '—'}</p><p>Stok: <strong>{query.data.totalStock}</strong></p><p>Başlangıç fiyatı: <strong>{money(query.data.startingPrice, query.data.currency)}</strong></p><p>Aktif platformlar: {query.data.activePlatforms?.join(', ') || '—'}</p></div></article><form className="panel product-step-card product-edit-form" onSubmit={updateProduct}><div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Oluşturma ekranındaki alan yapısıyla ürünü düzenleyin.</p></div></div><label>Ürün adı<input name="title" defaultValue={query.data.title} required /></label><label>Açıklama<RichTextEditor value={description} onChange={setDescription} /></label><button>Bilgileri kaydet</button></form></div>
    <div className="detail-grid product-edit-media"><form className="panel product-step-card" onSubmit={uploadImage}><div className="editor-section-title"><span>2</span><div><h2>Ürün görseli yükle</h2><p>JPEG veya PNG dosyasını doğrudan özel depolamaya yükleyin.</p></div></div><label className="upload-ghost-box product-media-upload"><input name="file" type="file" accept="image/jpeg,image/png" required /><strong>Dosya seç</strong><small>En fazla 10 MB</small></label><button>Görseli yükle</button></form><form className="panel product-step-card" onSubmit={image}><div className="editor-section-title"><span>3</span><div><h2>Görsel adresi</h2><p>İsteğe bağlı olarak HTTPS görsel adresi kullanın.</p></div></div><label>Görsel URL<input name="imageUrl" type="url" defaultValue={query.data.primaryImageUrl ?? ''} required /></label><button>Görseli kaydet</button></form></div>
    <article className="panel"><h2>Varyantlar, stok ve fiyat</h2>{query.data.variants.map(variant => <section className="variant-editor" key={variant.id}><div><strong>{variant.sku}</strong><span>Barkod: {variant.barcode ?? '—'} · Model: {variant.modelCode ?? '—'} · Ölçü: {variant.width ?? '—'} × {variant.length ?? '—'} × {variant.height ?? '—'} cm · Ağırlık: {variant.weight ?? '—'} kg</span></div><VariantQuickEditor variant={variant} connections={activeConnections} onChanged={refresh} /></section>)}</article>
    <article className="panel"><h2>Trendyol yayın yönetimi</h2><p className="notice">Stage bağlantısında manuel yayın ve güncelleme doğrudan sağlayıcıya gönderilir. Production’da aktif bağlantı ve dış-yazma anahtarları gerekir.</p><label>Aktif Trendyol bağlantısı<select aria-label="Ürün Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>{connectionId && <><div className="actions spaced"><button onClick={() => run(`/products/${id}/publication-jobs`, { connectionId })}>Yeni ürün olarak yayınla</button><button className="secondary" onClick={() => run(`/products/${id}/update-jobs`, { connectionId })}>Trendyol ürününü güncelle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: true })}>Trendyol'da arşivle</button><button className="secondary" onClick={() => run(`/products/${id}/archive-jobs`, { connectionId, archived: false })}>Arşivden çıkar</button></div>{status.isLoading ? <p>Yayın durumu yükleniyor…</p> : status.isError ? <p className="notice">Henüz listing profili veya yayın durumu yok.</p> : status.data && <dl className="details"><dt>Gerçek durum</dt><dd>{status.data.actualStatus ?? '—'}</dd><dt>Son job</dt><dd>{status.data.lastJobStatus ?? '—'}</dd><dt>Ret kodu</dt><dd>{status.data.lastRejectionCode ?? '—'}</dd></dl>}</>}</article>
  </>}</Page>
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
