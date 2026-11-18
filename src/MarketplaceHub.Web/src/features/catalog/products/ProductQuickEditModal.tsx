import { Fragment, useEffect, useId, useRef, useState, type CSSProperties, type FormEvent, type InputHTMLAttributes, type ReactElement } from 'react'
import { variantOptionParts } from '../variant-option-label'
import { colourFirstOptions } from '../variant-option-matching'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { TitleHint } from '../../../shared/components'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { RrAnchor, RrIcon, RrModal } from '../../../shared/ui/rr-components'
import { type NotificationContext } from '../../../shared/notifications'
import { updateVariantGroupSelection, variantGroupSelectionState, variantShortcutLabel, variantShortcutSelection } from '../variant-bulk-edit'
import { applyVariantStockSets, bulkEditSummary, isStaleStockError, parseStockInput, setVariantOnHand, stockTarget, type VariantStockSet } from '../variant-stock-set'
import { bulkEditValues, channelOffer, channelTakesListPrice, inlinePrices, PANEL_PRICE_CHOICE, panelPriceIsFallback, panelPrices, panelPriceTarget, formatPriceField, initialCustomChannels, priceFieldText, panelFollowChoiceVariantIds, panelFollowerColumns, panelFollowerWrites, quickEditOfferPrices, quickInlinePrices, quickPriceBody, quickPriceChoiceHelp, quickPriceColumns, quickPriceTarget, type QuickPriceColumn } from '../quick-price-edit'
import { formatMoney } from '../../../shared/format-money'
import { quickEditVariantItems, quickStockCellValue, type SavedStock } from './quick-edit-products'
import { applyQuickEditDraft, quickEditDraftValues, type QuickEditDraftChange, type QuickEditDraftRow, type QuickEditDraftValues } from './quick-edit-draft'
import { PanelSelect } from '../../../shared/PanelSelect'
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
export type QuickEditMode = 'stock' | 'price' | 'both'
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
export function ProductQuickEditModal({ products: savedProducts = [], connections = [], mode = 'both', onChanged = noChange, onClose, onResult, draft }: { products?: Product[]; connections?: MarketplaceConnection[]; mode?: QuickEditMode; onChanged?: () => Promise<unknown>; onClose: () => void; onResult?: (title: string, message: string, kind: 'success' | 'error' | 'info', context?: NotificationContext) => void; draft?: QuickEditDraft }) {
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
  const [selectedColors, setSelectedColors] = useState<string[]>([])
  const [selectedSizes, setSelectedSizes] = useState<string[]>([])
  const [openColorGroups, setOpenColorGroups] = useState<string[]>([])
  // The groups in one grid row open and close together (two columns in a stock window, one on a phone or for prices).
  const phoneWidth = useMediaQuery('(max-width: 760px)')
  const groupColumns = phoneWidth ? 1 : 2
  // Each colour group opens on its own; an open group's table takes the full width right under its row.
  const [closingGroups, setClosingGroups] = useState<string[]>([])
  const [revealPanel, setRevealPanel] = useState<string | null>(null)
  const groupPanelId = useId()
  const [listPrice, setListPrice] = useState(''); const [salePrice, setSalePrice] = useState(''); const [stockAmount, setStockAmount] = useState('')
  const [stockAction, setStockAction] = useState<'SET' | 'ADD' | 'SUBTRACT'>('SET'); const [notice, setNotice] = useState(''); const [saving, setSaving] = useState(false)
  // Where a price change goes: the panel's own price (preset: it sends nothing to a marketplace) or one channel the
  // person picks (its offer updated, or created when the variant has none there). The screen never picks a channel.
  const [priceConnectionId, setPriceConnectionId] = useState(PANEL_PRICE_CHOICE)
  const priceChannelName = connections.find(connection => connection.id === priceConnectionId)?.displayName ?? null
  // The per-variant table: stock, the panel price and every channel's price side by side, each saved on its own.
  const priceColumns = quickPriceColumns(connections)
  const channelColumns = priceColumns.filter(column => column.id !== PANEL_PRICE_CHOICE)
  const showStockColumn = mode !== 'price'; const showPriceColumns = mode !== 'stock'
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
  const needsPriceConnection = mode !== 'stock'
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
  const sizeOptions = [...new Set(sortedVariants.filter(item => !selectedColors.length || selectedColors.includes(colorOf(item))).map(sizeOf))].sort((left, right) => compareOptionValues('Beden', left, right))
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
  // A checkbox ticked by hand makes the selection the person's own: the Renk / Beden shortcuts no longer describe it.
  const clearShortcuts = () => { setSelectedColors([]); setSelectedSizes([]) }
  const toggle = (id: string) => { clearShortcuts(); setSelectionDraft(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]) }
  function applyFilterSelection(colors: string[], sizes: string[]) {
    setSelectionDraft(variantShortcutSelection(sortedVariants, colors, sizes, colorOf, sizeOf, item => item.variant.id))
  }
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
    const priceRequested = mode !== 'stock' && ((showListPrice && listPrice.trim() !== '') || salePrice.trim() !== ''); const stockRequested = stockAmount.trim() !== ''
    if (!priceRequested && !stockRequested) { const message = 'Uygulanacak fiyat veya stok değerini girin.'; setNotice(message); onResult?.('Değer Girilmedi', message, 'error'); return }
    // Read as Turkish ("1.250" is 1250); a stock fraction is refused, not rounded.
    const values = bulkEditValues({ showListPrice, listPrice: mode === 'stock' ? '' : listPrice, salePrice: mode === 'stock' ? '' : salePrice, stockAmount })
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
  const requestClose = () => { if (draftCount > 0 && !draft) setConfirmClose(true); else onClose() }
  const saveSummary = <>{[stockDrafts.count ? `${stockDrafts.count} varyantın stoğu` : '', priceDrafts.count ? `${priceDrafts.count} fiyat hücresi` : '', followDrafts.count ? `${followDrafts.count} kanalda Özel Fiyat seçimi` : ''].filter(Boolean).join(', ')} değişecek.</>
  const stepTwoTitle = mode === 'stock' ? 'Stok Değerini Düzenle' : mode === 'price' ? 'Fiyat Değerini Düzenle' : 'Fiyat ve Stok Değerini Düzenle'
  const reduceMotion = () => typeof window.matchMedia !== 'function' || window.matchMedia('(prefers-reduced-motion: reduce)').matches
  const toggleColorGroup = (color: string, panelId: string) => {
    if (!openColorGroups.includes(color)) { setOpenColorGroups(current => [...current, color]); setRevealPanel(panelId); return }
    if (reduceMotion()) { setOpenColorGroups(current => current.filter(item => item !== color)); return }
    setClosingGroups(current => [...current, color])
    window.setTimeout(() => { setOpenColorGroups(current => current.filter(item => item !== color)); setClosingGroups(current => current.filter(item => item !== color)) }, 150)
  }
  const allGroupsOpen = colorOptions.length > 0 && colorOptions.every(color => openColorGroups.includes(color))
  const toggleAllGroups = () => { setClosingGroups([]); setOpenColorGroups(allGroupsOpen ? [] : [...colorOptions]) }
  // The group just opened scrolls into view (nearest: no jump when it already is).
  useEffect(() => {
    if (!revealPanel) return
    document.getElementById(revealPanel)?.scrollIntoView?.({ block: 'nearest', behavior: reduceMotion() ? 'auto' : 'smooth' })
    setRevealPanel(null)
  }, [revealPanel])
  return <RrModal crumb={mode === 'stock' ? 'Hızlı Stok Güncelleme' : 'Toplu Düzenleme'} title={contextTitle} desc={contextModelCode ? `Model Kodu: ${contextModelCode}` : undefined} size="xl" modalClass={`rr-qe-modal${mode === 'stock' ? ' rr-qe-stock' : ''}`} locked={saving} onClose={onClose} onBeforeClose={() => { if (draftCount > 0 && !draft) { setConfirmClose(true); return false } return true }} foot={<><span className="rr-note">{activeSelection.length ? <><b>{activeSelection.length} varyant</b> seçildi</> : <>Seçim yok: <b>{targetIds.length} varyantın</b> tümüne uygulanır</>}</span><button type="button" className="rr-btn" onClick={requestClose} disabled={saving}>{draftCount > 0 && !draft ? 'Kapat' : 'Vazgeç'}</button><button type="submit" form="product-quick-edit-form" className="rr-btn rr-primary" disabled={saving || !targetIds.length} title={draft ? undefined : 'Değerleri tablodaki hücrelere taslak olarak yazar; kaydetmez.'}>{saving ? 'Uygulanıyor…' : draft ? 'Varyantlara Uygula' : activeSelection.length ? 'Seçilenlere Uygula' : 'Tümüne Uygula'}</button></>}>
    <form id="product-quick-edit-form" style={{ display: 'contents' }} onSubmit={apply}>
      <section className="rr-step"><header><span className="rr-stepn">1</span><div className="rr-h3">Varyantları Seç</div><div className="rr-step-tools">
        <button type="button" className="rr-btn rr-link" onClick={toggleAllGroups} aria-pressed={allGroupsOpen}>{allGroupsOpen ? 'Tümünü Kapat' : 'Tümünü Aç'}</button>
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Renk: {variantShortcutLabel(selectedColors, colorOptions)}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" aria-label="Renk" style={{ minWidth: 240 }}>{colorOptions.map(color => <label key={color} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedColors.includes(color)} onChange={() => toggleColor(color)} /><span className="rr-grow">{colorLabels[color] || formatCatalogOptionLabel(color)}</span><small>{groups[color].length} varyant</small></label>)}</div>}</RrAnchor>
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Beden: {variantShortcutLabel(selectedSizes, sizeOptions)}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" aria-label="Beden" style={{ minWidth: 220 }}>{sizeOptions.map(size => <label key={size} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedSizes.includes(size)} onChange={() => toggleSize(size)} /><span className="rr-grow">{size}</span><small>{variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && sizeOf(item) === size).length} varyant</small></label>)}</div>}</RrAnchor>
      </div></header>
                {/* Two columns of compact group rows (one on a phone); an opened group's table spans both, right under its row. */}
        <div className="rr-vgroups rr-two">{colorOptions.map((color, index) => {
          const items = groups[color]
          const isOpen = openColorGroups.includes(color)
          const selectedCount = items.filter(item => selectedSet.has(item.variant.id)).length
          const groupSelection = variantGroupSelectionState(items.map(item => item.variant.id), selectedSet)
          const groupLabel = colorLabels[color] || formatCatalogOptionLabel(color)
          const panelId = `${groupPanelId}-${index}`
          const header = <div className={`rr-vgroup${isOpen ? ' rr-open' : ''}`} key={color}><div className="rr-vg-h"><input type="checkbox" className="rr-cb" checked={groupSelection.checked} data-mixed={groupSelection.indeterminate ? '' : undefined} aria-label={`${groupLabel} rengindeki tüm varyantları seç`} onChange={event => {
            // Read before the state update: React clears currentTarget once the handler returns, and the updater can
            // run later (on render), where event.currentTarget.checked threw "Cannot read properties of null".
            const checked = event.currentTarget.checked
            const groupIds = items.map(item => item.variant.id)
            clearShortcuts()
            setSelectionDraft(current => updateVariantGroupSelection(current, groupIds, checked))
          }} /><button type="button" className="rr-vg-t" aria-expanded={isOpen} aria-controls={isOpen ? panelId : undefined} onClick={() => toggleColorGroup(color, panelId)}><span className="rr-vg-name"><b title={groupLabel}>{groupLabel}</b><small>{selectedCount ? `${selectedCount}/${items.length} seçili` : `${items.length} varyant`}</small></span><RrIcon name="chev" /></button></div></div>
          const panel = isOpen ? <div className={`rr-vg-panel${closingGroups.includes(color) ? ' rr-closing' : ''}`} key={`${color}-panel`}><div className="rr-qtable-wrap" id={panelId} role="region" aria-label={`${groupLabel} varyantları`}>
            <table className={`rr-qtable${showPriceColumns ? ' rr-qt-prices' : ''}${showStockColumn ? ' rr-qt-has-stock' : ''}`}>
              <caption className="sr-only">{groupLabel} varyantları</caption>
              <colgroup><col className="rr-qt-w-sel" /><col className="rr-qt-w-var" />{showStockColumn && <col className="rr-qt-w-stock" />}{draft && <col className="rr-qt-w-price" />}{showPriceColumns && priceColumns.flatMap(column => column.takesListPrice ? [<col key={`${column.id}-l`} className="rr-qt-w-price" />, <col key={`${column.id}-s`} className="rr-qt-w-price" />] : [<col key={column.id} className="rr-qt-w-price" />])}</colgroup>
              <thead>
                <tr>
                  <th scope="col" rowSpan={showPriceColumns ? 2 : 1} className="rr-qt-sel rr-qt-stick"><span className="sr-only">Seç</span></th>
                  <th scope="col" rowSpan={showPriceColumns ? 2 : 1} className="rr-qt-var rr-qt-stick">Varyant</th>
                  {showStockColumn && <th scope="col" rowSpan={showPriceColumns ? 2 : 1} className="rr-qt-stock rr-qt-stick rr-qt-num">Stok</th>}
                  {draft && <th scope="col" rowSpan={showPriceColumns ? 2 : 1} className="rr-qt-gs rr-qt-num">Maliyet</th>}
                  {showPriceColumns && <th scope="colgroup" colSpan={2} className="rr-qt-panel rr-qt-gs">Panel Fiyatı</th>}
                  {showPriceColumns && channelColumns.map(column => <th key={column.id} scope="colgroup" colSpan={column.takesListPrice ? 2 : 1} className={`rr-qt-ch rr-qt-gs${customChannels[column.id] ? ' rr-on' : ''}`}><span className="rr-qt-chhead"><span className="rr-qt-chname" title={column.label}>{column.label}</span><QuickEditCustomToggle column={column} custom={customChannels[column.id]} onChange={value => void setCustomChannel(column.id, value)} compact /></span></th>)}
                </tr>
                {showPriceColumns && <tr className="rr-qt-sub">
                  <th scope="col" className="rr-qt-panel rr-qt-gs rr-qt-num">Liste</th><th scope="col" className="rr-qt-panel rr-qt-ps rr-qt-num">Satış</th>
                  {channelColumns.map(column => column.takesListPrice ? [<th key={`${column.id}-l`} scope="col" className="rr-qt-gs rr-qt-num">Liste</th>, <th key={`${column.id}-s`} scope="col" className="rr-qt-num">Satış</th>] : <th key={column.id} scope="col" className="rr-qt-gs rr-qt-num">Satış</th>)}
                </tr>}
              </thead>
              <tbody>{items.map(item => { const parts = variantAxisParts(item.variant.optionSignature); return <tr className={selectedSet.has(item.variant.id) ? 'rr-on' : 'rr-off'} key={item.variant.id}>
                <td className="rr-qt-sel rr-qt-stick"><input type="checkbox" className="rr-cb" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} aria-label={`${item.variant.optionSignature || item.variant.sku} varyantını seç`} /></td>
                <th scope="row" className="rr-qt-var rr-qt-stick"><span className="rr-qname"><b>{parts.length ? parts.map(part => part.value).join(' · ') : 'Varyant'}</b><small>{item.variant.sku}</small></span></th>
                {draft ? <QuickEditDraftCells label={parts.length ? parts.map(part => part.value).join(' · ') : item.variant.sku} values={draftValues[item.variant.id]} onValue={(field, value) => setDraftValue(item.variant.id, field, value)} /> : <>{showStockColumn && <QuickEditStockCell variant={item.variant} shown={quickStockCellValue(item.variant.onHand, savedStocks.current.get(item.variant.id) ?? null).value} draft={stockDrafts.get(item.variant.id)?.value} error={cellError(`s:${item.variant.id}`)} onDraft={value => { setNotice(''); clearRowError(`s:${item.variant.id}`); stockDrafts.set(item.variant.id, value, String(quickStockCellValue(item.variant.onHand, savedStocks.current.get(item.variant.id) ?? null).value)) }} onRevert={() => { stockDrafts.revert(item.variant.id); clearRowError(`s:${item.variant.id}`) }} />}
                {showPriceColumns && priceColumns.map(column => column.id === PANEL_PRICE_CHOICE || customChannels[column.id]
                  ? <QuickEditPriceCell key={column.id} column={column} variant={item.variant} product={item.product} connectionCount={connections.length} draft={priceDrafts.get(priceKey(item.variant.id, column.id))?.value} error={cellError(`p:${priceKey(item.variant.id, column.id)}`)} onDraft={(next, original) => { setNotice(''); clearRowError(`p:${priceKey(item.variant.id, column.id)}`); priceDrafts.set(priceKey(item.variant.id, column.id), next, original) }} onRevert={() => { priceDrafts.revert(priceKey(item.variant.id, column.id)); clearRowError(`p:${priceKey(item.variant.id, column.id)}`) }} onCustom={phoneWidth && column.id !== PANEL_PRICE_CHOICE ? value => void setCustomChannel(column.id, value) : undefined} />
                  : <QuickEditFollowCell key={column.id} column={column} variant={item.variant} product={item.product} onCustom={phoneWidth ? value => void setCustomChannel(column.id, value) : undefined} />)}</>}
              </tr> })}</tbody>
            </table>
          </div></div> : null
          return { color, header, panel }
        }).reduce<Array<Array<{ color: string; header: ReactElement; panel: ReactElement | null }>>>((rows, entry, index) => { if (index % groupColumns === 0) rows.push([]); rows[rows.length - 1].push(entry); return rows }, []).map(row => <Fragment key={row[0].color}>{row.map(entry => entry.header)}{row.map(entry => entry.panel)}</Fragment>)}</div>
      </section>
      <section className="rr-step"><header><span className="rr-stepn">2</span><div className="rr-h3">{stepTwoTitle}<TitleHint label={stepTwoTitle}>{draft ? 'Tablodaki hücreleri tek tek değiştirebilir, buradaki değerleri işaretli varyantlara (hiçbiri işaretli değilse tümüne) verebilirsiniz. Uygula yalnız ürün düzenleyicideki varyant tablosunu değiştirir; kalıcı olması için sayfadaki Kaydet’e basın.' : 'Değer işaretli varyantlara uygulanır; hiçbir varyant işaretli değilse tümüne uygulanır.'}</TitleHint></div><small className="rr-lead">{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : `Seçim yok: tüm ${targetIds.length} varyanta uygulanacak`}</small></header>
        {activeSelection.length ? <div className="rr-chips">{selectionChips(variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => item.variant)).map(chip => <span key={chip} className="rr-chip">{chip}</span>)}</div> : null}
        {needsPriceConnection && !draft && <label className="rr-fld"><small>Fiyat Kanalı</small><PanelSelect ariaLabel="Fiyat kanalı" value={priceConnectionId} onChange={setPriceConnectionId} options={[{ value: PANEL_PRICE_CHOICE, label: 'Panel Ana Fiyatı' }, ...connections.map(connection => ({ value: connection.id, label: connection.displayName }))]} /><small className="rr-lead" id="quick-price-choice-help" aria-live="polite">{quickPriceChoiceHelp(priceConnectionId, priceChannelName, followerColumns.map(column => column.label))}</small></label>}
        {/* Prices (and Maliyet) on one row, Stok İşlemi and Miktar on the next (owner, 11 Nov 2026); a phone stacks them. */}
        {mode !== 'stock' && <div className="rr-dgrid rr-qe-row" style={{ '--qe-cols': Number(showListPrice) + 1 + Number(Boolean(draft)) } as CSSProperties}>{showListPrice && <label className="rr-fld"><small>Liste Fiyatı</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label>}<label className="rr-fld"><small>Satış Fiyatı</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label>{draft && <label className="rr-fld"><small>Maliyet</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={costAmount} onChange={event => setCostAmount(event.target.value)} placeholder="Değiştirme" /></label>}</div>}
        {mode !== 'price' && <div className="rr-dgrid rr-qe-row" style={{ '--qe-cols': 2 } as CSSProperties}><label className="rr-fld"><small>Stok İşlemi</small><PanelSelect ariaLabel="Stok İşlemi" value={stockAction} onChange={setStockAction} options={[{ value: 'SET', label: 'Bu Sayıya Eşitle' }, { value: 'ADD', label: 'Bu Kadar Ekle (+)' }, { value: 'SUBTRACT', label: 'Bu Kadar Çıkar (−)' }]} /></label><label className="rr-fld"><small>Miktar</small><input className="rr-in rr-num" type="text" inputMode="numeric" autoComplete="off" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></div>}
      </section>
      {!draft && <DraftSaveBar count={draftCount} busy={saving} invalid={firstDraftError} summary={saveSummary} notice="Otomatik dış yazması açık pazaryerlerine gönderilir." leaving={confirmClose} onSave={() => void saveDrafts()} onDiscard={() => { stockDrafts.revertAll(); priceDrafts.revertAll(); followDrafts.revertAll(); setRowErrors({}); setNotice('') }} onStay={() => setConfirmClose(false)} onLeave={onClose} />}
      {notice && <div className={`rr-callout${notice.startsWith('Başarısız:') ? ' rr-danger' : ''}`} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}><RrIcon name={notice.startsWith('Başarısız:') ? 'alert' : 'info'} /><span>{notice}</span></div>}
    </form>
  </RrModal>
}
// One editor row's cells in the Toplu Düzenle window opened from the product editor: Stok, Maliyet and the panel
// Liste / Satış prices, typed into the window only (nothing is saved); the window checks them when they are applied.
function QuickEditDraftCells({ label, values, onValue }: { label: string; values: QuickEditDraftValues; onValue: (field: keyof QuickEditDraftValues, value: string) => void }) {
  return <>
    <td className="rr-qt-stock rr-qt-stick rr-qt-num" data-l="Stok"><input className="rr-in rr-num" aria-label={`${label} stok`} type="text" inputMode="numeric" autoComplete="off" value={values.stock} onChange={event => onValue('stock', event.target.value)} /></td>
    <td className="rr-qt-gs rr-qt-num" data-l="Maliyet"><QuickPriceInput className="rr-in rr-num" placeholder="Maliyet" aria-label={`${label} maliyet`} value={values.costPrice} onValue={value => onValue('costPrice', value)} /></td>
    <td className="rr-qt-gs rr-qt-num rr-qt-panel" colSpan={2} data-l="Panel Fiyatı"><div className="rr-qpair" role="group" aria-label={`${label} panel fiyatı`}><QuickPriceInput className="rr-in rr-num" placeholder="Liste" aria-label={`${label} liste fiyatı`} value={values.listPrice} onValue={value => onValue('listPrice', value)} /><QuickPriceInput className="rr-in rr-num" placeholder="Satış" aria-label={`${label} satış fiyatı`} value={values.salePrice} onValue={value => onValue('salePrice', value)} /></div></td>
  </>
}
// One variant's stock in the quick edit matrix. Typing makes a draft (the window's Kaydet saves it, against the stock the
// window opened with); Esc puts the saved value back. The field shows the draft, else the loaded (or just saved) stock.
function QuickEditStockCell({ variant, shown, draft, error, onDraft, onRevert }: { variant: Variant; shown: number; draft?: string; error: string; onDraft: (value: string) => void; onRevert: () => void }) {
  const drafted = draft !== undefined
  return <td className={`rr-qt-stock rr-qt-stick rr-qt-num${drafted ? ' rr-draft' : ''}`} data-l="Stok"><input className="rr-in rr-num" aria-label={`${variant.sku} stok`} aria-invalid={error ? true : undefined} title={error || (drafted ? `Kaydedilmedi · kayıtlı stok: ${shown}` : undefined)} value={draft ?? String(shown)} onChange={event => onDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Escape' && drafted) { event.preventDefault(); onRevert() } }} type="number" min="0" step="1" /></td>
}
// "Özel Fiyat" for one marketplace, a switch: on, its own prices are edited in its cells; unticked, it sells at the panel
// price. Shown beside the column title on one line, and above the table on a phone, where the table becomes cards.
function QuickEditCustomToggle({ column, custom, onChange, compact = false, className }: { column: QuickPriceColumn; custom: boolean; onChange: (custom: boolean) => void; compact?: boolean; className?: string }) {
  return <label className={`rr-qt-custom${custom ? ' rr-on' : ''}${className ? ` ${className}` : ''}`} title={custom ? `${column.label}: kendi fiyatıyla satılır; panel fiyatı değişince değişmez.` : `${column.label}: ana fiyatla satılır; panel fiyatı kaydedilince bu pazaryerinin fiyatı da aynı değere güncellenir.`}><input type="checkbox" role="switch" className="rr-qt-switch" checked={custom} onChange={event => onChange(event.currentTarget.checked)} aria-label={`${column.label} özel fiyat`} />{!compact && <b>{column.label}</b>}<span>Özel Fiyat</span></label>
}
// A price field: the number as typed while focused, Turkish money digits ("1.249,90") otherwise.
function QuickPriceInput({ value, onValue, ...props }: Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> & { value: string; onValue: (value: string) => void }) {
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
// A marketplace that follows the panel price: its cells show the panel price, read-only. A variant with no offer there
// shows a dash: a panel save changes nothing on that marketplace (publishing creates the offer).
function QuickEditFollowCell({ column, variant, product, onCustom }: { column: QuickPriceColumn; variant: Variant; product: Product; onCustom?: (custom: boolean) => void }) {
  const listed = channelOffer(variant, column.id) != null
  const panel = panelPrices(variant, product)
  const text = (value: number | null) => listed && value != null ? formatPriceField(value) : '—'
  const title = listed ? `${column.label} ana fiyatla satılır: panel fiyatı kaydedilince bu fiyat da güncellenir. Kendi fiyatını girmek için başlıktaki Özel Fiyat anahtarını açın.` : `${column.label}: bu varyantın bu pazaryerinde fiyatı yok; panel fiyatı kaydedilince burada bir şey değişmez.`
  return <td className="rr-qt-follow rr-qt-gs rr-qt-num" colSpan={column.takesListPrice ? 2 : 1} data-l={`${column.label} · Ana Fiyat`} title={title}>{onCustom && <QuickEditCustomToggle column={column} custom={false} onChange={onCustom} compact className="rr-qt-cardswitch" />}<div className={`rr-qpair${column.takesListPrice ? '' : ' rr-qsale'}`}>{column.takesListPrice && <span>{text(panel.listPrice)}</span>}<span>{text(panel.salePrice)}</span></div></td>
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
// One variant's price on one column of the quick edit table: the panel price (the product editor's variant Fiyat /
// Liste Fiyatı) or one ticked ("Özel Fiyat") channel's offer. Typing makes a draft pair (Liste / Satış); the window's
// Kaydet writes it (see writePriceCell), Esc puts the saved pair back. A problem is told on the field (red border, title).
function QuickEditPriceCell({ column, variant, product, connectionCount, draft, error, onDraft, onRevert, onCustom }: { column: QuickPriceColumn; variant: Variant; product: Product; connectionCount: number; draft?: PriceDraft; error: string; onDraft: (next: PriceDraft, original: PriceDraft) => void; onRevert: () => void; onCustom?: (custom: boolean) => void }) {
  const isPanel = column.id === PANEL_PRICE_CHOICE
  const showListPrice = column.takesListPrice
  const shown = quickInlinePrices(column.id, variant, product)
  const original: PriceDraft = { list: priceFieldText(shown.listPrice), sale: priceFieldText(shown.salePrice) }
  const current = draft ?? original
  const priceTarget = isPanel ? panelPriceTarget(variant) : quickPriceTarget(variant, column.id, connectionCount)
  const priceBlocked = priceTarget.kind === 'blocked' ? priceTarget.reason : ''
  // A card without a saved panel price shows its first channel price, as the product editor does; said on hover.
  const fallback = isPanel && panelPriceIsFallback(variant, product)
  const drafted = draft !== undefined
  const priceTitle = error || priceBlocked || (drafted ? `Kaydedilmedi · kayıtlı fiyat: ${showListPrice ? `${original.list || '—'} / ` : ''}${original.sale || '—'}` : fallback ? 'Kayıtlı panel fiyatı yok; ürün düzenleyicideki gibi ilk aktif kanal fiyatı gösteriliyor. Değiştirirseniz panel fiyatı olarak kaydedilir.' : undefined)
  const fieldProps = { className: 'rr-in rr-num', 'aria-invalid': error ? true : undefined, title: priceTitle, disabled: !!priceBlocked } as const
  return <td className={`rr-qt-gs rr-qt-num${isPanel ? ' rr-qt-panel' : ' rr-qt-own'}${fallback ? ' rr-qfallback' : ''}${drafted ? ' rr-draft' : ''}`} colSpan={showListPrice ? 2 : 1} data-l={isPanel ? 'Panel Fiyatı' : `${column.label} · Özel Fiyat`} title={priceTitle} onKeyDown={event => { if (event.key === 'Escape' && drafted) { event.preventDefault(); onRevert() } }}>{onCustom && !isPanel && <QuickEditCustomToggle column={column} custom onChange={onCustom} compact className="rr-qt-cardswitch" />}<div className={`rr-qpair${showListPrice ? '' : ' rr-qsale'}`} role="group" aria-label={`${variant.sku} ${column.label}`}>{showListPrice && <QuickPriceInput {...fieldProps} placeholder="Liste" aria-label={`${variant.sku} ${column.label} liste fiyatı`} value={current.list} onValue={value => onDraft({ ...current, list: value }, original)} />}<QuickPriceInput {...fieldProps} placeholder="Satış" aria-label={`${variant.sku} ${column.label} satış fiyatı`} value={current.sale} onValue={value => onDraft({ ...current, sale: value }, original)} /></div></td>
}
