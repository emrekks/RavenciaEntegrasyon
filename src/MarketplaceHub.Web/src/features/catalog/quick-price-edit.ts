// Where a quick price change on the products list goes: the offer the variant has on the channel the person picked
// (at the version the list loaded), or a new offer on that channel. The channel is always the person's choice; the
// screen never prices a channel it picked by itself.

export type QuickPriceOffer = {
  offerId: string; offerVersion: number | null
  listPrice?: number | null; salePrice?: number | null; currency?: string | null; vatRate?: number | null
  vatInclusion?: string | null; roundingMode?: string | null; safetyStock?: number | null; offerStatus?: string | null
  followsPanelPrice?: boolean | null
}

export type QuickPriceTarget =
  | { kind: 'update'; offerId: string; offerVersion: number; offer: QuickPriceOffer }
  | { kind: 'create'; connectionId: string }
  | { kind: 'blocked'; reason: string }

type QuickPriceVariant = {
  platformStatuses?: ReadonlyArray<{ connectionId?: string | null; offerId?: string | null; offerVersion?: number | null; listPrice?: number | null; salePrice?: number | null; currency?: string | null; vatRate?: number | null; vatInclusion?: string | null; roundingMode?: string | null; safetyStock?: number | null; offerStatus?: string | null; followsPanelPrice?: boolean | null; platformCode?: string | null }>
}

// Marketplaces that take a sale price only (Hepsiburada's inventory upload and PTT AVM's price send carry no list
// price): their quick edit hides Liste Fiyatı, and the offer's list price is kept equal to its sale price.
const SALE_PRICE_ONLY_PLATFORMS = new Set(['HEPSIBURADA', 'PTTAVM'])
export function channelTakesListPrice(platformCode: string | null | undefined) {
  return !SALE_PRICE_ONLY_PLATFORMS.has((platformCode ?? '').trim().toUpperCase().replace(/\s/g, ''))
}

// The offer body a quick edit sends: the channel offer's own settings kept (its status too, so a passive offer is not
// switched on), the variant's for a channel that gets its first offer. followsPanelPrice is the channel's "Özel Fiyat"
// choice stored with the offer (unticked → true, ticked → false); left out, the stored choice is kept.
export function quickPriceBody(target: Extract<QuickPriceTarget, { kind: 'update' | 'create' }>, variant: { id: string; currency: string | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null }, prices: { listPrice: number; salePrice: number }, reason: string, followsPanelPrice?: boolean) {
  const source = target.kind === 'update' ? target.offer : null
  return {
    connectionId: target.kind === 'create' ? target.connectionId : '', variantId: variant.id, listPrice: prices.listPrice, salePrice: prices.salePrice,
    currency: source?.currency || variant.currency || 'TRY', vatRate: source?.vatRate ?? variant.vatRate ?? 10, vatInclusion: source?.vatInclusion || variant.vatInclusion || 'INCLUDED',
    roundingMode: source?.roundingMode || variant.roundingMode || 'HALF_EVEN', safetyStock: source?.safetyStock ?? variant.safetyStock ?? 0, status: source?.offerStatus || 'ACTIVE', reason,
    ...(followsPanelPrice == null ? {} : { followsPanelPrice }),
  }
}

// The variant's offer on one channel, read from its per-channel statuses; null when it has none there.
export function channelOffer(variant: QuickPriceVariant, connectionId: string): QuickPriceOffer | null {
  const status = variant.platformStatuses?.find(item => item.connectionId === connectionId && item.offerId)
  return status?.offerId ? { ...status, offerId: status.offerId, offerVersion: status.offerVersion ?? null } : null
}

export function quickPriceTarget(variant: QuickPriceVariant, connectionId: string, connectionCount: number): QuickPriceTarget {
  if (!connectionCount) return { kind: 'blocked', reason: 'Fiyat için aktif platform bağlantısı yok.' }
  if (!connectionId) return { kind: 'blocked', reason: 'Önce Fiyat Kanalı seçin.' }
  // Without the per-channel statuses it is unknown whether the channel already has an offer: a new one could clash.
  if (!variant.platformStatuses) return { kind: 'blocked', reason: 'Platform fiyatları okunamadı; listeyi yenileyip tekrar deneyin.' }
  const offer = channelOffer(variant, connectionId)
  if (!offer) return { kind: 'create', connectionId }
  if (offer.offerVersion == null) return { kind: 'blocked', reason: 'Fiyat sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }
  return { kind: 'update', offerId: offer.offerId, offerVersion: offer.offerVersion, offer }
}

// The prices a bulk edit sends for one variant's offer. A value left empty keeps the offer's own; an offer without a
// sale price (none yet on that channel) takes the list price typed, which every marketplace accepts
// (listPrice >= salePrice: Trendyol, n11, Pazarama and Shopify take them equal, Hepsiburada and PTT AVM send the sale
// price only). The caller warns about each defaulted variant. A sale price of zero is never sent.
export type QuickEditOfferPrices =
  | { ok: true; listPrice: number; salePrice: number; saleDefaulted: boolean }
  | { ok: false; message: string }

export function quickEditOfferPrices(current: { listPrice?: number | null; salePrice?: number | null }, list: number | null, sale: number | null): QuickEditOfferPrices {
  const currentSale = current.salePrice != null && current.salePrice > 0 ? current.salePrice : null
  const saleDefaulted = sale == null && currentSale == null && list != null
  const nextSale = sale ?? currentSale ?? list
  if (nextSale == null || !(nextSale > 0)) return { ok: false, message: 'satış fiyatı girin; sıfır satış fiyatı kaydedilmez.' }
  const nextList = list ?? current.listPrice ?? nextSale
  if (nextList < nextSale) return { ok: false, message: 'liste fiyatı satış fiyatından küçük olamaz.' }
  return { ok: true, listPrice: nextList, salePrice: nextSale, saleDefaulted }
}

// "Panel Ana Fiyatı" in the Fiyat Kanalı select: the product card's own price (the editor's Liste/Satış Fiyatı), not a
// marketplace offer. It is the preset choice because it sends nothing to a marketplace.
export const PANEL_PRICE_CHOICE = 'PANEL'

type PanelPriceSource = { defaultListPrice?: number | null; defaultSalePrice?: number | null }
// The variant's own price fields as the product list sends them: its first active channel offer's (oldest first).
type VariantOfferPriceSource = PanelPriceSource & { listPrice?: number | null; salePrice?: number | null }

// The panel price of a variant exactly as the product editor's variant table shows it (Fiyat / Liste Fiyatı): the
// variant's own, else the card's, else — for a card that never had a panel price, such as one imported from a
// marketplace — the variant's first active channel offer. Both screens use this, so they never show different values.
export function panelPrices(variant: VariantOfferPriceSource, product: PanelPriceSource): { listPrice: number | null; salePrice: number | null } {
  const salePrice = variant.defaultSalePrice ?? product.defaultSalePrice ?? variant.salePrice ?? null
  return { listPrice: variant.defaultListPrice ?? product.defaultListPrice ?? variant.listPrice ?? variant.salePrice ?? null, salePrice }
}

// True when no panel price is saved for the variant or its card, so the value shown is the first channel offer's.
export function panelPriceIsFallback(variant: VariantOfferPriceSource, product: PanelPriceSource) {
  const saved = (variant.defaultSalePrice ?? product.defaultSalePrice) != null || (variant.defaultListPrice ?? product.defaultListPrice) != null
  return !saved && (variant.salePrice != null || variant.listPrice != null)
}

// One price column of the quick edit matrix: the panel price first, then every marketplace connection in the order
// the panel lists them. A sale-price-only marketplace (Hepsiburada, PTT AVM) has a single Satış field.
export type QuickPriceColumn = { id: string; label: string; platformCode: string | null; takesListPrice: boolean }
export function quickPriceColumns(connections: ReadonlyArray<{ id: string; displayName?: string | null; platformCode?: string | null }>): QuickPriceColumn[] {
  return [
    { id: PANEL_PRICE_CHOICE, label: 'Panel Fiyatı', platformCode: null, takesListPrice: true },
    ...connections.map(connection => ({ id: connection.id, label: connection.displayName?.trim() || connection.platformCode || 'Kanal', platformCode: connection.platformCode ?? null, takesListPrice: channelTakesListPrice(connection.platformCode) })),
  ]
}

// "Özel Fiyat" per marketplace, in the quick edit's columns and the product editor's "Pazaryerlerine Özel Fiyat": an
// unticked marketplace sells at the panel (main) price, so a panel price saved in the panel is written to its offers
// too; a ticked one keeps its own prices. The choice is stored per offer (followsPanelPrice). An offer whose choice was
// never stored (null) reads it from its prices: at the panel price it follows the panel; at any other price (or with
// the panel price unknown) it keeps its own, so a panel change never overwrites a price the person set apart. A variant
// with no offer on a marketplace follows the panel. offerFollowsPanel is that rule; every screen reads it from here.
type FollowItem = { variant: QuickPriceVariant & VariantOfferPriceSource; product: PanelPriceSource }
export function offerMatchesPanel(offer: Pick<QuickPriceOffer, 'listPrice' | 'salePrice'>, panel: { listPrice: number | null; salePrice: number | null }, takesListPrice: boolean) {
  if (panel.salePrice == null || offer.salePrice == null || !samePrice(offer.salePrice, panel.salePrice)) return false
  if (!takesListPrice) return true
  return panel.listPrice != null && offer.listPrice != null && samePrice(offer.listPrice, panel.listPrice)
}
export function offerFollowsPanel(offer: (Pick<QuickPriceOffer, 'listPrice' | 'salePrice' | 'followsPanelPrice'>) | null | undefined, panel: { listPrice: number | null; salePrice: number | null }, takesListPrice: boolean): boolean {
  if (offer == null) return true
  if (offer.followsPanelPrice != null) return offer.followsPanelPrice
  return offerMatchesPanel(offer, panel, takesListPrice)
}
// One marketplace's choice over several variants (a card's variants, a quick edit's products): its own price as soon as
// one variant's offer there keeps its own, so no variant's own price is overwritten by a panel save.
export function channelKeepsOwnPrice(items: ReadonlyArray<FollowItem>, connectionId: string, takesListPrice: boolean): boolean {
  return items.some(item => !offerFollowsPanel(channelOffer(item.variant, connectionId), panelPrices(item.variant, item.product), takesListPrice))
}
export function initialCustomChannels(items: ReadonlyArray<FollowItem>, columns: ReadonlyArray<QuickPriceColumn>): Record<string, boolean> {
  return Object.fromEntries(columns.filter(column => column.id !== PANEL_PRICE_CHOICE).map(column => [column.id, channelKeepsOwnPrice(items, column.id, column.takesListPrice)]))
}

// The product editor's "Pazaryerlerine Özel Fiyat" cards as an existing product opens: every marketplace whose choice
// (channelKeepsOwnPrice over the card's variants) is its own price, with the prices of the first variant (in the order
// given) whose offer there keeps its own. Marketplaces that follow the panel are left out (their switch is off).
export function editorCustomChannelPrices(variants: ReadonlyArray<QuickPriceVariant & VariantOfferPriceSource>, product: PanelPriceSource): Record<string, { listPrice: string; salePrice: string }> {
  const connections = new Map<string, string | null>()
  for (const variant of variants) for (const status of variant.platformStatuses ?? []) if (status.connectionId && !connections.has(status.connectionId)) connections.set(status.connectionId, status.platformCode ?? null)
  return Object.fromEntries([...connections].flatMap(([connectionId, platformCode]) => {
    const takesListPrice = channelTakesListPrice(platformCode)
    const own = variants.map(variant => channelOffer(variant, connectionId)).find((offer, index) => offer != null && !offerFollowsPanel(offer, panelPrices(variants[index], product), takesListPrice))
    if (!own) return []
    const salePrice = own.salePrice ?? own.listPrice ?? 0
    return [[connectionId, { listPrice: String(own.listPrice ?? salePrice), salePrice: String(salePrice) }]]
  }))
}

// The variants whose offer on a marketplace is to store a new "Özel Fiyat" choice (a variant without an offer there has
// nothing to store; an offer already holding the choice is left out).
export function panelFollowChoiceVariantIds(variants: ReadonlyArray<QuickPriceVariant & { id: string }>, connectionId: string, followsPanelPrice: boolean): string[] {
  return variants.flatMap(variant => {
    const offer = channelOffer(variant, connectionId)
    return offer && offer.followsPanelPrice !== followsPanelPrice ? [variant.id] : []
  })
}

// What the product editor writes to one variant's offer on one marketplace when it saves channel prices (a first
// publish, "Kaydet ve Yayınla", "Pazaryerlerinde Güncelle"). A marketplace that follows the panel takes the panel price.
// One with its own price takes the price typed in its card only when the person changed that price (or the variant has
// no offer there yet); otherwise each variant keeps its own offer price, so a card that shows one variant's price never
// overwrites the others'. A per-variant price typed in the variant price table wins over both.
export type EditorChannelOfferInput = {
  custom: boolean
  panel: { listPrice: number; salePrice: number }
  card: { listPrice: number; salePrice: number }
  cardEdited: boolean
  variantPrice?: { listPrice: number; salePrice: number } | null
  offer?: { listPrice?: number | null; salePrice?: number | null } | null
}
export function editorChannelOfferPrices(input: EditorChannelOfferInput): { listPrice: number; salePrice: number; followsPanelPrice: boolean } {
  const followsPanelPrice = !input.custom
  if (input.variantPrice) return { ...input.variantPrice, followsPanelPrice }
  if (!input.custom) return { ...input.panel, followsPanelPrice }
  const own = input.offer?.salePrice != null && input.offer.salePrice > 0 ? { listPrice: input.offer.listPrice ?? input.offer.salePrice, salePrice: input.offer.salePrice } : null
  return { ...(input.cardEdited || !own ? input.card : own), followsPanelPrice }
}

// The choice a price typed for one variant's marketplace offer stores: a price apart from the variant's panel price is
// its own (false); one equal to it keeps the stored choice (undefined: not sent).
export function typedOfferPriceChoice(prices: { listPrice: number; salePrice: number }, panel: { listPrice: number | null; salePrice: number | null }, takesListPrice: boolean): false | undefined {
  return offerMatchesPanel(prices, panel, takesListPrice) ? undefined : false
}

// The marketplaces that follow the panel price: every marketplace column not ticked "Özel Fiyat".
export function panelFollowerColumns(columns: ReadonlyArray<QuickPriceColumn>, custom: Readonly<Record<string, boolean>>) {
  return columns.filter(column => column.id !== PANEL_PRICE_CHOICE && !custom[column.id])
}

// What a panel price saved for one variant writes to the marketplaces that follow it: the variant's existing offer
// on each, at the version loaded, with the same list and sale price (a sale-price-only marketplace takes list = sale).
// An offer already at that price is left alone, and a marketplace where the variant has no offer gets none: creating
// one is publishing, which stays with "Platformlarda Yayınla" and the product editor.
export type PanelFollowerWrite =
  | { kind: 'update'; column: QuickPriceColumn; offerId: string; offerVersion: number; offer: QuickPriceOffer; listPrice: number; salePrice: number }
  | { kind: 'blocked'; column: QuickPriceColumn; reason: string }
export function panelFollowerWrites(variant: QuickPriceVariant, followers: ReadonlyArray<QuickPriceColumn>, prices: { listPrice: number; salePrice: number }): PanelFollowerWrite[] {
  if (!followers.length) return []
  if (!variant.platformStatuses) return followers.map(column => ({ kind: 'blocked' as const, column, reason: 'Platform fiyatları okunamadı; listeyi yenileyip tekrar deneyin.' }))
  return followers.flatMap<PanelFollowerWrite>(column => {
    const offer = channelOffer(variant, column.id)
    if (!offer) return []
    const listPrice = column.takesListPrice ? prices.listPrice : prices.salePrice
    if (offerMatchesPanel(offer, { listPrice, salePrice: prices.salePrice }, column.takesListPrice)) return []
    if (offer.offerVersion == null) return [{ kind: 'blocked', column, reason: 'Fiyat sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }]
    return [{ kind: 'update', column, offerId: offer.offerId, offerVersion: offer.offerVersion, offer, listPrice, salePrice: prices.salePrice }]
  })
}

function samePrice(left: number, right: number) {
  return Math.abs(left - right) < 0.005
}

// A price as the table shows it outside editing: Turkish digits, two decimals ("1.249,90"); empty for none.
export function formatPriceField(value: string): string {
  if (value.trim() === '') return ''
  const number = Number(value)
  return Number.isFinite(number) ? number.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : value
}

// A price typed into the table, as the number fields take it: a decimal comma becomes a point and thousands dots go
// ("1.249,90" -> "1249.90"); a value with a point only is kept as typed.
export function normalizePriceInput(raw: string): string {
  const text = raw.replace(/[\s₺]/g, '')
  if (!text.includes(',')) return text
  return text.replace(/\./g, '').replace(',', '.')
}

// Every query that shows a variant's panel price, channel prices or stock: the products list with its summary, counts
// and low-stock details (['products', …]) and the product editor (['product', id]). A quick edit save invalidates all
// of them so an open editor shows the same values.
export const QUICK_EDIT_SYNC_KEYS: ReadonlyArray<readonly string[]> = [['products'], ['product']]

// The list and sale price the per-variant fields show for the choice: the panel's, the chosen channel's offer, or none.
export function quickInlinePrices(choice: string, variant: QuickPriceVariant & VariantOfferPriceSource, product: PanelPriceSource): { listPrice: number | null; salePrice: number | null } {
  if (choice === PANEL_PRICE_CHOICE) return panelPrices(variant, product)
  const offer = choice ? channelOffer(variant, choice) : null
  return { listPrice: offer?.listPrice ?? null, salePrice: offer?.salePrice ?? null }
}

// What the choice changes, said under the select so it is never a guess.
export function quickPriceChoiceHelp(choice: string, channelName?: string | null, followerNames: ReadonlyArray<string> = []): string {
  if (choice === PANEL_PRICE_CHOICE && followerNames.length) return `Ürün kartındaki ana fiyat (Liste/Satış Fiyatı) değişir. Özel Fiyat işaretli olmayan pazaryerleri (${followerNames.join(', ')}) ana fiyatla satılır: seçili varyantların oradaki fiyatı da aynı değere güncellenir. Özel Fiyat işaretli pazaryerlerinin fiyatı değişmez.`
  if (choice === PANEL_PRICE_CHOICE) return "Ürün kartındaki ana fiyat (Liste/Satış Fiyatı) değişir; pazaryerlerindeki fiyatlar değişmez ve pazaryerine hiçbir şey gönderilmez. Ana fiyat, ürün bir kanalda ilk kez yayınlanırken (Platformlarda Yayınla) ve ürün düzenleyicide 'Ana fiyatla satılır' kanallar kaydedilirken kullanılır."
  if (choice) return `Yalnız ${channelName || 'seçilen kanalın'} fiyatı değişir; panel ana fiyatı ve diğer kanalların fiyatı değişmez. Bu kanalda fiyatı olmayan varyantın ilk fiyatı oluşturulur.`
  return 'Fiyatın nereye yazılacağını seçin: Panel Ana Fiyatı ya da tek bir pazaryeri kanalı.'
}

// The group label of the per-variant price fields.
export function quickPriceChoiceLabel(choice: string, channelName?: string | null): string {
  if (choice === PANEL_PRICE_CHOICE) return 'Panel Fiyatı'
  return choice ? channelName || 'Kanal Fiyatı' : 'Fiyat'
}

// Where a panel price change goes: the variant, at the version the list loaded.
export type PanelPriceTarget = { kind: 'panel'; variantId: string; version: number } | { kind: 'blocked'; reason: string }
export function panelPriceTarget(variant: { id: string; version?: number | null }): PanelPriceTarget {
  if (variant.version == null) return { kind: 'blocked', reason: 'Varyant sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }
  return { kind: 'panel', variantId: variant.id, version: variant.version }
}

// The list and sale price typed into the two per-variant fields, checked as the server checks them. An empty list
// price takes the sale price.
export function inlinePrices(list: string, sale: string): { ok: true; listPrice: number; salePrice: number } | { ok: false; message: string } {
  const salePrice = Number(sale)
  if (sale.trim() === '' || !Number.isFinite(salePrice) || salePrice <= 0) return { ok: false, message: 'Satış fiyatı sıfırdan büyük olmalıdır.' }
  const listPrice = list.trim() === '' ? salePrice : Number(list)
  if (!Number.isFinite(listPrice) || listPrice < salePrice) return { ok: false, message: 'Liste fiyatı satış fiyatından küçük olamaz.' }
  if (!hasAtMostTwoDecimals(listPrice) || !hasAtMostTwoDecimals(salePrice)) return { ok: false, message: 'Fiyat en fazla iki ondalık basamak içerebilir.' }
  return { ok: true, listPrice, salePrice }
}

export function hasAtMostTwoDecimals(value: number) {
  return Math.abs(Math.round(value * 100) - value * 100) < 1e-6
}
