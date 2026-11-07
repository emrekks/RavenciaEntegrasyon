export function productPublicationTargets(isPanelOnlySave: boolean, connectionIds: readonly string[]) {
  return isPanelOnlySave ? [] : [...connectionIds]
}

// Shopify, N11, Pazarama (one groupCode) and PttAVM (one product under the parent barcode) hold a model family's colour
// cards in one marketplace product; Trendyol and Hepsiburada list every card as its own product.
const oneProductPerFamilyPlatforms = new Set(['SHOPIFY', 'N11', 'PAZARAMA', 'PTTAVM'])

export function holdsFamilyAsOneProduct(platformCode: string | null | undefined) {
  return oneProductPerFamilyPlatforms.has(platformCode?.trim().toUpperCase() ?? '')
}

// What Yayınla did on one marketplace for the colour cards of the family. Live 2026-10-28: Shopify refused all 8 cards
// of MZ054YNC and the only trace was one line in a passing toast that said "Ürün güncellendi."; every outcome now names
// its marketplace and says what was queued, what was left as it was (and why) and what was refused.
export type MarketplacePublishOutcome = {
  platformName: string
  platformCode: string
  /** Colour cards of the family Yayınla looked at (1 for a product without a family). */
  familyCards: number
  enqueuedCards: number
  /** Variants the queued jobs carry (a card already on the marketplace sends only its variants not there yet). */
  enqueuedVariants: number
  /** Queued cards that were already on the marketplace and sent only their missing variants. */
  addedToListingCards?: number
  skipped: { live: number; running: number; matched: number }
  /** Where the opened card stands among the family's cards (0 when left out). */
  openedCard?: number
  /** Refusal message → the cards (their places in the family's colour order) it stopped. */
  failures: ReadonlyMap<string, readonly number[]>
}

// What "Güncelle" queued on one marketplace for a product already there.
export type MarketplaceUpdateOutcome = {
  platformName: string
  queued: { productInformation: boolean; priceInventory: boolean }
  /** The price/stock parts the pick sent; both when missing. */
  priceInventoryParts?: { price: boolean; stock: boolean }
  error?: string
  /** A model family's product information update (family-update-plan.ts): every colour card on the marketplace gets
   *  its own job; a card not there is named, never skipped silently. */
  family?: MarketplaceFamilyUpdate
}

export type MarketplaceFamilyUpdate = {
  /** Colour cards of the family. */
  cards: number
  queuedCards: number
  queuedVariants: number
  /** Colours with no variant on this marketplace: not sent. */
  absent: readonly string[]
  /** Refusal message → the colours it stopped. */
  failures: ReadonlyMap<string, readonly string[]>
}

export type ProductSaveOutcome = {
  /** A new product was created (not an edit). */
  created: boolean
  /** The edit form held changes, so the product itself was written. */
  productChanged: boolean
  /** Other things the save wrote: "görseller", "stok", "kanal fiyatları", "diğer renklerin görsel sırası". */
  savedParts: readonly string[]
  versionRetried: boolean
  /** Problems of the save itself (stock refused), each a whole sentence. */
  saveWarnings: readonly string[]
  /** Yayınla was pressed (not Kaydet), with no marketplace selected when the list below is empty. */
  publishRequested: boolean
  publications: readonly MarketplacePublishOutcome[]
  /** Marketplaces whose whole publication step failed before any card (name and message). */
  publicationErrors: readonly { platformName: string; message: string }[]
  /** Marketplaces the user chose whose card showed a required gap, so they were not sent (chosenGapSkipped). A card the
   * user did not choose is left out: a Trendyol-only publish speaks of Trendyol only (owner, 6 Nov 2026). */
  gapSkipped?: readonly { platformName: string; requiredGaps: number }[]
  updates?: readonly MarketplaceUpdateOutcome[]
}

export type ProductSaveFeedback = {
  title: string
  lines: string[]
  kind: 'success' | 'error' | 'info'
  persistent: boolean
  durationMs?: number
}

const cardCount = (count: number) => `${count} renk kartı`

// A refusal of the opened card alone needs no note; another card is named by its place in the colour order.
function cardsNote(indexes: readonly number[], openedCard = 0) {
  if (indexes.length > 1) return ` (${indexes.length} renk kartında)`
  return indexes.length === 1 && indexes[0] !== openedCard ? ` (${indexes[0] + 1}. renk kartı)` : ''
}

function skippedPhrase(outcome: MarketplacePublishOutcome) {
  const single = outcome.familyCards <= 1
  const parts: string[] = []
  const { live, running, matched } = outcome.skipped
  if (live) parts.push(single ? 'zaten yayında' : `${cardCount(live)} zaten yayında`)
  if (matched) parts.push(single ? 'pazaryerinde zaten eşleşmiş' : `${cardCount(matched)} pazaryerinde zaten eşleşmiş`)
  if (running) parts.push(single ? 'yayın işi zaten sürüyor' : `${cardCount(running)} için yayın işi zaten sürüyor`)
  return parts.length ? `${parts.join(', ')}, atlandı` : ''
}

function enqueuedPhrase(outcome: MarketplacePublishOutcome, platformHoldsFamily: boolean) {
  const variants = `${outcome.enqueuedVariants} varyant`
  // Cards already there sent only what they lacked: said so, so the count is not read as the whole card.
  if (outcome.enqueuedCards > 0 && outcome.addedToListingCards === outcome.enqueuedCards) {
    return outcome.familyCards <= 1
      ? `eksik ${variants} gönderiliyor`
      : `${cardCount(outcome.enqueuedCards)} için eksik ${variants} gönderiliyor`
  }
  if (outcome.familyCards <= 1) return `ürün (${variants}) gönderiliyor`
  if (platformHoldsFamily && outcome.enqueuedCards === outcome.familyCards) {
    return `model ailesi (${outcome.enqueuedCards} renk, ${variants}) tek ürün olarak gönderiliyor`
  }
  return `${cardCount(outcome.enqueuedCards)} (${variants}) gönderiliyor`
}

/** The lines one marketplace contributes: what was queued and skipped, then each refusal with the cards it stopped. */
export function marketplacePublishLines(outcome: MarketplacePublishOutcome) {
  const name = outcome.platformName
  const lines: string[] = []
  const skipped = skippedPhrase(outcome)
  if (outcome.enqueuedCards > 0) {
    lines.push(`${name}: ${enqueuedPhrase(outcome, holdsFamilyAsOneProduct(outcome.platformCode))}${skipped ? `; ${skipped}` : ''}.`)
  } else if (skipped) {
    lines.push(`${name}: ${skipped}; yeni gönderim yapılmadı.`)
  }
  for (const [failure, indexes] of outcome.failures) {
    lines.push(`${name}: reddedildi — ${failure.replace(/\s+$/u, '')}${cardsNote(indexes, outcome.openedCard)}`)
  }
  if (!lines.length) lines.push(`${name}: gönderilecek renk kartı bulunamadı.`)
  return lines
}

// "ürün bilgisi" of a model family names how many colours and variants it went for (owner, 7 Nov 2026: RYP003T said
// "3 varyant gönderildi" of a 21-variant family with no word on the other 6 colours).
function productInformationText(family: MarketplaceFamilyUpdate | undefined) {
  if (!family || family.cards <= 1) return 'ürün bilgisi'
  const colours = family.queuedCards === family.cards ? `${family.cards} rengin tamamı` : `${family.cards} renkten ${family.queuedCards} renk`
  return `ürün bilgisi (${colours}, ${family.queuedVariants} varyant)`
}

function familyUpdateFailed(outcome: MarketplaceUpdateOutcome) {
  return Boolean(outcome.family && (outcome.family.failures.size > 0 || outcome.family.absent.length > 0))
}

function marketplaceUpdateLines(outcome: MarketplaceUpdateOutcome) {
  const name = outcome.platformName
  const parts = outcome.priceInventoryParts
  const priceInventory = !parts || parts.price && parts.stock ? 'fiyat-stok' : parts.price ? 'fiyat' : 'stok'
  const queued = [outcome.queued.productInformation && productInformationText(outcome.family), outcome.queued.priceInventory && priceInventory].filter(Boolean).join(' ve ')
  const queuedText = queued ? `${queued} gönderiliyor` : ''
  const lines: string[] = []
  if (!outcome.error) {
    if (queuedText) lines.push(`${name}: ${queuedText}.`)
    else if (!familyUpdateFailed(outcome)) lines.push(`${name}: seçilen bilgilerden bu platforma gidecek olan yok; iş açılmadı.`)
  } else {
    lines.push(`${name}: ${queuedText ? `${queuedText}; ardından ` : ''}aktarım başlatılamadı — ${outcome.error}`)
  }
  const family = outcome.family
  if (family?.absent.length) lines.push(`${name}: ${family.absent.length} renk bu mağazada yok (${family.absent.join(', ')}); bu renkler güncellenmedi. Önce Yayınla ile gönderilmeleri gerekir.`)
  for (const [failure, colours] of family?.failures ?? []) lines.push(`${name}: reddedildi — ${failure.replace(/\s+$/u, '')} (${colours.join(', ')})`)
  return lines
}

function saveLine(outcome: ProductSaveOutcome) {
  const parts = outcome.savedParts.filter(part => part !== 'ürün')
  const retried = outcome.versionRetried ? ' Daha güncel kayıt sürümüyle güvenli şekilde tekrar denendi.' : ''
  if (outcome.created) return `Ürün oluşturuldu${parts.length ? `; ${parts.join(', ')} kaydedildi` : ''}.${retried}`
  if (outcome.productChanged) return `Ürün bilgileri kaydedildi${parts.length ? `; ${parts.join(', ')} güncellendi` : ''}.${retried}`
  return ''
}

/** The toast after Kaydet, Yayınla or Güncelle: a title by outcome and one line per thing that happened. */
export function productSaveFeedback(outcome: ProductSaveOutcome): ProductSaveFeedback {
  const lines: string[] = []
  const first = saveLine(outcome)
  if (first) lines.push(first)
  lines.push(...outcome.saveWarnings)

  let title: string
  let refused = outcome.saveWarnings.length > 0
  if (outcome.updates) {
    const failed = outcome.updates.filter(update => update.error || familyUpdateFailed(update))
    const started = outcome.updates.filter(update => update.queued.productInformation || update.queued.priceInventory)
    lines.push(...outcome.updates.flatMap(marketplaceUpdateLines))
    refused ||= failed.length > 0
    title = !failed.length ? 'Aktarım Başladı' : started.length ? 'Aktarım Kısmen Başladı' : 'Aktarım Başlatılamadı'
  } else if (outcome.publishRequested) {
    for (const publication of outcome.publications) lines.push(...marketplacePublishLines(publication))
    for (const error of outcome.publicationErrors) lines.push(`${error.platformName}: reddedildi — ${error.message}`)
    const gapSkipped = outcome.gapSkipped ?? []
    for (const gap of gapSkipped) lines.push(`${gap.platformName}: gönderilmedi — yayın için ${gap.requiredGaps} zorunlu eksik var; kartındaki eksikler giderilince seçilebilir.`)
    const anyQueued = outcome.publications.some(publication => publication.enqueuedCards > 0)
    const anyRefused = outcome.publicationErrors.length > 0 || outcome.publications.some(publication => publication.failures.size > 0)
    refused ||= anyRefused
    if (!outcome.publications.length && !outcome.publicationErrors.length) {
      if (!gapSkipped.length) lines.push('Yayın için pazaryeri seçilmedi; ürün pazaryerlerine gönderilmedi.')
      title = gapSkipped.length ? 'Yayın Başlatılamadı' : outcome.created ? 'Ürün Oluşturuldu' : 'Ürün Kaydedildi'
    } else {
      // A marketplace left out for a gap keeps the title from saying the whole publication started.
      title = anyQueued ? (anyRefused || gapSkipped.length ? 'Yayın Kısmen Başlatıldı' : 'Aktarım Başladı') : anyRefused ? 'Yayın Başlatılamadı' : outcome.created ? 'Ürün Oluşturuldu' : 'Ürün Kaydedildi'
    }
    // Not sent for a gap is not a refusal, but it is not a success either: the toast stays until it is read.
    if (gapSkipped.length && !refused) {
      if (!lines.length) lines.push('Kaydedilecek yeni değişiklik yoktu.')
      return { title, lines, kind: 'info', persistent: true }
    }
  } else {
    title = outcome.created ? 'Ürün Oluşturuldu' : 'Ürün Kaydedildi'
  }
  if (!lines.length) lines.push('Kaydedilecek yeni değişiklik yoktu.')

  // A refusal or a stock that was not saved stays on screen until it is closed; a full success closes by itself, a
  // little later when it has several lines to read; "nothing new to send" is information, not an error.
  if (refused) return { title, lines, kind: 'error', persistent: true }
  // Something went to a marketplace: it is on its way, not done. The toast stays as work in progress until the followed
  // jobs say how it ended (shared/job-outcome); a success never shows before that (owner, 7 Nov 2026: RYP003T).
  const sending = outcome.updates
    ? outcome.updates.some(update => update.queued.productInformation || update.queued.priceInventory)
    : outcome.publishRequested && outcome.publications.some(publication => publication.enqueuedCards > 0)
  if (sending) return { title, lines, kind: 'info', persistent: true }
  const nothingSent = outcome.publishRequested && !outcome.updates && outcome.publications.length > 0 && outcome.publications.every(publication => publication.enqueuedCards === 0)
  const durationMs = nothingSent ? 8000 : lines.length > 1 ? 6000 : 3500
  return { title, lines, kind: nothingSent ? 'info' : 'success', persistent: false, durationMs }
}

/** The ↻ refresh of one marketplace card: a family held as one marketplace product is read for every card. */
export function publicationRefreshMessage(platformName: string, platformCode: string, familyCards: number) {
  return holdsFamilyAsOneProduct(platformCode) && familyCards > 1
    ? `${platformName}: ${familyCards} renk kartının yayın durumu pazaryerinden yeniden sorgulanıyor.`
    : `${platformName}: yayın durumu pazaryerinden yeniden sorgulanıyor.`
}

/** The stop control of a running publication job, by the state the API answered with. */
export function publicationCancelMessage(platformName: string, jobStatus: string | null | undefined) {
  const status = jobStatus?.trim().toUpperCase()
  if (status === 'CANCELLED') return `${platformName}: yayın işi durduruldu. Pazaryerine önceden gönderilmiş bir istek geri alınamamış olabilir.`
  if (status === 'SUCCEEDED') return `${platformName}: yayın işi durdurulmadan önce tamamlandı.`
  return `${platformName}: yayın işinin durdurulması istendi; çalışan adım bitince durur.`
}

/** The marketplaces Yayınla names as left out for a required gap: only those the user chose (owner, 6 Nov 2026: a
 *  Trendyol-only publish listed Hepsiburada, N11, Pazarama and PttAVM as "gönderilmedi"). A card not chosen says nothing. */
export function chosenGapSkipped<T extends { id: string }>(connections: readonly T[], chosenIds: readonly string[], gapCards: Readonly<Record<string, number>>, platformName: (connection: T) => string) {
  return connections
    .filter(connection => chosenIds.includes(connection.id) && (gapCards[connection.id] ?? 0) > 0)
    .map(connection => ({ platformName: platformName(connection), requiredGaps: gapCards[connection.id] }))
}
