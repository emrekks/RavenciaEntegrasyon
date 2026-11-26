// Ürünler › Stok Takibi › Düşük Stok: the models with sold-out variants, two closed cards to a row, one open at a time.
import { memo, useCallback, useDeferredValue, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { loadAllPages } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { TitleHint } from '../../../shared/components'
import { EmptyState } from '../../../shared/components/EmptyState'
import { PanelSelect } from '../../../shared/PanelSelect'
import { RrError, RrIcon, RrLoading, RrModal, RrThumb } from '../../../shared/ui/rr-components'
import { ViewTabsWithMore } from '../../../shared/view-tabs'
import { readProductListPage, type ProductListItem } from '../product-list-item'
import { colourFirstOptions } from '../variant-option-matching'
import { Product, Variant, formatCatalogOptionLabel, isColorOptionName, isVariantOptionName, preferredColorOption, variantOptionEntries } from '../shared'
import { groupByFamily } from './product-family-key'
import { productGroupImage } from './product-group-image'
import { variantAxisParts } from './ProductQuickEditModal'

type LowStockGroup = { id: string; primary: Product; products: Product[]; variants: Array<{ product: Product; variant: Variant }> }
type ImageClick = (url: string, title: string, modelCode?: string) => void
type LowStockSort = 'NEWEST' | 'MODEL_ASC' | 'MODEL_DESC' | 'MISSING_DESC' | 'MISSING_ASC'

// One collator per option set: String.prototype.localeCompare with a locale and options builds a new collator on every
// call (the window's sorts and searches made thousands of them); Intl.Collator#compare gives the same order.
const trBase = new Intl.Collator('tr', { sensitivity: 'base' })
const trNumeric = new Intl.Collator('tr', { numeric: true, sensitivity: 'base' })

// One card per model family: brand + model code, by the server's key (another brand's cards with the same code stay apart).
function lowStockGroups(products: Product[]): LowStockGroup[] {
  return groupByFamily(products).map(({ id, members }) => ({
    id,
    primary: members[0],
    products: members,
    variants: members.flatMap(product => product.variants.map(variant => ({ product, variant })))
  }))
}
function lowStockModelCode(group: LowStockGroup) {
  return group.products.map(product => product.modelCode).find(value => value?.trim())
    ?? group.variants.map(item => item.variant.modelCode).find(value => value?.trim())
    ?? 'Model kodu yok'
}
function lowStockProductColor(product: Product) {
  for (const variant of product.variants) {
    const color = preferredColorOption(variantOptionEntries(variant))
    if (color?.value) return color.value
  }
  return 'Renk belirtilmemiş'
}
function productCategoryLabel(product: Product) {
  return product.categoryPath?.trim() || 'Kategori belirtilmemiş'
}
function lowStockVariantLabel(variant: Variant) {
  const options = colourFirstOptions(variantOptionEntries(variant)
    .sort((left, right) => trBase.compare(left.name, right.name) || trNumeric.compare(left.value, right.value)))
  return options.map(option => `${option.name}: ${option.value}`).join(' · ') || variant.optionSignature || variant.sku
}
function lowStockProductImage(product: Product) {
  return product.primaryImageUrl
    ?? product.variants.flatMap(variant => variant.mediaUrls ?? [])[0]
    ?? null
}
// The variant's own options (size and the like) without the colour, which already heads the list; the long attribute
// list is only a fallback for variants that carry no options.
function lowStockSizeLabel(variant: Variant) {
  const parts = variantAxisParts(variant.optionSignature).filter(part => !isColorOptionName(part.name))
  if (parts.length) return parts.map(part => `${part.name}: ${part.value}`).join(' · ')
  // Imported variants may carry every product attribute; only the variant-defining ones (size) name the variant.
  const sizes = variantOptionEntries(variant).filter(option => isVariantOptionName(option.name) && !isColorOptionName(option.name))
  return sizes.length ? sizes.map(option => `${formatCatalogOptionLabel(option.name)}: ${option.value}`).join(' · ') : variant.sku
}
function lowStockMissingVariantCount(group: LowStockGroup) {
  return group.variants.filter(item => item.variant.onHand <= 0).length
}

// What the filters and sorts read of a model, worked out once per list instead of on every keystroke: its code, its
// search text (the same joined, lower-cased text as before, so a query matches exactly the models it matched), each
// card's own text and category, and its newest card's place in the list.
type IndexedGroup = { group: LowStockGroup; modelCode: string; text: string; productText: Map<Product, string>; category: Map<Product, string>; missing: number; newestRank: number; productRank: Map<string, number> }
const newestRank = (group: LowStockGroup, productRank: Map<string, number>) => Math.min(...group.products.map(product => productRank.get(product.id) ?? Number.MAX_SAFE_INTEGER))
function indexGroups(products: Product[]): IndexedGroup[] {
  const productRank = new Map(products.map((product, index) => [product.id, index]))
  return lowStockGroups(products).map(group => {
    const modelCode = lowStockModelCode(group)
    const parts = new Map(group.products.map(product => [product, [product.title, productCategoryLabel(product), lowStockProductColor(product), ...product.variants.map(lowStockVariantLabel)]]))
    return {
      group,
      modelCode,
      text: [modelCode, ...group.products.flatMap(product => parts.get(product)!)].join(' ').toLocaleLowerCase('tr-TR'),
      productText: new Map([...parts].map(([product, values]) => [product, values.join(' ').toLocaleLowerCase('tr-TR')])),
      category: new Map(group.products.map(product => [product, productCategoryLabel(product)])),
      missing: lowStockMissingVariantCount(group),
      newestRank: newestRank(group, productRank),
      productRank
    }
  })
}
// The filtered models, each with the cards and variants the search and category leave; a model left whole keeps its
// object, so its card does not render again.
function filterGroups(indexed: IndexedGroup[], search: string, categoryFilter: string, sort: LowStockSort): IndexedGroup[] {
  const query = search.trim().toLocaleLowerCase('tr-TR')
  return indexed.flatMap(entry => {
    const { group } = entry
    if (query && !entry.text.includes(query)) return []
    if (categoryFilter && !group.products.some(product => entry.category.get(product) === categoryFilter)) return []
    if (!query && !categoryFilter) return [entry]
    const matchingProducts = query ? group.products.filter(product => entry.productText.get(product)!.includes(query)) : []
    const categoryProducts = categoryFilter ? group.products.filter(product => entry.category.get(product) === categoryFilter) : group.products
    const visibleProducts = matchingProducts.length > 0 ? matchingProducts.filter(product => categoryProducts.includes(product)) : categoryProducts
    if (visibleProducts.length === group.products.length) return [entry]
    const visibleVariants = matchingProducts.length > 0
      ? group.variants.filter(item => matchingProducts.includes(item.product) && categoryProducts.includes(item.product))
      : group.variants.filter(item => categoryProducts.includes(item.product))
    const narrowed = { ...group, products: visibleProducts, variants: visibleVariants }
    // A narrowed model is ranked, coded and counted by the cards it keeps, as before.
    return [{ ...entry, group: narrowed, modelCode: lowStockModelCode(narrowed), missing: lowStockMissingVariantCount(narrowed), newestRank: newestRank(narrowed, entry.productRank) }]
  }).filter(entry => entry.group.products.length > 0).sort((left, right) => {
    if (sort === 'NEWEST') return left.newestRank - right.newestRank
    if (sort === 'MODEL_DESC') return trNumeric.compare(right.modelCode, left.modelCode)
    if (sort === 'MISSING_DESC') return right.missing - left.missing || trNumeric.compare(left.modelCode, right.modelCode)
    if (sort === 'MISSING_ASC') return left.missing - right.missing || trNumeric.compare(left.modelCode, right.modelCode)
    return trNumeric.compare(left.modelCode, right.modelCode)
  })
}

const noProducts: Product[] = []

// A closed card renders only when its own model, its open state or its open colour changes (React.memo): opening one
// model no longer renders the other cards of the list again.
const LowStockModelCard = memo(function LowStockModelCard({ group, modelCode, missing, open, openColorId, onToggle, onToggleColor, onImageClick }: { group: LowStockGroup; modelCode: string; missing: number; open: boolean; openColorId: string | null; onToggle: (id: string) => void; onToggleColor: (id: string) => void; onImageClick: ImageClick }) {
  const image = productGroupImage(group) ?? lowStockProductImage(group.primary)
  return <article className={`rr-lowf${open ? ' rr-open rr-lowf-full' : ''}`} data-low-model={group.id}>
    <div className="rr-lowf-h"><RrThumb src={image} alt={group.primary.title} onOpen={image ? () => onImageClick(image, group.primary.title, group.products.map(item => item.modelCode).find(value => value?.trim()) ?? undefined) : undefined} /><button type="button" className="rr-lowf-t" aria-expanded={open} onClick={() => onToggle(group.id)}><span><small>Model Kodu</small><b className="rr-num">{modelCode}</b><em title={group.primary.title}>{group.primary.title}</em></span><span className="rr-lowf-c" title={`${missing} eksik · ${group.products.length} renk`}><b className="rr-num">{missing}</b></span><RrIcon name="chev" /></button></div>
    {open && <section>{[...group.products].sort((left, right) => trBase.compare(lowStockProductColor(left), lowStockProductColor(right))).map(product => { const lowVariants = product.variants.filter(variant => variant.onHand <= 0).sort((left, right) => trNumeric.compare(lowStockSizeLabel(left), lowStockSizeLabel(right))); const colorOpen = openColorId === product.id; return <div className={`rr-lowc${colorOpen ? ' rr-open' : ''}`} key={product.id}><button type="button" className="rr-lowc-h" aria-expanded={colorOpen} onClick={() => onToggleColor(product.id)}><b>{lowStockProductColor(product)}</b><span className="rr-lowc-n">{lowVariants.length} eksik varyant</span><RrIcon name="chev" /></button>{colorOpen && (lowVariants.length ? <div className="rr-lowvs">{lowVariants.map(variant => <div className="rr-lowv" key={variant.id}><span><b>{lowStockSizeLabel(variant)}</b><small>Stok Kodu: {variant.sku}{variant.barcode ? ` · Barkod: ${variant.barcode}` : ''}</small></span><span className={variant.onHand === 0 ? 'rr-zero' : 'rr-low'}>{variant.onHand === 0 ? 'Stoksuz' : <>Kalan <b className="rr-num">{variant.onHand}</b></>}</span></div>)}</div> : <p className="rr-lead">Bu renkte kritik stok varyantı bulunamadı.</p>)}</div> })}</section>}
  </article>
})

// Two lists: Düşük Stok Renkleri (a colour with at least half its variants sold out, a whole colour too, while the model
// still has stock: the products page's low-stock count)
// and Tükenen Varyantlar (any variant sold out, a whole colour too, while the model still has stock, read when its tab is opened).
export function LowStockDetailsModal({ products: lowProducts, loading: lowLoading, error: lowError, onClose, onImageClick }: { products: Product[]; loading: boolean; error: unknown; onClose: () => void; onImageClick: ImageClick }) {
  const [mode, setMode] = useState<'LOW' | 'GAP'>('LOW')
  const gapQuery = useQuery({ queryKey: queryKeys.products('sold-out-variant-details'), queryFn: ({ signal }) => loadAllPages<ProductListItem>('/products?stock=GAP', { limit: 200, signal }).then(readProductListPage), enabled: mode === 'GAP', staleTime: 15_000 })
  const gapProducts = gapQuery.data?.items
  // Each tab's models are grouped and indexed once per answer, so switching back to a tab reuses them.
  const lowIndex = useMemo(() => indexGroups(lowProducts), [lowProducts])
  const gapIndex = useMemo(() => indexGroups(gapProducts ?? noProducts), [gapProducts])
  const low = mode === 'LOW'
  const products = low ? lowProducts : gapProducts ?? noProducts
  const indexed = low ? lowIndex : gapIndex
  const loading = low ? lowLoading : gapQuery.isLoading
  const error = low ? lowError : gapQuery.error
  const [search, setSearchValue] = useState('')
  const [categoryFilter, setCategoryValue] = useState('')
  // The list comes newest first (like the products page), so a model's rank is its newest card's place in it.
  const [sort, setSortValue] = useState<LowStockSort>('NEWEST')
  // The typed text shows at once; the list follows it at a lower priority, so a keystroke never waits for the cards.
  const deferredSearch = useDeferredValue(search)
  const categoryOptions = useMemo(() => [...new Set(products.map(productCategoryLabel))].sort((left, right) => trBase.compare(left, right)), [products])
  const filteredGroups = useMemo(() => filterGroups(indexed, deferredSearch, categoryFilter, sort), [indexed, deferredSearch, categoryFilter, sort])
  const lowVariantCount = filteredGroups.reduce((sum, entry) => sum + entry.missing, 0)
  const catalogRecordCount = filteredGroups.reduce((sum, entry) => sum + entry.group.products.length, 0)
  // Model cards start closed and sit two to a row.
  // One model and one colour are open at a time (an accordion): opening another closes the one that was open; the open
  // model takes a full row of the grid and scrolls into view.
  const [openFamily, setOpenFamily] = useState<string | null>(null)
  const [openColor, setOpenColor] = useState<string | null>(null)
  const toggleFamily = useCallback((id: string) => {
    setOpenFamily(current => {
      if (current === id) return null
      window.requestAnimationFrame(() => document.querySelector(`[data-low-model="${id}"]`)?.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' }))
      return id
    })
  }, [])
  const toggleColor = useCallback((id: string) => setOpenColor(current => current === id ? null : id), [])
  // A new search, category, sort or tab closes the open model (in the same render as the change, not one after it).
  const closeOpen = () => { setOpenFamily(null); setOpenColor(null) }
  const setSearch = (value: string) => { setSearchValue(value); closeOpen() }
  const setCategoryFilter = (value: string) => { setCategoryValue(value); closeOpen() }
  const setSort = (value: LowStockSort) => { setSortValue(value); closeOpen() }
  // The page passes a new image handler on every render; the cards get one that never changes.
  const imageClickRef = useRef(onImageClick)
  useLayoutEffect(() => { imageClickRef.current = onImageClick })
  const imageClick = useCallback<ImageClick>((url, title, modelCode) => imageClickRef.current(url, title, modelCode), [])

  return <RrModal crumb="Stok Takibi" label="Düşük Stok" title={<>Düşük Stok<TitleHint label="Düşük Stok">Model koduna göre gruplanmış, tükenen varyantı olan ürünler. <b>Düşük stok:</b> stoğu süren modelde bir rengin varyantlarının en az yarısı tükenmiş; tamamen biten renkler de dahil. <b>Tükenen varyant:</b> stoğu süren modelde en az bir beden, seçenek ya da renk bitmiş.</TitleHint></>} size="lg" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button></>}>
    <ViewTabsWithMore className="rr-low-tabs" more={[]} ariaLabel="Düşük stok" value={mode} onChange={value => { setMode(value as typeof mode); closeOpen() }} items={[{ value: 'LOW', label: 'Düşük Stok Renkleri' }, { value: 'GAP', label: 'Tükenen Varyantlar' }]} />
    {loading ? <RrLoading text={low ? 'Düşük stok ürünleri yükleniyor…' : 'Tükenen varyantlar yükleniyor…'} /> : error ? <RrError error={error} /> : !indexed.length ? <EmptyState icon="stock" title={low ? 'Düşük Stok Ürün Yok' : 'Tükenen Varyant Yok'}>Tüm ürünlerin stoğu yeterli.</EmptyState> : <>
      <div className="rr-low-tools"><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} value={search} onChange={event => setSearch(event.target.value)} placeholder="Model kodu, ürün veya renk ara" aria-label="Stok eksiklerinde ara" /></span><PanelSelect ariaLabel="Kategori" value={categoryFilter} onChange={setCategoryFilter} width={220} options={[{ value: '', label: 'Tüm Kategoriler' }, ...categoryOptions.map(category => ({ value: category, label: category }))]} /><PanelSelect ariaLabel="Sıralama" value={sort} onChange={setSort} width={260} options={[{ value: 'NEWEST', label: 'Eklenme Sırası (Yeniden Eskiye)' }, { value: 'MODEL_ASC', label: 'Model Kodu (A-Z)' }, { value: 'MODEL_DESC', label: 'Model Kodu (Z-A)' }, { value: 'MISSING_DESC', label: 'Eksik Varyant (Çoktan Aza)' }, { value: 'MISSING_ASC', label: 'Eksik Varyant (Azdan Çoğa)' }]} /></div>
      <p className="rr-lead">{filteredGroups.length} model · {catalogRecordCount} katalog kaydı · {lowVariantCount} eksik varyant</p>
      {!filteredGroups.length && <p className="rr-lead">Arama veya filtreye uyan ürün bulunamadı.</p>}
      <div className="rr-lowgrid">{filteredGroups.map(entry => { const open = openFamily === entry.group.id; return <LowStockModelCard key={entry.group.id} group={entry.group} modelCode={entry.modelCode} missing={entry.missing} open={open} openColorId={open ? openColor : null} onToggle={toggleFamily} onToggleColor={toggleColor} onImageClick={imageClick} /> })}</div>
    </>}
  </RrModal>
}
