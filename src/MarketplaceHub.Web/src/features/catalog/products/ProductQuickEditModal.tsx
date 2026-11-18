import { useEffect, useRef, useState, type CSSProperties, type FormEvent, type InputHTMLAttributes } from 'react'
import { variantOptionParts } from '../variant-option-label'
import { colourFirstOptions } from '../variant-option-matching'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { TitleHint } from '../../../shared/components'
import { RrIcon, RrModal } from '../../../shared/ui/rr-components'
import { type NotificationContext } from '../../../shared/notifications'
import { updateVariantGroupSelection } from '../variant-bulk-edit'
import { applyVariantStockSets, bulkEditSummary, isStaleStockError, parseStockInput, setVariantOnHand, stockTarget, type VariantStockSet } from '../variant-stock-set'
import { bulkEditValues, channelOffer, channelTakesListPrice, inlinePrices, PANEL_PRICE_CHOICE, panelPriceIsFallback, panelPrices, panelPriceTarget, formatPriceField, initialCustomChannels, priceFieldText, panelFollowChoiceVariantIds, panelFollowerColumns, panelFollowerWrites, quickEditOfferPrices, quickInlinePrices, quickPriceBody, quickPriceColumns, quickPriceTarget, type QuickPriceColumn } from '../quick-price-edit'
import { formatMoney } from '../../../shared/format-money'
import { quickEditVariantItems, quickStockCellValue, type SavedStock } from './quick-edit-products'
import { applyQuickEditDraft, quickEditDraftValues, type QuickEditDraftChange, type QuickEditDraftRow, type QuickEditDraftValues } from './quick-edit-draft'
import { PanelSelect, type PanelSelectOption } from '../../../shared/PanelSelect'
import { PlatformSquareMark } from '../../../shared/platform-square-mark'
import { QuickEditColorRows, buildMatrix, colorRowsLayout, type MatrixLine } from './QuickEditColorRows'
import { DraftSaveBar, useDraftEdits, useUnsavedChangesGuard } from '../../../shared/draft-edits'
import { MarketplaceConnection, Product, Variant, compareLabelsAlphabetically, compareOptionValues, compareVariantOptionSignatures, formatCatalogOptionLabel, isVariantOptionName, preferredColorOption, variantColorKey, variantOptionEntries } from '../shared'

function compareVariantItemsAlphabetically(left: { variant: { optionSignature?: string | null; sku: string } }, right: { variant: { optionSignature?: string | null; sku: string } }) {
  const leftLabel = (left.variant.optionSignature || left.variant.sku).trim()
  const rightLabel = (right.variant.optionSignature || right.variant.sku).trim()
  return compareVariantOptionSignatures(leftLabel, rightLabel)
    || compareLabelsAlphabetically(left.variant.sku, right.variant.sku)
}
// The options that tell one variant from another (colour, size, number). Imported variants carry every product
// attribute in their signature ("Renk: Yeşil | Beden: XL | Boy: Regular | Cinsiyet: …"); only these name the variant.
export function variantAxisParts(signature: string | null | undefined) {
  const parts = colourFirstOptions(variantOptionParts(signature))
  const axes = parts.filter(part => isVariantOptionName(part.name))
  return axes.length ? axes : parts.slice(0, 2)
}

// The selected variants as one chip per first option, the rest joined: "Bej · L - XL - 2XL" instead of a chip each.
function selectionChips(variants: Array<Pick<Variant, 'optionSignature' | 'sku'>>) {
  const groups = new Map<string, string[]>()
  for (const variant of variants) {
    const values = variantAxisParts(variant.optionSignature).map(part => part.value)
    const head = values.length > 1 ? values[0] : values.join(' · ') || variant.sku
    const rest = values.length > 1 ? values.slice(1).join(' · ') : ''
    const list = groups.get(head) ?? []
    if (rest && !list.includes(rest)) list.push(rest)
    groups.set(head, list)
  }
  return [...groups].map(([head, rest]) => rest.length ? `${head} · ${rest.join(' - ')}` : head)
}
export type QuickEditMode = 'stock' | 'price'
// The product editor's rows, given instead of saved products: the window edits them on screen only (see quick-edit-draft.ts).
export type QuickEditDraft = { title: string; modelCode: string; rows: QuickEditDraftRow[]; onApply: (changes: QuickEditDraftChange[]) => void }
const DRAFT_PRODUCT_ID = 'editor-draft'
// The editor's rows as the window's products: one card holding every row, so the colour groups and Renk / Beden
// pickers work on them as on saved variants.
function draftProduct(draft: QuickEditDraft): Product {
  const variants = draft.rows.map(row => ({ id: row.key, productId: DRAFT_PRODUCT_ID, sku: row.sku, barcode: row.barcode, modelCode: draft.modelCode, optionSignature: row.optionSignature, options: row.options, onHand: row.stock, costPrice: row.costPrice, defaultListPrice: row.listPrice, defaultSalePrice: row.salePrice }) as unknown as Variant)
  return { id: DRAFT_PRODUCT_ID, title: draft.title, modelCode: draft.modelCode, variants } as unknown as Product
}
const noChange = async () => undefined
// A price cell's unsaved text, as typed (Liste / Satış); keyed by variant and price column.
type PriceDraft = { list: string; sale: string }
const samePriceDraft = (left: PriceDraft, right: PriceDraft) => left.list === right.list && left.sale === right.sale
const priceKey = (variantId: string, columnId: string) => `${variantId}|${columnId}`
export function ProductQuickEditModal({ products: savedProducts = [], connections = [], mode = 'stock', onChanged = noChange, onClose, onResult, draft }: { products?: Product[]; connections?: MarketplaceConnection[]; mode?: QuickEditMode; onChanged?: () => Promise<unknown>; onClose: () => void; onResult?: (title: string, message: string, kind: 'success' | 'error' | 'info', context?: NotificationContext) => void; draft?: QuickEditDraft }) {
  // Opened from the editor, the rows are read once: what is typed here stays here until it is applied.
  const [draftProducts] = useState(() => draft ? [draftProduct(draft)] : null)
  const products = draftProducts ?? savedProducts
  const [draftValues, setDraftValues] = useState<Record<string, QuickEditDraftValues>>(() => Object.fromEntries((draft?.rows ?? []).map(row => [row.key, quickEditDraftValues(row)])))
  const setDraftValue = (key: string, field: keyof QuickEditDraftValues, value: string) => { setNotice(''); setDraftValues(current => ({ ...current, [key]: { ...current[key], [field]: value } })) }
  const [costAmount, setCostAmount] = useState('')
  // Typing in the table makes drafts only (owner, 18 Nov 2026): nothing is saved, and nothing goes to a marketplace, until
  // the person presses Kaydet in the bar and confirms. Tümüne Uygula fills drafts too.
  const stockDrafts = useDraftEdits<string>()
  const priceDrafts = useDraftEdits<PriceDraft>(samePriceDraft)
  const followDrafts = useDraftEdits<boolean>()
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({})
  const savedStocks = useRef(new Map<string, SavedStock>())
  const [confirmClose, setConfirmClose] = useState(false)
  const draftCount = stockDrafts.count + priceDrafts.count + followDrafts.count
  useUnsavedChangesGuard(draftCount > 0 && !draft)
  useEffect(() => { if (!draftCount) setConfirmClose(false) }, [draftCount])
  const clearRowError = (key: string) => setRowErrors(current => { if (!(key in current)) return current; const next = { ...current }; delete next[key]; return next })
  const contextProduct = products[0]
  const contextTitle = contextProduct?.title || 'Toplu düzenleme'
  const contextModelCode = contextProduct ? contextProduct.modelCode || contextProduct.variants.find(variant => variant.modelCode)?.modelCode || '—' : null
  // Each variant once, beside its own card: a product read by id lists its whole family's variants.
  const variants = quickEditVariantItems(products)
  const sortedVariants = [...variants].sort(compareVariantItemsAlphabetically)
  const groups = sortedVariants.reduce<Record<string, typeof variants>>((result, item) => {
    const color = variantColorKey(item.variant) || 'Diğer'
    ;(result[color] ??= []).push(item)
    return result
  }, {})
  const optionValue = (signature: string, labels: string[], fallback: string) => {
    const labelPattern = labels.join('|')
    return signature.match(new RegExp(`(?:${labelPattern})\\s*[:=]\\s*([^|_]+)`, 'i'))?.[1]?.trim() || fallback
  }
  const colorOf = (item: typeof variants[number]) => variantColorKey(item.variant) || 'Diğer'
  const colorLabelOf = (item: typeof variants[number]) => preferredColorOption(variantOptionEntries(item.variant))?.value.trim() || 'Diğer'
  const sizeOf = (item: typeof variants[number]) => optionValue(item.variant.optionSignature || '', ['BEDEN', 'SIZE'], item.variant.optionSignature || 'Ana varyant')
  const colorLabels = sortedVariants.reduce<Record<string, string>>((result, item) => { const key = colorOf(item); result[key] ??= formatCatalogOptionLabel(colorLabelOf(item)); return result }, {})
  const colorOptions = Object.keys(groups).sort((left, right) => compareLabelsAlphabetically(colorLabels[left] || left, colorLabels[right] || right))
  const [selectionDraft, setSelectionDraft] = useState<string[]>([])
  const [listPrice, setListPrice] = useState(''); const [salePrice, setSalePrice] = useState(''); const [stockAmount, setStockAmount] = useState('')
  const [stockAction, setStockAction] = useState<'SET' | 'ADD' | 'SUBTRACT'>('SET'); const [notice, setNotice] = useState(''); const [saving, setSaving] = useState(false)
  // Where a price change goes: the panel's own price (preset: it sends nothing to a marketplace) or one channel the
  // person picks (its offer updated, or created when the variant has none there). The screen never picks a channel.
  // The per-variant table: stock, the panel price and every channel's price side by side, each saved on its own.
  const priceColumns = quickPriceColumns(connections)
  const channelColumns = priceColumns.filter(column => column.id !== PANEL_PRICE_CHOICE)
  const showStockColumn = true; const showPriceColumns = true
  // Which value the matrix edits: Stok, or one price (the panel's Satış / Liste, each marketplace's). The editor's own rows
  // (draft) edit Stok, Maliyet, Liste and Satış. Drafts of the tabs not shown are kept.
  type ValueTab = { id: string; label: string; kind: 'stock' | 'price' | 'draft'; columnId?: string; field: 'stock' | 'sale' | 'list' | 'costPrice' | 'listPrice' | 'salePrice' }
  const valueTabs: ValueTab[] = draft
    ? [{ id: 'stock', label: 'Stok', kind: 'draft', field: 'stock' }, { id: 'costPrice', label: 'Maliyet', kind: 'draft', field: 'costPrice' }, { id: 'listPrice', label: 'Liste Fiyatı', kind: 'draft', field: 'listPrice' }, { id: 'salePrice', label: 'Satış Fiyatı', kind: 'draft', field: 'salePrice' }]
    : [
      ...(showStockColumn ? [{ id: 'stock', label: 'Stok', kind: 'stock', field: 'stock' } as ValueTab] : []),
      ...(showPriceColumns ? priceColumns.flatMap((column): ValueTab[] => {
        const name = column.id === PANEL_PRICE_CHOICE ? 'Panel' : column.label
        return column.takesListPrice
          ? [{ id: `${column.id}:sale`, label: `${name} Satış Fiyatı`, kind: 'price', columnId: column.id, field: 'sale' }, { id: `${column.id}:list`, label: `${name} Liste Fiyatı`, kind: 'price', columnId: column.id, field: 'list' }]
          : [{ id: `${column.id}:sale`, label: `${name} Fiyatı`, kind: 'price', columnId: column.id, field: 'sale' }]
      }) : [])
    ]
  const [activeTabId, setActiveTabId] = useState(mode === 'price' ? `${PANEL_PRICE_CHOICE}:sale` : '')
  const activeTab = valueTabs.find(item => item.id === activeTabId) ?? valueTabs[0]
  const activeColumn = activeTab?.kind === 'price' ? priceColumns.find(column => column.id === activeTab.columnId) : undefined
  const activeChannel = activeColumn && activeColumn.id !== PANEL_PRICE_CHOICE ? activeColumn : undefined
  const priceConnectionId = activeColumn?.id ?? PANEL_PRICE_CHOICE
  const priceChannelName = connections.find(connection => connection.id === priceConnectionId)?.displayName ?? null
  const stepValue: 'stock' | 'price' | 'draft' = draft ? 'draft' : activeTab?.kind === 'price' ? 'price' : 'stock'
  // "Özel Fiyat" per marketplace, as in the product editor: unticked, the marketplace sells at the panel price and a
  // panel price saved here is written to its offers too. The first state is each offer's stored choice, else what its
  // prices say (see initialCustomChannels); a marketplace not read yet counts as ticked, so nothing is overwritten.
  // The products list's copies (shown until each product is read by id) carry no channel offers (variant
  // platformStatuses), so the choice is read only once every row has them: until then each marketplace counts as ticked.
  const offersRead = variants.length > 0 && variants.every(item => item.variant.platformStatuses)
  const [derivedCustom, setDerivedCustom] = useState<Record<string, boolean> | null>(() => offersRead ? initialCustomChannels(variants, priceColumns) : null)
  if (derivedCustom == null && offersRead) setDerivedCustom(initialCustomChannels(variants, priceColumns))
  // The saved choices of this window's life (a saved switch keeps showing its value until the lists are read again).
  const [customOverrides, setCustomOverrides] = useState<Record<string, boolean>>({})
  const customChannels: Record<string, boolean> = Object.fromEntries(channelColumns.map(column => [column.id, followDrafts.get(column.id)?.value ?? customOverrides[column.id] ?? derivedCustom?.[column.id] ?? true]))
  // A switch only makes a draft, as a price cell does (owner, 18 Nov 2026): Kaydet writes the choice to every offer of this
  // window's variants on that marketplace (no price changes). Turning a channel to "follows the panel price" drops the
  // price drafts of its cells, which turn read-only.
  function setCustomChannel(connectionId: string, custom: boolean) {
    setNotice(''); clearRowError(`f:${connectionId}`)
    followDrafts.set(connectionId, custom, customOverrides[connectionId] ?? derivedCustom?.[connectionId] ?? true)
    if (!custom) priceDrafts.remove([...priceDrafts.entries.keys()].filter(key => key.endsWith(`|${connectionId}`)))
  }
  const followerColumns = panelFollowerColumns(priceColumns, customChannels)
  // A marketplace that takes no list price shows only Satış Fiyatı; its offer's list price follows the sale price.
  const showListPrice = priceConnectionId === PANEL_PRICE_CHOICE || channelTakesListPrice(connections.find(connection => connection.id === priceConnectionId)?.platformCode)
  // A first offer keeps its create key for this window's life, so a resend after a lost answer cannot make two.
  const offerCreateKeys = useRef(new Map<string, string>())
  const offerCreateKey = (variantId: string) => {
    const existing = offerCreateKeys.current.get(variantId)
    if (existing) return existing
    const created = newIdempotencyKey('channel-offer-create')
    offerCreateKeys.current.set(variantId, created)
    return created
  }
  // The stock each variant had when this dialog opened (or after this dialog saved it): the value a bulk change is
  // computed from and sent as the expected current stock. A refused (stale) change resets it to the refreshed list.
  const stockBaseline = useRef<Map<string, number>>(new Map())
  const resetStockBaseline = useRef(false)
  useEffect(() => {
    for (const item of variants) if (resetStockBaseline.current || !stockBaseline.current.has(item.variant.id)) stockBaseline.current.set(item.variant.id, item.variant.onHand)
    resetStockBaseline.current = false
  })
  const selectedSet = new Set(selectionDraft)
  const activeSelection = selectionDraft
  // Nothing ticked means every variant (owner, 11 Nov 2026): Renk / Beden left empty narrow nothing, so the window's
  // action goes to all of its variants; ticking rows (by hand or with Renk / Beden) narrows it to them.
  const targetIds = selectionDraft.length ? selectionDraft : sortedVariants.map(item => item.variant.id)
  const activeSelectedSet = selectedSet
  // Ticking a row, a column, the corner or one cell's mark adds or drops those variants (updateVariantGroupSelection).
  const selectIds = (ids: string[], checked: boolean) => setSelectionDraft(current => updateVariantGroupSelection(current, ids, checked))
  async function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (saving) return
    if (draft) {
      // The editor's rows: the cells as typed and the values below on the ticked rows, checked first, then given to
      // the editor's table. Nothing is saved or sent to a marketplace here; the editor's Kaydet does that.
      const result = applyQuickEditDraft(draft.rows, draftValues, new Set(targetIds), { listPrice, salePrice, costPrice: costAmount, stockAction, stockAmount }, row => variantAxisParts(row.optionSignature).map(part => part.value).join(' · ') || row.sku || 'Varyant')
      if (!result.ok) { setNotice(`Başarısız: ${result.message}`); return }
      draft.onApply(result.changes)
      onClose()
      return
    }
    if (!targetIds.length) { const message = 'Bu pencerede varyant yok.'; setNotice(message); onResult?.('Varyant Yok', message, 'error'); return }
    const targetSet = new Set(targetIds)
    const priceRequested = stepValue === 'price' && ((showListPrice && listPrice.trim() !== '') || salePrice.trim() !== ''); const stockRequested = stepValue === 'stock' && stockAmount.trim() !== ''
    if (!priceRequested && !stockRequested) { const message = 'Uygulanacak fiyat veya stok değerini girin.'; setNotice(message); onResult?.('Değer Girilmedi', message, 'error'); return }
    // Read as Turkish ("1.250" is 1250); a stock fraction is refused, not rounded.
    const values = bulkEditValues({ showListPrice, listPrice: stepValue === 'price' ? listPrice : '', salePrice: stepValue === 'price' ? salePrice : '', stockAmount: stepValue === 'stock' ? stockAmount : '' })
    if (!values.ok) { setNotice(values.message); onResult?.('Geçersiz Değer', values.message, 'error'); return }
    const { list, sale, amount } = values
    // Tümüne Uygula only fills the drafts of the table (owner, 18 Nov 2026): the values are checked and shown in their
    // cells, and Kaydet in the bar saves them. Nothing is sent from here.
    if (priceRequested && priceConnectionId !== PANEL_PRICE_CHOICE && !customChannels[priceConnectionId]) { const message = `${priceChannelName ?? 'Bu pazaryeri'} ana fiyatla satılıyor; kendi fiyatını yazmak için başlıktaki Özel Fiyat anahtarını açın ya da Panel Ana Fiyatı’nı seçin.`; setNotice(`Başarısız: ${message}`); return }
    const failures: Array<{ sku: string; message: string }> = []
    const nextStock: Array<[string, string, string]> = []
    const nextPrice: Array<[string, PriceDraft, PriceDraft]> = []
    const saleDefaulted: Array<{ sku: string; currency: string }> = []
    for (const item of sortedVariants.filter(value => targetSet.has(value.variant.id))) {
      const variant = item.variant
      if (priceRequested) {
        try {
          const column = priceColumns.find(candidate => candidate.id === priceConnectionId)
          if (!column) throw new Error('fiyat kanalı bulunamadı')
          const target = priceConnectionId === PANEL_PRICE_CHOICE ? panelPriceTarget(variant) : quickPriceTarget(variant, priceConnectionId, connections.length)
          if (target.kind === 'blocked') throw new Error(target.reason)
          const current = priceConnectionId === PANEL_PRICE_CHOICE ? panelPrices(variant, item.product) : target.kind === 'update' ? target.offer : {}
          const prices = quickEditOfferPrices(current, list, sale)
          if (!prices.ok) throw new Error(prices.message)
          const shown = quickInlinePrices(column.id, variant, item.product)
          nextPrice.push([priceKey(variant.id, column.id), { list: priceFieldText(prices.listPrice), sale: priceFieldText(prices.salePrice) }, { list: priceFieldText(shown.listPrice), sale: priceFieldText(shown.salePrice) }])
          if (prices.saleDefaulted) saleDefaulted.push({ sku: variant.sku, currency: variant.currency || 'TRY' })
        } catch (error) {
          failures.push({ sku: variant.sku, message: `fiyat: ${error instanceof Error ? error.message : 'uygulanamadı'}` })
        }
      }
      if (stockRequested && amount != null) {
        const expected = stockBaseline.current.get(variant.id) ?? variant.onHand
        const target = stockTarget(stockAction, amount, expected)
        if (target < 0) failures.push({ sku: variant.sku, message: 'stok sıfırın altına inemez.' })
        else nextStock.push([variant.id, String(target), String(quickStockCellValue(variant.onHand, savedStocks.current.get(variant.id) ?? null).value)])
      }
    }
    for (const [id, value, original] of nextStock) { stockDrafts.set(id, value, original); clearRowError(`s:${id}`) }
    for (const [key, value, original] of nextPrice) { priceDrafts.set(key, value, original); clearRowError(`p:${key}`) }
    const filled = nextStock.length + nextPrice.length
    const defaultedNote = saleDefaulted.length && list != null ? ` Satış fiyatı girilmediği için ${saleDefaulted.length} varyantın satış fiyatı liste fiyatına (${formatMoney(list, saleDefaulted[0].currency)}) eşitlendi; kontrol edin.` : ''
    if (failures.length) { setNotice(`Başarısız: ${bulkEditSummary(targetIds.length, failures)}${filled ? ` ${filled} hücre taslak oldu.` : ''}${defaultedNote}`); return }
    setNotice(`${filled} hücre taslak oldu; hiçbir şey kaydedilmedi. Kaydetmek için alttaki çubuktaki Kaydet’e basın.${defaultedNote}`)
  }
  // Kaydet (after its confirmation line): the drafts of the table written at once. Stock goes as an absolute value against
  // the stock this window opened with (a change made meanwhile is refused, not undone); every price cell is saved on its
  // own and a refused one stays a draft with its reason. A saved draft leaves; the rest keep their mark.
  async function saveDrafts() {
    if (saving || draft) return
    setSaving(true); setNotice(''); setRowErrors({})
    const byId = new Map(variants.map(item => [item.variant.id, item]))
    const failures: Array<{ sku: string; message: string }> = []
    const errors: Record<string, string> = {}
    let anyStale = false
    let stockSaved = 0; let priceSaved = 0; let followSaved = 0
    try {
      const followDone: string[] = []
      for (const [connectionId, entry] of followDrafts.entries) {
        const label = channelColumns.find(column => column.id === connectionId)?.label ?? 'Pazaryeri'
        const variantIds = panelFollowChoiceVariantIds(variants.map(item => item.variant), connectionId, !entry.value)
        try {
          if (variantIds.length) await hubApi('/channel-offers/panel-price-follow', { method: 'PUT', body: JSON.stringify({ connectionId, variantIds, followsPanelPrice: !entry.value }) })
          setCustomOverrides(current => ({ ...current, [connectionId]: entry.value }))
          followDone.push(connectionId); followSaved += 1
        } catch (error) {
          const message = error instanceof Error ? error.message : 'beklenmeyen hata.'
          errors[`f:${connectionId}`] = message; failures.push({ sku: label, message: `Özel Fiyat seçimi kaydedilemedi: ${message}` })
        }
      }
      followDrafts.remove(followDone)
      const updates: VariantStockSet[] = []
      for (const [id, entry] of stockDrafts.entries) {
        const item = byId.get(id); const parsed = parseStockInput(entry.value)
        if (!item || parsed.kind !== 'value') continue
        updates.push({ variantId: id, sku: item.variant.sku, onHand: parsed.value, expectedOnHand: stockBaseline.current.get(id) ?? item.variant.onHand })
      }
      const stockResult = await applyVariantStockSets(updates, update => setVariantOnHand(update, 'Hızlı varyant stok düzenleme'))
      for (const saved of stockResult.saved) {
        stockBaseline.current.set(saved.variantId, saved.onHand)
        savedStocks.current.set(saved.variantId, { value: saved.onHand, previousOnHand: byId.get(saved.variantId)?.variant.onHand ?? saved.expectedOnHand })
      }
      stockSaved = stockResult.saved.length
      stockDrafts.remove([...stockResult.saved.map(saved => saved.variantId), ...updates.filter(update => update.onHand === update.expectedOnHand).map(update => update.variantId)])
      for (const failed of stockResult.failed) { if (failed.stale) anyStale = true; errors[`s:${failed.variantId}`] = failed.message; failures.push({ sku: failed.sku, message: `stok: ${failed.message}` }) }
      const done: string[] = []
      for (const [key, entry] of priceDrafts.entries) {
        const [variantId, columnId] = key.split('|')
        const item = byId.get(variantId); const column = priceColumns.find(candidate => candidate.id === columnId)
        if (!item || !column) continue
        if (column.id !== PANEL_PRICE_CHOICE && !customChannels[column.id]) { done.push(key); continue }
        const result = await writePriceCell({ column, followers: column.id === PANEL_PRICE_CHOICE ? followerColumns : [], variant: item.variant, connectionCount: connections.length, list: entry.value.list, sale: entry.value.sale, offerCreateKey: offerCreateKey(key) })
        if (result.ok) { done.push(key); priceSaved += 1 } else { if (result.stale) anyStale = true; errors[`p:${key}`] = result.message; failures.push({ sku: item.variant.sku, message: `${column.label} fiyatı: ${result.message}` }) }
      }
      priceDrafts.remove(done)
    } finally {
      await onChanged().catch(() => undefined)
      setSaving(false)
    }
    setRowErrors(errors)
    const saved = [stockSaved ? `${stockSaved} varyantın stoğu` : '', priceSaved ? `${priceSaved} fiyat hücresi` : '', followSaved ? `${followSaved} kanalda Özel Fiyat seçimi` : ''].filter(Boolean).join(', ')
    if (!failures.length) { setNotice(saved ? `${saved} kaydedildi.` : 'Kaydedilecek değişiklik kalmadı.'); onResult?.('Hızlı Düzenleme Kaydedildi', saved ? `${saved} kaydedildi.` : 'Değişiklik yoktu.', 'success'); return }
    const summary = `${saved ? `${saved} kaydedildi, ` : ''}${failures.length} değişiklik kaydedilemedi. ${failures.slice(0, 3).map(failure => `${failure.sku}: ${failure.message}`).join('; ')}`
    setNotice(`Başarısız: ${summary} Kaydedilemeyenler taslak olarak kaldı.${anyStale ? ' Değişen değerler yenilendi; kontrol edip yeniden kaydedin.' : ''}`)
    onResult?.('Hızlı Düzenleme Tamamlanamadı', summary, 'error')
    if (anyStale) resetStockBaseline.current = true
  }
  // The drafts that fail their checks (stock a whole number from 0; a sale price above 0, list not below it): Kaydet stays
  // off while one exists, and each cell tells its own problem.
  const draftErrors: Record<string, string> = {}
  for (const [id, entry] of stockDrafts.entries) { const parsed = parseStockInput(entry.value); if (parsed.kind === 'empty') draftErrors[`s:${id}`] = 'Stok boş bırakılamaz.'; else if (parsed.kind === 'invalid') draftErrors[`s:${id}`] = 'Stok sıfır veya pozitif bir tam sayı olmalıdır.' }
  for (const [key, entry] of priceDrafts.entries) { const column = priceColumns.find(candidate => candidate.id === key.split('|')[1]); const checked = inlinePrices(column?.takesListPrice === false ? entry.value.sale : entry.value.list, entry.value.sale); if (!checked.ok) draftErrors[`p:${key}`] = checked.message }
  const cellError = (key: string) => draftErrors[key] || rowErrors[key] || ''
  const firstDraftError = Object.values(draftErrors)[0] ?? ''
  useEffect(() => {
    if (draft) return
    const press = (event: KeyboardEvent) => {
      if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey) || !(event.target instanceof Element) || !event.target.closest('.rr-qe-modal')) return
      event.preventDefault()
      document.querySelector<HTMLButtonElement>('.rr-qe-modal .rv-draftbar-save:not(:disabled)')?.click()
    }
    document.addEventListener('keydown', press)
    return () => document.removeEventListener('keydown', press)
  }, [draft])
  const requestClose = () => { if (draftCount > 0 && !draft) setConfirmClose(true); else onClose() }
  const saveSummary = <>{[stockDrafts.count ? `${stockDrafts.count} varyantın stoğu` : '', priceDrafts.count ? `${priceDrafts.count} fiyat hücresi` : '', followDrafts.count ? `${followDrafts.count} kanalda Özel Fiyat seçimi` : ''].filter(Boolean).join(', ')} değişecek.</>
  const canApply = stepValue === 'price' ? (showListPrice && listPrice.trim() !== '') || salePrice.trim() !== '' : stepValue === 'stock' ? stockAmount.trim() !== '' : true
  // What the boxes show now: one value, named in the band above the sizes, in step 2's title and in its field.
  const valueKind: 'stock' | 'cost' | 'list' | 'sale' = !activeTab || activeTab.field === 'stock' ? 'stock' : activeTab.field === 'costPrice' ? 'cost' : activeTab.field === 'list' || activeTab.field === 'listPrice' ? 'list' : 'sale'
  const valueHeading = valueKind === 'stock' ? 'Stok (Adet)' : valueKind === 'cost' ? 'Maliyet (₺)' : valueKind === 'list' ? 'Liste Fiyatı (₺)' : 'Satış Fiyatı (₺)'
  const stepTwoTitle = valueKind === 'stock' ? 'Stok Değerini Düzenle' : valueKind === 'cost' ? 'Maliyeti Düzenle' : valueKind === 'list' ? 'Liste Fiyatını Düzenle' : 'Satış Fiyatını Düzenle'
  // A marketplace window applies one value at a time, so a typed amount does not carry over to the next value; the editor's window keeps what was typed (a dot marks the values holding one) and applies them together.
  const changeValue = (id: string) => { setActiveTabId(id); if (!draft) { setListPrice(''); setSalePrice(''); setCostAmount(''); setStockAmount('') } }
  const valueBand = activeTab ? <>{activeChannel && <PlatformSquareMark code={connections.find(connection => connection.id === activeChannel.id)?.platformCode} name={activeChannel.label} size="sm" />}{activeChannel ? `${activeChannel.label} · ` : ''}{valueHeading}</> : null
  const priceField = (label: string, value: string, set: (value: string) => void, className?: string) => <label className={`rr-fld${className ? ` ${className}` : ''}`}><small>{label}</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={value} onChange={event => set(event.target.value)} placeholder="Değiştirme" aria-label={label} /></label>
  const valueFields = valueKind === 'stock' ? null : valueKind === 'cost' ? priceField(valueHeading, costAmount, setCostAmount, 'rr-qe-f140') : valueKind === 'list' ? priceField(valueHeading, listPrice, setListPrice, 'rr-qe-f140') : priceField(valueHeading, salePrice, setSalePrice, 'rr-qe-f140')
  const stockFields = <><label className="rr-fld rr-qe-f200"><small>Stok İşlemi</small><PanelSelect ariaLabel="Stok İşlemi" value={stockAction} onChange={setStockAction} options={[{ value: 'SET', label: 'Bu Sayıya Eşitle' }, { value: 'ADD', label: 'Bu Kadar Ekle (+)' }, { value: 'SUBTRACT', label: 'Bu Kadar Çıkar (−)' }]} /></label><label className="rr-fld rr-qe-f120"><small>Miktar (Adet)</small><input className="rr-in rr-num" type="text" inputMode="numeric" autoComplete="off" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" aria-label="Miktar (Adet)" /></label></>
  const tabHasDraft = (tab: ValueTab) => tab.kind === 'stock' ? stockDrafts.count > 0 : tab.kind === 'price' ? [...priceDrafts.entries].some(([key, entry]) => key.endsWith(`|${tab.columnId}`) && entry.value[tab.field as 'sale' | 'list'] !== entry.original[tab.field as 'sale' | 'list']) : (tab.field === 'stock' ? stockAmount : tab.field === 'costPrice' ? costAmount : tab.field === 'listPrice' ? listPrice : salePrice).trim() !== ''
  // The one list of what can be edited, grouped by marketplace (the panel first); a value holding a draft carries a dot.
  const channelCodeOf = (columnId?: string) => connections.find(connection => connection.id === columnId)?.platformCode
  const valueText = (tab: ValueTab) => tab.kind === 'price' && !draft ? tab.label : tab.label
  const valueOptions: PanelSelectOption[] = (() => {
    const dot = (tab: ValueTab) => tabHasDraft(tab) ? <i className="rr-mx-dot" aria-label="Kaydedilmemiş değişiklik var" /> : null
    if (draft) return valueTabs.map(tab => ({ value: tab.id, text: tab.label, label: <span className="rr-mx-opt">{tab.label}{dot(tab)}</span> }))
    const out: PanelSelectOption[] = []
    for (const tab of valueTabs) {
      if (tab.kind === 'stock') { out.push({ value: tab.id, text: tab.label, label: <span className="rr-mx-opt">{tab.label}{dot(tab)}</span> }); continue }
      const column = priceColumns.find(candidate => candidate.id === tab.columnId)
      const name = column?.id === PANEL_PRICE_CHOICE ? 'Panel' : column?.label ?? ''
      if (!out.some(option => option.value === `__group:${tab.columnId}`)) out.push({ value: `__group:${tab.columnId}`, text: name, label: <span className="rr-mx-group">{column && column.id !== PANEL_PRICE_CHOICE && <PlatformSquareMark code={channelCodeOf(column.id)} name={name} size="sm" />}{name}</span> })
      const short = tab.field === 'list' ? 'Liste Fiyatı' : column?.takesListPrice ? 'Satış Fiyatı' : 'Fiyatı'
      out.push({ value: tab.id, text: `${name} ${short}`, label: <span className="rr-mx-opt rr-mx-sub">{short}{dot(tab)}</span> })
    }
    return out
  })()
  const matrixColors: MatrixLine[] = colorOptions.map(color => ({ key: color, label: colorLabels[color] || formatCatalogOptionLabel(color) }))
  const matrix = buildMatrix(sortedVariants, colorOf, sizeOf, (left, right) => compareOptionValues('Beden', left, right), matrixColors)
  // The window is as wide as its sizes need (all on one line), never wider than the screen less 32 px.
  const windowWidth = `min(calc(100vw - 32px), ${Math.max(720, colorRowsLayout(matrixColors, matrix.sizes).windowPx)}px)`
  const itemLabel = (item: typeof variants[number]) => { const parts = variantAxisParts(item.variant.optionSignature); return parts.length ? parts.map(part => part.value).join(' · ') : item.variant.sku }
  // One cell's box: the loaded value, or the draft typed over it (purple frame, the saved value in its tooltip).
  function renderCell(item: typeof variants[number], position: { row: number; col: number }) {
    if (!activeTab) return null
    const variant = item.variant
    const where = { 'data-mr': position.row, 'data-mc': position.col }
    const identity = `${variant.sku}${variant.barcode ? ` · ${variant.barcode}` : ''}`
    if (activeTab.kind === 'draft') {
      const values = draftValues[variant.id]
      const field = activeTab.field as keyof QuickEditDraftValues
      const name = itemLabel(item)
      const aria = `${name} ${field === 'stock' ? 'stok' : field === 'costPrice' ? 'maliyet' : field === 'listPrice' ? 'liste fiyatı' : 'satış fiyatı'}`
      return <span className="rr-mx-box">{field === 'stock'
        ? <input {...where} className="rr-in rr-num rr-mx-in" title={identity} aria-label={aria} type="text" inputMode="numeric" autoComplete="off" value={values[field]} onChange={event => setDraftValue(variant.id, field, event.target.value)} />
        : <QuickPriceInput {...where} className="rr-in rr-num rr-mx-in" title={identity} aria-label={aria} value={values[field]} onValue={value => setDraftValue(variant.id, field, value)} />}</span>
    }
    if (activeTab.kind === 'stock') {
      const shown = quickStockCellValue(variant.onHand, savedStocks.current.get(variant.id) ?? null).value
      const drafted = stockDrafts.get(variant.id)?.value
      const error = cellError(`s:${variant.id}`)
      return <span className={`rr-mx-box${drafted !== undefined ? ' rr-draft' : ''}`}><input {...where} className="rr-in rr-num rr-mx-in" aria-label={`${variant.sku} stok`} aria-invalid={error ? true : undefined} title={error || (drafted !== undefined ? `Kaydedilmedi · kayıtlı stok: ${shown} · ${identity}` : identity)} value={drafted ?? String(shown)} type="number" min="0" step="1"
        onChange={event => { setNotice(''); clearRowError(`s:${variant.id}`); stockDrafts.set(variant.id, event.target.value, String(shown)) }}
        onKeyDown={event => { if (event.key === 'Escape' && drafted !== undefined) { event.preventDefault(); stockDrafts.revert(variant.id); clearRowError(`s:${variant.id}`) } }} /></span>
    }
    const column = activeColumn
    if (!column) return null
    const field = activeTab.field as 'sale' | 'list'
    const isPanel = column.id === PANEL_PRICE_CHOICE
    if (!isPanel && !customChannels[column.id]) {
      // A marketplace at the panel price shows it read-only; a variant with no offer there shows a dash.
      const listed = channelOffer(variant, column.id) != null
      const panel = panelPrices(variant, item.product)
      const value = field === 'list' ? panel.listPrice : panel.salePrice
      return <span className="rr-mx-follow" title={listed ? `${column.label} ana fiyatla satılır: panel fiyatı kaydedilince bu fiyat da güncellenir. · ${identity}` : `${column.label}: bu varyantın bu pazaryerinde fiyatı yok. · ${identity}`}>{listed && value != null ? formatPriceField(value) : '—'}</span>
    }
    const shownPrices = quickInlinePrices(column.id, variant, item.product)
    const original: PriceDraft = { list: priceFieldText(shownPrices.listPrice), sale: priceFieldText(shownPrices.salePrice) }
    const key = priceKey(variant.id, column.id)
    const draftPair = priceDrafts.get(key)?.value
    const current = draftPair ?? original
    const target = isPanel ? panelPriceTarget(variant) : quickPriceTarget(variant, column.id, connections.length)
    const blocked = target.kind === 'blocked' ? target.reason : ''
    const error = cellError(`p:${key}`)
    const fallback = isPanel && panelPriceIsFallback(variant, item.product)
    const drafted = draftPair !== undefined && draftPair[field] !== original[field]
    const title = error || blocked || (drafted ? `Kaydedilmedi · kayıtlı fiyat: ${original[field] || '—'} · ${identity}` : fallback ? 'Kayıtlı panel fiyatı yok; ürün düzenleyicideki gibi ilk aktif kanal fiyatı gösteriliyor. Değiştirirseniz panel fiyatı olarak kaydedilir.' : identity)
    return <span className={`rr-mx-box${drafted ? ' rr-draft' : ''}`}><QuickPriceInput {...where} className="rr-in rr-num rr-mx-in" aria-invalid={error ? true : undefined} title={title} disabled={!!blocked} placeholder={field === 'list' ? 'Liste' : 'Satış'} aria-label={`${variant.sku} ${column.label} ${field === 'list' ? 'liste' : 'satış'} fiyatı`} value={current[field]}
      onValue={value => { setNotice(''); clearRowError(`p:${key}`); priceDrafts.set(key, { ...current, [field]: value }, original) }}
      onKeyDown={event => { if (event.key === 'Escape' && draftPair !== undefined) { event.preventDefault(); priceDrafts.revert(key); clearRowError(`p:${key}`) } }} /></span>
  }
  return <RrModal crumb={!draft && mode === 'stock' ? 'Hızlı Stok Güncelleme' : 'Toplu Düzenleme'} title={contextTitle} desc={contextModelCode ? `Model Kodu: ${contextModelCode}` : undefined} size="xl" modalClass={`rr-qe-modal${mode === 'stock' ? ' rr-qe-stock' : ''}`} locked={saving} modalStyle={{ width: windowWidth, maxWidth: 'calc(100vw - 32px)' }} onClose={onClose} onBeforeClose={() => { if (draftCount > 0 && !draft) { setConfirmClose(true); return false } return true }} foot={draft
    ? <><span className="rr-note">{activeSelection.length ? <><b>{activeSelection.length} seçili</b> varyanta</> : <>Seçim yok: <b>{targetIds.length} varyantın</b> tümüne</>}</span><button type="button" className="rr-btn" onClick={requestClose} disabled={saving}>Vazgeç</button><button type="submit" form="product-quick-edit-form" className="rr-btn rr-primary" disabled={saving || !targetIds.length}>{saving ? 'Uygulanıyor…' : 'Varyantlara Uygula'}</button></>
    : <DraftSaveBar variant="foot" count={draftCount} busy={saving} invalid={firstDraftError} summary={saveSummary} notice="Otomatik dış yazması açık pazaryerlerine gönderilir." leaving={confirmClose} onSave={() => void saveDrafts()} onDiscard={() => { stockDrafts.revertAll(); priceDrafts.revertAll(); followDrafts.revertAll(); setRowErrors({}); setNotice('') }} onStay={() => setConfirmClose(false)} onLeave={onClose} onClose={onClose} />}>
    <form id="product-quick-edit-form" style={{ display: 'contents' }} onSubmit={apply}>
      <section className="rr-step"><header><span className="rr-stepn">1</span><div className="rr-h3">Varyantları Seç</div><small className="rr-lead">Düzenlenecek değeri listeden seçin. Renk kutusu o rengi, beden başlığı o bedeni, beden etiketi tek varyantı seçer.</small></header>
        <QuickEditColorRows rows={matrix.rows} sizes={matrix.sizes} cells={matrix.cells} idOf={item => item.variant.id} labelOf={itemLabel} selected={selectedSet} onSelect={selectIds} renderCell={renderCell} caption={`${contextTitle}: renk ve beden varyantları`} band={valueBand} bandKey={activeTab?.id} head={<div className="rr-mx-tabs"><div className="rr-mx-pickfld"><PanelSelect ariaLabel="Düzenlenen Değer" value={activeTab?.id ?? ''} onChange={changeValue} width={240} options={valueOptions} triggerLabel={<span className="rr-mx-trig"><span className="rr-mx-trigk">Değer:</span>{activeTab ? valueText(activeTab) : ''}{valueTabs.some(tabHasDraft) && <i className="rr-mx-dot" aria-label="Kaydedilmemiş değişiklik var" />}</span>} /></div>
          {activeChannel && <div className="rr-mx-switchrow" title={customChannels[activeChannel.id] ? undefined : 'Panel fiyatını izliyor; kendi fiyatını yazmak için Özel Fiyat’ı açın.'}><QuickEditCustomToggle column={activeChannel} custom={customChannels[activeChannel.id]} onChange={value => setCustomChannel(activeChannel.id, value)} compact /></div>}</div>} />
      </section>
      <section className="rr-step"><header><span className="rr-stepn">2</span><div className="rr-h3">{stepTwoTitle}<TitleHint label={stepTwoTitle}>{draft ? 'Tablodaki hücreleri tek tek değiştirebilir, buradaki değerleri işaretli varyantlara (hiçbiri işaretli değilse tümüne) verebilirsiniz. Uygula yalnız ürün düzenleyicideki varyant tablosunu değiştirir; kalıcı olması için sayfadaki Kaydet’e basın.' : 'Değer işaretli varyantlara uygulanır; hiçbir varyant işaretli değilse tümüne uygulanır.'}</TitleHint></div></header>
        {activeSelection.length ? <div className="rr-chips">{selectionChips(variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => item.variant)).map(chip => <span key={chip} className="rr-chip">{chip}</span>)}</div> : null}
        
        {/* One value at a time, the one named above the sizes: its field (a stock change takes an action and an amount). */}
        {draft ? <div className="rr-dgrid rr-qe-row" style={{ '--qe-cols': valueKind === 'stock' ? 2 : 1 } as CSSProperties}>{valueKind === 'stock' ? stockFields : valueFields}</div>
          : <div className="rr-qe-applyrow">{valueKind === 'stock' ? stockFields : valueFields}<span className="rr-note rr-qe-scope">{activeSelection.length ? <><b>{activeSelection.length} seçili</b> varyanta</> : <>Seçim yok: <b>{targetIds.length} varyantın</b> tümüne</>}</span><button type="submit" className="rr-btn rr-qe-apply" disabled={saving || !targetIds.length || !canApply} title="Değeri tablodaki hücrelere taslak olarak yazar; kaydetmez.">Uygula</button></div>}
      </section>
      {notice && <div className={`rr-callout${notice.startsWith('Başarısız:') ? ' rr-danger' : ''}`} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}><RrIcon name={notice.startsWith('Başarısız:') ? 'alert' : 'info'} /><span>{notice}</span></div>}
    </form>
  </RrModal>
}
// "Özel Fiyat" for one marketplace, a switch: on, its own prices are edited in its cells; unticked, it sells at the panel
// price. Shown beside the column title on one line, and above the table on a phone, where the table becomes cards.
function QuickEditCustomToggle({ column, custom, onChange, compact = false, className }: { column: QuickPriceColumn; custom: boolean; onChange: (custom: boolean) => void; compact?: boolean; className?: string }) {
  return <label className={`rr-qt-custom${custom ? ' rr-on' : ''}${className ? ` ${className}` : ''}`} title={custom ? `${column.label}: kendi fiyatıyla satılır; panel fiyatı değişince değişmez.` : `${column.label}: ana fiyatla satılır; panel fiyatı kaydedilince bu pazaryerinin fiyatı da aynı değere güncellenir.`}><input type="checkbox" role="switch" className="rr-qt-switch" checked={custom} onChange={event => onChange(event.currentTarget.checked)} aria-label={`${column.label} özel fiyat`} />{!compact && <b>{column.label}</b>}<span>Özel Fiyat</span></label>
}
// A price field: the number as typed while focused, Turkish money digits ("1.249,90") otherwise.
function QuickPriceInput({ value, onValue, ...props }: Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> & { value: string; onValue: (value: string) => void } & Record<string, unknown>) {
  const [focused, setFocused] = useState(false)
  return <input {...props} type="text" inputMode="decimal" autoComplete="off" value={focused ? value : formatPriceField(value)} onFocus={event => { setFocused(true); props.onFocus?.(event) }} onBlur={event => { setFocused(false); props.onBlur?.(event) }} onChange={event => onValue(event.target.value)} />
}
// The panel price written to the marketplaces that follow it (no "Özel Fiyat"): each existing offer of the variant
// there, at the version loaded, through the same channel offer update as a marketplace's own cell. Returns what failed.
async function writePanelFollowers(variant: Variant, followers: ReadonlyArray<QuickPriceColumn>, prices: { listPrice: number; salePrice: number }, reason: string) {
  const failures: Array<{ message: string; stale: boolean }> = []
  for (const write of panelFollowerWrites(variant, followers, prices)) {
    if (write.kind === 'blocked') { failures.push({ message: `${write.column.label} fiyatı: ${write.reason}`, stale: false }); continue }
    try {
      const body = quickPriceBody({ kind: 'update', offerId: write.offerId, offerVersion: write.offerVersion, offer: write.offer }, variant, { listPrice: write.listPrice, salePrice: write.salePrice }, reason, true)
      await hubApi(`/channel-offers/${write.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${write.offerVersion}"` }, body: JSON.stringify(body) })
    } catch (error) {
      failures.push({ message: `${write.column.label} fiyatı: ${error instanceof Error ? error.message : 'kaydedilmedi'}`, stale: isStaleStockError(error) })
    }
  }
  return failures
}
// One price draft written: the panel price (and the offers of the marketplaces that follow it) or one "Özel Fiyat"
// channel's offer, at the versions loaded; nothing else here decides what is saved. Returns the reason when refused.
async function writePriceCell({ column, followers, variant, connectionCount, list, sale, offerCreateKey }: { column: QuickPriceColumn; followers: ReadonlyArray<QuickPriceColumn>; variant: Variant; connectionCount: number; list: string; sale: string; offerCreateKey: string }): Promise<{ ok: true } | { ok: false; message: string; stale: boolean }> {
  const isPanel = column.id === PANEL_PRICE_CHOICE
  const target = isPanel ? panelPriceTarget(variant) : quickPriceTarget(variant, column.id, connectionCount)
  if (target.kind === 'blocked') return { ok: false, message: target.reason, stale: false }
  const prices = inlinePrices(column.takesListPrice ? list : sale, sale)
  if (!prices.ok) return { ok: false, message: prices.message, stale: false }
  try {
    if (target.kind === 'panel') {
      await hubApi(`/variants/${target.variantId}/default-prices`, { method: 'PATCH', headers: { 'If-Match': `"v${target.version}"` }, body: JSON.stringify({ listPrice: prices.listPrice, salePrice: prices.salePrice, reason: 'Hızlı varyant fiyat düzenleme' }) })
      const failures = await writePanelFollowers(variant, followers, prices, 'Hızlı varyant fiyat düzenleme')
      if (failures.length) return { ok: false, message: `panel fiyatı kaydedildi; ${failures.map(failure => failure.message).join('; ')}`, stale: failures.some(failure => failure.stale) }
    } else {
      // A marketplace cell is editable only while its "Özel Fiyat" is ticked: the price is stored as its own.
      const body = quickPriceBody(target, variant, prices, 'Hızlı varyant fiyat düzenleme', false)
      if (target.kind === 'update') await hubApi(`/channel-offers/${target.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.offerVersion}"` }, body: JSON.stringify(body) })
      else await sendWithVersionedIdempotencyKey(offerCreateKey, key => hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(body) }))
    }
  } catch (error) {
    return { ok: false, message: `kaydedilmedi: ${error instanceof Error ? error.message : 'beklenmeyen hata.'}`, stale: isStaleStockError(error) }
  }
  return { ok: true }
}
