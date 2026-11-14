import { describe, expect, it } from 'vitest'
import { productImportChoiceSummary, productImportModeDescription, productImportNeedsChoice, productImportOptionVisibility, productImportOverwriteConfirmation, productImportStartedMessage } from './product-import-options'

describe('product import price and stock choices', () => {
  it('writes the started toast as sentences, never a chain of fragments', () => {
    const message = productImportStartedMessage({ connectionCount: 1, scope: 'full', includeArchived: true, includeDrafts: false, includePendingApproval: true, contentChoice: false, priceStock: null })
    expect(message).toBe('Mağazadaki tüm ürünler, arşivdekiler ve onay bekleyenler dahil, panele aktarılıyor. Paneldeki mevcut ürün bilgileri değişmeyecek. İlerlemeyi İşlem Takibi’nden izleyebilirsiniz.')
    expect(message).not.toContain('·')
    expect(productImportStartedMessage({ connectionCount: 2, scope: 'existing', includeArchived: false, includeDrafts: false, includePendingApproval: false, contentChoice: true, priceStock: { updatePrices: true, updateStock: false } }))
      .toBe('2 mağazadaki mevcut ürünlerin bilgileri panele aktarılıyor. Paneldeki mevcut ürün bilgileri güncellenecek. Mevcut fiyatlar güncellenecek, mevcut stoklar korunacak. İlerlemeyi İşlem Takibi’nden izleyebilirsiniz.')
  })

  it('asks for confirmation only when something is overwritten, and names what', () => {
    expect(productImportOverwriteConfirmation(false, false)).toBeNull()
    expect(productImportOverwriteConfirmation(true, false)?.title).toBe('Panel Fiyatları Değiştirilsin mi?')
    expect(productImportOverwriteConfirmation(true, false)?.message).not.toContain('stok')
    expect(productImportOverwriteConfirmation(false, true)?.title).toBe('Panel Stokları Değiştirilsin mi?')
    expect(productImportOverwriteConfirmation(true, true)?.title).toBe('Panel Fiyat ve Stokları Değiştirilsin mi?')
  })
})

describe('Mevcut Ürünleri Güncelle choices (owner, 12 Nov 2026)', () => {
  const none = { updateExisting: false, updatePrices: false, updateStock: false }

  it('waits for a choice on a Trendyol existing-products run, nowhere else', () => {
    const base = { method: 'BULK' as const, mode: 'EXISTING_ONLY' as const, hasTrendyol: true, showUpdateExisting: true }
    expect(productImportNeedsChoice({ ...base, ...none })).toBe(true)
    expect(productImportNeedsChoice({ ...base, ...none, updateStock: true })).toBe(false)
    expect(productImportNeedsChoice({ ...base, ...none, updatePrices: true })).toBe(false)
    expect(productImportNeedsChoice({ ...base, ...none, updateExisting: true })).toBe(false)
    expect(productImportNeedsChoice({ ...base, ...none, hasTrendyol: false })).toBe(false)
    expect(productImportNeedsChoice({ ...base, ...none, mode: 'NEW_ONLY' })).toBe(false)
    expect(productImportNeedsChoice({ ...base, ...none, method: 'SINGLE' })).toBe(false)
  })

  it('says in one line what the run will change', () => {
    expect(productImportChoiceSummary('BULK', none)).toBe('En az bir seçenek işaretleyin')
    expect(productImportChoiceSummary('BULK', { ...none, updateStock: true })).toBe('Sadece stok güncellenecek')
    expect(productImportChoiceSummary('BULK', { ...none, updatePrices: true })).toBe('Sadece fiyat güncellenecek')
    expect(productImportChoiceSummary('BULK', { ...none, updatePrices: true, updateStock: true })).toBe('Fiyat ve stok güncellenecek')
    expect(productImportChoiceSummary('BULK', { ...none, updateExisting: true })).toBe('Tüm ürün bilgileri, yeni varyantlar güncellenecek; fiyat ve stok korunacak')
    expect(productImportChoiceSummary('BULK', { updateExisting: true, updatePrices: true, updateStock: true })).toBe('Tüm ürün bilgileri, yeni varyantlar, fiyat ve stok güncellenecek')
    expect(productImportChoiceSummary('BULK', { updateExisting: true, updatePrices: false, updateStock: true })).toBe('Tüm ürün bilgileri, yeni varyantlar ve stok güncellenecek')
    expect(productImportChoiceSummary('SINGLE', none)).toBe('Ürün panelde yoksa eklenir; panelde varsa değişmez')
  })
})

describe('product import options', () => {
  const visibility = (method: 'BULK' | 'SINGLE', mode: 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY' | 'OPTIONS_ONLY', platformCodes: string[], supportsPendingApproval = false) =>
    productImportOptionVisibility({ method, mode, platformCodes, supportsPendingApproval })

  it('offers the archive choice in Ürün Eşleme too, Hepsiburada alone included (owner, 12 Nov 2026)', () => {
    expect(visibility('BULK', 'MAPPING_ONLY', ['HEPSIBURADA'])).toEqual({
      showExistingMode: false,
      showUpdateExisting: false,
      showPriceStock: false,
      updateExistingScopeNote: null,
      priceStockScopeNote: null,
      readOnlyNotes: [],
      showArchived: true,
      showPendingApproval: false,
      showOptionsSection: true
    })
    expect(visibility('BULK', 'MAPPING_ONLY', ['TRENDYOL'], true))
      .toMatchObject({ showUpdateExisting: false, showPriceStock: false, showArchived: true, showPendingApproval: true, showOptionsSection: true })
  })

  it('shows per mode the choices the API reads (Trendyol bulk)', () => {
    const visible = (mode: 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY') => visibility('BULK', mode, ['TRENDYOL'], true)
    expect(visible('FULL')).toMatchObject({ showUpdateExisting: false, showPriceStock: false, showArchived: true, showPendingApproval: true })
    expect(visible('NEW_ONLY')).toMatchObject({ showUpdateExisting: false, showArchived: true, showPendingApproval: true })
    // The API drops listings awaiting approval from Mevcut Ürünleri Güncelle, so the choice is not offered there.
    expect(visible('EXISTING_ONLY')).toMatchObject({ showExistingMode: true, showUpdateExisting: true, showPriceStock: true, updateExistingScopeNote: null, priceStockScopeNote: null, readOnlyNotes: [], showArchived: true, showPendingApproval: false })
    expect(visible('MAPPING_ONLY')).toMatchObject({ showUpdateExisting: false, showArchived: true, showPendingApproval: true })
  })

  it('offers nothing that n11, Pazarama or PTT AVM would ignore, and says so (9.1.9)', () => {
    for (const code of ['N11', 'PAZARAMA', 'PTTAVM', 'HEPSIBURADA']) {
      const options = visibility('BULK', 'EXISTING_ONLY', [code])
      expect(options).toMatchObject({ showExistingMode: false, showUpdateExisting: false, showPriceStock: false })
      expect(visibility('SINGLE', 'FULL', [code])).toMatchObject({ showUpdateExisting: false, showPriceStock: false })
    }
    expect(visibility('BULK', 'FULL', ['N11', 'PAZARAMA']).readOnlyNotes)
      .toEqual(['N11, Pazarama: panelde var olan ürünün bilgisi, fiyatı ve stoğu yazılmaz; ürün yalnız eşlenir, eksik varyantı ve boş desisi eklenir.'])
    expect(visibility('BULK', 'MAPPING_ONLY', ['N11']).readOnlyNotes).toEqual([])
  })

  it('offers Shopify its prices and stock but not the listing content', () => {
    const options = visibility('BULK', 'EXISTING_ONLY', ['SHOPIFY'])
    expect(options).toMatchObject({ showExistingMode: true, showUpdateExisting: false, showPriceStock: true, priceStockScopeNote: null })
    expect(options.readOnlyNotes).toEqual(['Shopify: panelde var olan ürünün bilgisi yazılmaz; yalnız fiyat ve stok seçenekleri uygulanır.'])
    expect(visibility('BULK', 'FULL', ['SHOPIFY']).readOnlyNotes[0]).toMatch(/^Shopify: panelde var olan ürünün bilgisi, fiyatı ve stoğu yazılmaz/)
  })

  it('names the platforms a choice applies to in a mixed selection', () => {
    const options = visibility('BULK', 'EXISTING_ONLY', ['TRENDYOL', 'SHOPIFY', 'N11'])
    expect(options).toMatchObject({ showUpdateExisting: true, showPriceStock: true })
    expect(options.updateExistingScopeNote).toBe("Yalnız Trendyol'da uygulanır.")
    expect(options.priceStockScopeNote).toBe("Yalnız Trendyol ve Shopify'da uygulanır.")
    expect(options.readOnlyNotes).toHaveLength(2)
    expect(visibility('BULK', 'EXISTING_ONLY', ['SHOPIFY', 'PTTAVM']).priceStockScopeNote).toBe("Yalnız Shopify'da uygulanır.")
  })

  it('keeps every choice on offer before a connection is chosen', () => {
    expect(visibility('BULK', 'EXISTING_ONLY', [])).toMatchObject({ showExistingMode: true, showUpdateExisting: true, showPriceStock: true, readOnlyNotes: [] })
  })

  it('offers the update and archive choices for a single product, not the bulk-only pending approval', () => {
    expect(visibility('SINGLE', 'FULL', ['TRENDYOL'], true))
      .toMatchObject({ showUpdateExisting: true, showPriceStock: true, showArchived: true, showPendingApproval: false, showOptionsSection: true })
  })

  it('keeps the Trendyol option-only repair free of unrelated import switches', () => {
    expect(visibility('BULK', 'OPTIONS_ONLY', ['TRENDYOL'])).toMatchObject({
      showUpdateExisting: false,
      showPriceStock: false,
      readOnlyNotes: [],
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    })
  })
})

describe('product import scope descriptions (9.2.16)', () => {
  it('says a full scan keeps the cards the panel holds', () => {
    const trendyol = productImportModeDescription('FULL', ['TRENDYOL'])
    expect(trendyol).not.toContain('panel kayıtlarını günceller')
    expect(trendyol).toContain('Panelde var olan ürünlerin içeriği, fiyatı ve stoğu değişmez; yalnız boş desisi doldurulur.')
    expect(trendyol).toContain('yeni bedenler eklenmez, iş özetinde bildirilir')
    expect(productImportModeDescription('FULL', ['N11'])).not.toContain('beden')
    expect(productImportModeDescription('FULL', ['HEPSIBURADA'])).toContain('yeni ürün açmaz')
  })
})
