import { useEffect, useId, useRef, useState, type FormEvent, type InputHTMLAttributes } from 'react'
import { variantOptionParts } from '../variant-option-label'
import { colourFirstOptions } from '../variant-option-matching'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { TitleHint, UiIcon } from '../../../shared/components'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { RrAnchor, RrIcon, RrModal } from '../../marketplace/returns-ui'
import { type NotificationContext } from '../../../shared/notifications'
import { toggleVariantGroupRow, updateVariantGroupSelection, variantGroupRowMates, variantGroupSelectionState, variantShortcutLabel, variantShortcutSelection } from '../variant-bulk-edit'
import { applyVariantStockSets, bulkEditSummary, isStaleStockError, parseStockInput, setVariantOnHand, stockErrorMessage, stockTarget, type VariantStockSet } from '../variant-stock-set'
import { bulkEditValues, channelOffer, channelTakesListPrice, inlinePrices, PANEL_PRICE_CHOICE, panelPriceIsFallback, panelPrices, panelPriceTarget, formatPriceField, initialCustomChannels, priceFieldText, panelFollowChoiceVariantIds, panelFollowerColumns, panelFollowerWrites, quickEditOfferPrices, quickInlinePrices, quickPriceBody, quickPriceChoiceHelp, quickPriceColumns, quickPriceTarget, type QuickPriceColumn } from '../quick-price-edit'
import { formatMoney } from '../../../shared/format-money'
import { quickEditVariantItems, quickStockCellValue, type SavedStock } from './quick-edit-products'
import { applyQuickEditDraft, quickEditDraftValues, type QuickEditDraftChange, type QuickEditDraftRow, type QuickEditDraftValues } from './quick-edit-draft'
import { PanelSelect } from '../../../shared/PanelSelect'
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
export function ProductQuickEditModal({ products: savedProducts = [], connections = [], mode = 'both', onChanged = noChange, onClose, onResult, draft }: { products?: Product[]; connections?: MarketplaceConnection[]; mode?: QuickEditMode; onChanged?: () => Promise<unknown>; onClose: () => void; onResult?: (title: string, message: string, kind: 'success' | 'error' | 'info', context?: NotificationContext) => void; draft?: QuickEditDraft }) {
  // Opened from the editor, the rows are read once: what is typed here stays here until it is applied.
  const [draftProducts] = useState(() => draft ? [draftProduct(draft)] : null)
  const products = draftProducts ?? savedProducts
  const [draftValues, setDraftValues] = useState<Record<string, QuickEditDraftValues>>(() => Object.fromEntries((draft?.rows ?? []).map(row => [row.key, quickEditDraftValues(row)])))
  const setDraftValue = (key: string, field: keyof QuickEditDraftValues, value: string) => { setNotice(''); setDraftValues(current => ({ ...current, [key]: { ...current[key], [field]: value } })) }
  const [costAmount, setCostAmount] = useState('')
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
  const groupColumns = mode === 'stock' && !phoneWidth ? 2 : 1
  const [hintedGroupRow, setHintedGroupRow] = useState<number | null>(null)
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
  const [customOverrides, setCustomOverrides] = useState<Record<string, boolean>>({})
  const customChannels: Record<string, boolean> = Object.fromEntries(channelColumns.map(column => [column.id, customOverrides[column.id] ?? derivedCustom?.[column.id] ?? true]))
  // A switch is saved at once, as a price cell is: the choice goes to every offer of this window's variants on that
  // marketplace (no price changes, nothing goes to the marketplace). A refused save puts the switch back.
  async function setCustomChannel(connectionId: string, custom: boolean) {
    const previous = customChannels[connectionId]
    setCustomOverrides(current => ({ ...current, [connectionId]: custom }))
    const variantIds = panelFollowChoiceVariantIds(variants.map(item => item.variant), connectionId, !custom)
    if (!variantIds.length) return
    try {
      await hubApi('/channel-offers/panel-price-follow', { method: 'PUT', body: JSON.stringify({ connectionId, variantIds, followsPanelPrice: !custom }) })
      await onChanged().catch(() => undefined)
    } catch (error) {
      setCustomOverrides(current => ({ ...current, [connectionId]: previous }))
      const label = channelColumns.find(column => column.id === connectionId)?.label ?? 'Pazaryeri'
      const message = `${label} Özel Fiyat seçimi kaydedilemedi: ${error instanceof Error ? error.message : 'beklenmeyen hata.'}`
      setNotice(`Başarısız: ${message}`); onResult?.('Özel Fiyat Kaydedilemedi', message, 'error')
    }
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
    const targetSelection = targetIds
    const targetSelectionSet = new Set(targetSelection)
    const priceRequested = mode !== 'stock' && ((showListPrice && listPrice.trim() !== '') || salePrice.trim() !== ''); const stockRequested = stockAmount.trim() !== ''
    if (!priceRequested && !stockRequested) { const message = 'Uygulanacak fiyat veya stok değerini girin.'; setNotice(message); onResult?.('Değer Girilmedi', message, 'error'); return }
    // Read as Turkish ("1.250" is 1250); a stock fraction is refused, not rounded.
    const values = bulkEditValues({ showListPrice, listPrice: mode === 'stock' ? '' : listPrice, salePrice: mode === 'stock' ? '' : salePrice, stockAmount })
    if (!values.ok) { setNotice(values.message); onResult?.('Geçersiz Değer', values.message, 'error'); return }
    const { list, sale, amount } = values
    setSaving(true); setNotice('')
    // Every selected variant is attempted; a failure is recorded and the rest still go out. Stock is sent as an
    // absolute value against the stock this dialog opened with, so a change made meanwhile is refused, not undone.
    const failures: Array<{ sku: string; message: string }> = []
    const stockUpdates: VariantStockSet[] = []
    // Variants whose first sale price was taken from the list price typed (no sale price given, none on the offer).
    const saleDefaulted: Array<{ sku: string; currency: string }> = []
    let anyStale = false
    try {
      for (const item of sortedVariants.filter(value => targetSelectionSet.has(value.variant.id))) {
        const variant = item.variant
        if (priceRequested) {
          try {
            if (priceConnectionId === PANEL_PRICE_CHOICE) {
              const target = panelPriceTarget(variant)
              if (target.kind === 'blocked') throw new Error(target.reason)
              const prices = quickEditOfferPrices(panelPrices(variant, item.product), list, sale)
              if (!prices.ok) throw new Error(prices.message)
              await hubApi(`/variants/${target.variantId}/default-prices`, { method: 'PATCH', headers: { 'If-Match': `"v${target.version}"` }, body: JSON.stringify({ listPrice: prices.listPrice, salePrice: prices.salePrice, reason: 'Toplu ürün fiyat düzenleme' }) })
              if (prices.saleDefaulted) saleDefaulted.push({ sku: variant.sku, currency: variant.currency || 'TRY' })
              // The marketplaces without "Özel Fiyat" sell at the panel price: their offers take the same price.
              for (const failure of await writePanelFollowers(variant, followerColumns, prices, 'Toplu ürün fiyat düzenleme')) {
                if (failure.stale) anyStale = true
                failures.push({ sku: variant.sku, message: failure.message })
              }
            } else {
              const target = quickPriceTarget(variant, priceConnectionId, connections.length)
              if (target.kind === 'blocked') throw new Error(target.reason)
              const prices = quickEditOfferPrices(target.kind === 'update' ? target.offer : {}, list, sale)
              if (!prices.ok) throw new Error(prices.message)
              // The marketplace's "Özel Fiyat" switch is stored with the price: ticked keeps its own, unticked follows the panel.
              const body = quickPriceBody(target, variant, prices, 'Toplu ürün fiyat düzenleme', !customChannels[priceConnectionId])
              if (target.kind === 'update') await hubApi(`/channel-offers/${target.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.offerVersion}"` }, body: JSON.stringify(body) })
              else await sendWithVersionedIdempotencyKey(offerCreateKey(variant.id), key => hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(body) }))
              if (prices.saleDefaulted) saleDefaulted.push({ sku: variant.sku, currency: variant.currency || 'TRY' })
            }
          } catch (error) {
            if (isStaleStockError(error)) anyStale = true
            failures.push({ sku: variant.sku, message: `fiyat: ${error instanceof Error ? error.message : 'kaydedilemedi'}` })
          }
        }
        if (stockRequested && amount != null) {
          const expected = stockBaseline.current.get(variant.id) ?? variant.onHand
          const target = stockTarget(stockAction, amount, expected)
          if (target < 0) failures.push({ sku: variant.sku, message: 'stok sıfırın altına inemez.' })
          else stockUpdates.push({ variantId: variant.id, sku: variant.sku, onHand: target, expectedOnHand: expected })
        }
      }
      const stockResult = await applyVariantStockSets(stockUpdates, update => setVariantOnHand(update, 'Toplu ürün stok düzenleme'))
      for (const saved of stockResult.saved) stockBaseline.current.set(saved.variantId, saved.onHand)
      for (const failed of stockResult.failed) { if (failed.stale) anyStale = true; failures.push({ sku: failed.sku, message: `stok: ${failed.message}` }) }
    } finally {
      await onChanged().catch(() => undefined)
      setSaving(false)
    }
    const summary = bulkEditSummary(targetSelection.length, failures)
    // The defaulted sale price is a decision the person did not type: it stays in the notifications to be checked.
    if (saleDefaulted.length && list != null) onResult?.('Satış Fiyatı Liste Fiyatına Eşitlendi', `Satış fiyatı girilmediği için ${saleDefaulted.length} varyantın satış fiyatı liste fiyatına (${formatMoney(list, saleDefaulted[0].currency)}) eşitlendi: ${saleDefaulted.slice(0, 10).map(item => item.sku).join(', ')}${saleDefaulted.length > 10 ? ` ve ${saleDefaulted.length - 10} varyant daha` : ''}. Satış fiyatını kontrol edin.`, 'info', { record: true })
    if (!failures.length) { onResult?.('Hızlı Düzenleme Uygulandı', summary, 'success'); onClose(); return }
    const staleHint = anyStale ? ' Değişen değerler yenilendi; kontrol edip yeniden uygulayın.' : ''
    setNotice(`Başarısız: ${summary}${staleHint}`); onResult?.('Hızlı Düzenleme Tamamlanamadı', summary, 'error')
    // The refreshed list is the user's new baseline once they have been told the stock changed.
    if (anyStale) resetStockBaseline.current = true
  }
  const stepTwoTitle = mode === 'stock' ? 'Stok Değerini Düzenle' : mode === 'price' ? 'Fiyat Değerini Düzenle' : 'Fiyat ve Stok Değerini Düzenle'
  const toggleColorGroup = (color: string) => setOpenColorGroups(current => toggleVariantGroupRow(current, colorOptions, color, groupColumns))
  return <RrModal crumb={mode === 'stock' ? 'Hızlı Stok Güncelleme' : 'Toplu Düzenleme'} title={contextTitle} desc={contextModelCode ? `Model Kodu: ${contextModelCode}` : undefined} size="xl" modalClass={`rr-qe-modal${mode === 'stock' ? ' rr-qe-stock' : ''}`} locked={saving} onClose={onClose} foot={<><span className="rr-note">{activeSelection.length ? <><b>{activeSelection.length} varyant</b> seçildi</> : <>Seçim yok: <b>{targetIds.length} varyantın</b> tümüne uygulanır</>}</span><button type="button" className="rr-btn" onClick={onClose} disabled={saving}>Vazgeç</button><button type="submit" form="product-quick-edit-form" className="rr-btn rr-primary" disabled={saving || !targetIds.length}>{saving ? 'Uygulanıyor…' : draft ? 'Varyantlara Uygula' : activeSelection.length ? 'Seçilenlere Uygula' : 'Tümüne Uygula'}</button></>}>
    <form id="product-quick-edit-form" style={{ display: 'contents' }} onSubmit={apply}>
      <section className="rr-step"><header><span className="rr-stepn">1</span><div className="rr-h3">Varyantları Seç</div><div className="rr-step-tools">
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Renk: {variantShortcutLabel(selectedColors, colorOptions)}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" aria-label="Renk" style={{ minWidth: 240 }}>{colorOptions.map(color => <label key={color} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedColors.includes(color)} onChange={() => toggleColor(color)} /><span className="rr-grow">{colorLabels[color] || formatCatalogOptionLabel(color)}</span><small>{groups[color].length} varyant</small></label>)}</div>}</RrAnchor>
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Beden: {variantShortcutLabel(selectedSizes, sizeOptions)}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" aria-label="Beden" style={{ minWidth: 220 }}>{sizeOptions.map(size => <label key={size} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedSizes.includes(size)} onChange={() => toggleSize(size)} /><span className="rr-grow">{size}</span><small>{variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && sizeOf(item) === size).length} varyant</small></label>)}</div>}</RrAnchor>
      </div></header>
        {/* A stock window's groups sit in two columns (their rows are short); a price window keeps one column. */}
        {groupColumns > 1 && <span id={`${groupPanelId}-pair`} className="sr-only">Aynı satırdaki renk grubuyla birlikte açılır ve kapanır.</span>}
        <div className={`rr-vgroups${mode === 'stock' ? ' rr-two' : ''}`}>{colorOptions.map((color, index) => {
          const items = groups[color]
          const isOpen = openColorGroups.includes(color)
          const selectedCount = items.filter(item => selectedSet.has(item.variant.id)).length
          const groupSelection = variantGroupSelectionState(items.map(item => item.variant.id), selectedSet)
          const groupLabel = colorLabels[color] || formatCatalogOptionLabel(color)
          // A row of two is marked as one: both cards light up on hover, and open with a shared accent.
          const row = Math.floor(index / groupColumns)
          const rowMates = variantGroupRowMates(colorOptions, color, groupColumns)
          const paired = rowMates.length > 1
          const rowPanels = rowMates.filter(mate => openColorGroups.includes(mate)).map(mate => `${groupPanelId}-${colorOptions.indexOf(mate)}`).join(' ')
          return <div className={`rr-vgroup${isOpen ? ' rr-open' : ''}${paired ? ' rr-paired' : ''}${paired && hintedGroupRow === row ? ' rr-row-hint' : ''}`} key={color} onPointerEnter={paired ? () => setHintedGroupRow(row) : undefined} onPointerLeave={paired ? () => setHintedGroupRow(current => current === row ? null : current) : undefined}><div className="rr-vg-h"><input type="checkbox" className="rr-cb" checked={groupSelection.checked} data-mixed={groupSelection.indeterminate ? '' : undefined} aria-label={`${groupLabel} rengindeki tüm varyantları seç`} onChange={event => {
            // Read before the state update: React clears currentTarget once the handler returns, and the updater can
            // run later (on render), where event.currentTarget.checked threw "Cannot read properties of null".
            const checked = event.currentTarget.checked
            const groupIds = items.map(item => item.variant.id)
            clearShortcuts()
            setSelectionDraft(current => updateVariantGroupSelection(current, groupIds, checked))
          }} /><button type="button" className="rr-vg-t" aria-expanded={isOpen} aria-controls={rowPanels || undefined} aria-describedby={paired ? `${groupPanelId}-pair` : undefined} onClick={() => toggleColorGroup(color)}><span className="rr-vg-name"><b title={groupLabel}>{groupLabel}</b><small>{selectedCount ? `${selectedCount}/${items.length} seçili` : `${items.length} varyant`}</small></span>{paired && <span className="rr-vg-link" aria-hidden="true" title="Aynı satırdaki grupla birlikte açılır"><UiIcon name="link" /></span>}<RrIcon name="chev" /></button></div>{isOpen && <div className="rr-qtable-wrap" id={`${groupPanelId}-${index}`} role="region" aria-label={`${groupLabel} varyantları`}>
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
              <tbody>{items.map(item => { const parts = variantAxisParts(item.variant.optionSignature); const fail = (message: string) => setNotice(`Başarısız: ${item.variant.sku}: ${message}`); return <tr className={selectedSet.has(item.variant.id) ? 'rr-on' : 'rr-off'} key={item.variant.id}>
                <td className="rr-qt-sel rr-qt-stick"><input type="checkbox" className="rr-cb" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} aria-label={`${item.variant.optionSignature || item.variant.sku} varyantını seç`} /></td>
                <th scope="row" className="rr-qt-var rr-qt-stick"><span className="rr-qname"><b>{parts.length ? parts.map(part => part.value).join(' · ') : 'Varyant'}</b><small>{item.variant.sku}</small></span></th>
                {draft ? <QuickEditDraftCells label={parts.length ? parts.map(part => part.value).join(' · ') : item.variant.sku} values={draftValues[item.variant.id]} onValue={(field, value) => setDraftValue(item.variant.id, field, value)} /> : <>{showStockColumn && <QuickEditStockCell variant={item.variant} onChanged={onChanged} onStockSaved={(variantId, onHand) => stockBaseline.current.set(variantId, onHand)} onError={fail} />}
                {showPriceColumns && priceColumns.map(column => column.id === PANEL_PRICE_CHOICE || customChannels[column.id]
                  ? <QuickEditPriceCell key={column.id} column={column} followers={column.id === PANEL_PRICE_CHOICE ? followerColumns : []} variant={item.variant} product={item.product} connectionCount={connections.length} onChanged={onChanged} onError={fail} onCustom={phoneWidth && column.id !== PANEL_PRICE_CHOICE ? value => void setCustomChannel(column.id, value) : undefined} />
                  : <QuickEditFollowCell key={column.id} column={column} variant={item.variant} product={item.product} onCustom={phoneWidth ? value => void setCustomChannel(column.id, value) : undefined} />)}</>}
              </tr> })}</tbody>
            </table>
          </div>}</div>
        })}</div>
      </section>
      <section className="rr-step"><header><span className="rr-stepn">2</span><div className="rr-h3">{stepTwoTitle}<TitleHint label={stepTwoTitle}>{draft ? 'Tablodaki hücreleri tek tek değiştirebilir, buradaki değerleri işaretli varyantlara (hiçbiri işaretli değilse tümüne) verebilirsiniz. Uygula yalnız ürün düzenleyicideki varyant tablosunu değiştirir; kalıcı olması için sayfadaki Kaydet’e basın.' : 'Değer işaretli varyantlara uygulanır; hiçbir varyant işaretli değilse tümüne uygulanır.'}</TitleHint></div><small className="rr-lead">{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : `Seçim yok: tüm ${targetIds.length} varyanta uygulanacak`}</small></header>
        {activeSelection.length ? <div className="rr-chips">{selectionChips(variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => item.variant)).map(chip => <span key={chip} className="rr-chip">{chip}</span>)}</div> : null}
        {needsPriceConnection && !draft && <label className="rr-fld"><small>Fiyat Kanalı</small><PanelSelect ariaLabel="Fiyat kanalı" value={priceConnectionId} onChange={setPriceConnectionId} options={[{ value: PANEL_PRICE_CHOICE, label: 'Panel Ana Fiyatı' }, ...connections.map(connection => ({ value: connection.id, label: connection.displayName }))]} /><small className="rr-lead" id="quick-price-choice-help" aria-live="polite">{quickPriceChoiceHelp(priceConnectionId, priceChannelName, followerColumns.map(column => column.label))}</small></label>}
        <div className="rr-dgrid">{mode !== 'stock' && <>{showListPrice && <label className="rr-fld"><small>Liste Fiyatı</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label>}<label className="rr-fld"><small>Satış Fiyatı</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></>}{draft && <label className="rr-fld"><small>Maliyet</small><input className="rr-in rr-num" type="text" inputMode="decimal" autoComplete="off" value={costAmount} onChange={event => setCostAmount(event.target.value)} placeholder="Değiştirme" /></label>}{mode !== 'price' && <><label className="rr-fld"><small>Stok İşlemi</small><PanelSelect ariaLabel="Stok İşlemi" value={stockAction} onChange={setStockAction} options={[{ value: 'SET', label: 'Bu Sayıya Eşitle' }, { value: 'ADD', label: 'Bu Kadar Ekle (+)' }, { value: 'SUBTRACT', label: 'Bu Kadar Çıkar (−)' }]} /></label><label className="rr-fld"><small>Miktar</small><input className="rr-in rr-num" type="text" inputMode="numeric" autoComplete="off" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></>}</div>
      </section>
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
// One variant's stock in the quick edit matrix. The field shows the loaded stock until the user types; fresh data
// replaces it only while it is untouched, and a save sends the typed value as an absolute stock against the value
// shown when editing started (a change made meanwhile is refused, not undone).
function QuickEditStockCell({ variant, onChanged, onStockSaved, onError }: { variant: Variant; onChanged: () => Promise<unknown>; onStockSaved?: (variantId: string, onHand: number) => void; onError: (message: string) => void }) {
  const [stock, setStock] = useState(String(variant.onHand))
  const [stockDirty, setStockDirty] = useState(false)
  const [stockBase, setStockBase] = useState(variant.onHand)
  const [stockError, setStockError] = useState('')
  // The value this cell saved, held until the variant read again carries a new stock (quickStockCellValue).
  const savedStock = useRef<SavedStock | null>(null)
  useEffect(() => {
    if (stockDirty) return
    const view = quickStockCellValue(variant.onHand, savedStock.current)
    savedStock.current = view.saved
    setStock(String(view.value)); setStockBase(view.value)
  }, [stockDirty, variant.onHand])
  const [savingStock, setSavingStock] = useState(false)
  async function saveStock() {
    if (savingStock || !stockDirty) return
    const parsed = parseStockInput(stock)
    if (parsed.kind === 'empty') { setStockError('Stok boş bırakılamaz; değişiklik kaydedilmedi.'); onError('stok: boş bırakılamaz; değişiklik kaydedilmedi.'); setStockDirty(false); return }
    if (parsed.kind === 'invalid') { setStockError('Stok sıfır veya pozitif bir tam sayı olmalıdır.'); onError('stok: sıfır veya pozitif bir tam sayı olmalıdır.'); return }
    if (parsed.value === stockBase) { setStockError(''); setStockDirty(false); return }
    setSavingStock(true); setStockError('')
    try {
      await setVariantOnHand({ variantId: variant.id, sku: variant.sku, onHand: parsed.value, expectedOnHand: stockBase }, 'Hızlı varyant stok düzenleme')
      onStockSaved?.(variant.id, parsed.value)
      savedStock.current = { value: parsed.value, previousOnHand: variant.onHand }
      setStockBase(parsed.value)
      setStockDirty(false)
    } catch (error) {
      setStockError(stockErrorMessage(error)); onError(`stok: ${stockErrorMessage(error)}`)
      // A refused change keeps nothing local: the field shows the stock the server now holds.
      if (isStaleStockError(error)) { savedStock.current = null; setStockDirty(false) }
    } finally {
      setSavingStock(false)
      await onChanged().catch(() => undefined)
    }
  }
  return <td className="rr-qt-stock rr-qt-stick rr-qt-num" data-l="Stok"><input className="rr-in rr-num" aria-label={`${variant.sku} stok`} aria-invalid={stockError ? true : undefined} title={stockError || undefined} value={stock} onChange={event => { setStockError(''); if (!stockDirty) setStockBase(quickStockCellValue(variant.onHand, savedStock.current).value); setStockDirty(true); setStock(event.target.value) }} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} /></td>
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
// One variant's price on one column of the quick edit table: the panel price (the product editor's variant Fiyat /
// Liste Fiyatı) or one ticked ("Özel Fiyat") channel's offer (updated at the version loaded, or created when the
// variant has none there). A panel price saved here also goes to the offers of the marketplaces that follow it
// (`followers`); with none, it sends nothing to a marketplace. Saved when focus leaves the pair, so moving from Liste
// to Satış does not save half a change. A problem is told in the dialog's notice and on the field (red border, title).
function QuickEditPriceCell({ column, followers, variant, product, connectionCount, onChanged, onError, onCustom }: { column: QuickPriceColumn; followers: ReadonlyArray<QuickPriceColumn>; variant: Variant; product: Product; connectionCount: number; onChanged: () => Promise<unknown>; onError: (message: string) => void; onCustom?: (custom: boolean) => void }) {
  const isPanel = column.id === PANEL_PRICE_CHOICE
  const showListPrice = column.takesListPrice
  const shown = quickInlinePrices(column.id, variant, product)
  const shownList = priceFieldText(shown.listPrice)
  const shownSale = priceFieldText(shown.salePrice)
  const [listPrice, setListPrice] = useState(shownList)
  const [salePrice, setSalePrice] = useState(shownSale)
  useEffect(() => { setListPrice(shownList); setSalePrice(shownSale) }, [shownList, shownSale])
  const [savingPrice, setSavingPrice] = useState(false)
  const [priceError, setPriceError] = useState('')
  // The first offer's create key lives as long as the field, so a resend after a lost answer cannot make two offers.
  const [offerCreateKey] = useState(() => newIdempotencyKey('channel-offer-create'))
  const priceTarget = isPanel ? panelPriceTarget(variant) : quickPriceTarget(variant, column.id, connectionCount)
  const failPrice = (message: string) => { setPriceError(message); onError(`${column.label} fiyatı: ${message}`) }
  async function savePrice() {
    if (savingPrice || ((!showListPrice || listPrice === shownList) && salePrice === shownSale)) return
    if (priceTarget.kind === 'blocked') { failPrice(priceTarget.reason); return }
    const prices = inlinePrices(showListPrice ? listPrice : salePrice, salePrice)
    if (!prices.ok) { failPrice(prices.message); return }
    setSavingPrice(true); setPriceError('')
    try {
      if (priceTarget.kind === 'panel') {
        await hubApi(`/variants/${priceTarget.variantId}/default-prices`, { method: 'PATCH', headers: { 'If-Match': `"v${priceTarget.version}"` }, body: JSON.stringify({ listPrice: prices.listPrice, salePrice: prices.salePrice, reason: 'Hızlı varyant fiyat düzenleme' }) })
        const failures = await writePanelFollowers(variant, followers, prices, 'Hızlı varyant fiyat düzenleme')
        if (failures.length) { await onChanged().catch(() => undefined); failPrice(`panel fiyatı kaydedildi; ${failures.map(failure => failure.message).join(' · ')}`); return }
      } else {
        // A marketplace cell is editable only while its "Özel Fiyat" is ticked: the price is stored as its own.
        const body = quickPriceBody(priceTarget, variant, prices, 'Hızlı varyant fiyat düzenleme', false)
        if (priceTarget.kind === 'update') await hubApi(`/channel-offers/${priceTarget.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${priceTarget.offerVersion}"` }, body: JSON.stringify(body) })
        else await sendWithVersionedIdempotencyKey(offerCreateKey, key => hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(body) }))
      }
    } catch (error) {
      failPrice(`kaydedilmedi: ${error instanceof Error ? error.message : 'beklenmeyen hata.'}`)
      return
    } finally {
      setSavingPrice(false)
    }
    await onChanged().catch(() => undefined)
  }
  const priceBlocked = priceTarget.kind === 'blocked' ? priceTarget.reason : ''
  // A card without a saved panel price shows its first channel price, as the product editor does; said on hover.
  const fallback = isPanel && panelPriceIsFallback(variant, product)
  const priceTitle = priceError || priceBlocked || (fallback ? 'Kayıtlı panel fiyatı yok; ürün düzenleyicideki gibi ilk aktif kanal fiyatı gösteriliyor. Değiştirirseniz panel fiyatı olarak kaydedilir.' : undefined)
  const fieldProps = { className: 'rr-in rr-num', 'aria-invalid': priceError ? true : undefined, title: priceTitle, disabled: savingPrice || !!priceBlocked } as const
  return <td className={`rr-qt-gs rr-qt-num${isPanel ? ' rr-qt-panel' : ' rr-qt-own'}${fallback ? ' rr-qfallback' : ''}`} colSpan={showListPrice ? 2 : 1} data-l={isPanel ? 'Panel Fiyatı' : `${column.label} · Özel Fiyat`} title={priceTitle} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) void savePrice() }}>{onCustom && !isPanel && <QuickEditCustomToggle column={column} custom onChange={onCustom} compact className="rr-qt-cardswitch" />}<div className={`rr-qpair${showListPrice ? '' : ' rr-qsale'}`} role="group" aria-label={`${variant.sku} ${column.label}`}>{showListPrice && <QuickPriceInput {...fieldProps} placeholder="Liste" aria-label={`${variant.sku} ${column.label} liste fiyatı`} value={listPrice} onValue={value => { setPriceError(''); setListPrice(value) }} />}<QuickPriceInput {...fieldProps} placeholder="Satış" aria-label={`${variant.sku} ${column.label} satış fiyatı`} value={salePrice} onValue={value => { setPriceError(''); setSalePrice(value) }} /></div></td>
}
