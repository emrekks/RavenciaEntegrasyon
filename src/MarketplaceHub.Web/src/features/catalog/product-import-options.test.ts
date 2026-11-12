import { describe, expect, it } from 'vitest'
import { productImportChoiceSummary, productImportNeedsChoice, productImportOptionVisibility, productImportOverwriteConfirmation, productImportPriceStockLabel } from './product-import-options'

describe('product import price and stock choices', () => {
  it('labels prices and stock separately', () => {
    expect(productImportPriceStockLabel(true, false)).toBe(' · mevcut fiyatlar güncellenecek · mevcut stoklar korunacak')
    expect(productImportPriceStockLabel(false, true)).toBe(' · mevcut fiyatlar korunacak · mevcut stoklar güncellenecek')
    expect(productImportPriceStockLabel(false, false)).toBe(' · mevcut fiyatlar korunacak · mevcut stoklar korunacak')
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
  it('offers the archive choice in Ürün Eşleme too, Hepsiburada alone included (owner, 12 Nov 2026)', () => {
    expect(productImportOptionVisibility({
      method: 'BULK',
      mode: 'MAPPING_ONLY',
      onlyHepsiburada: true,
      hasHepsiburada: true,
      supportsPendingApproval: false
    })).toEqual({
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: true,
      showPendingApproval: false,
      showOptionsSection: true
    })
    expect(productImportOptionVisibility({ method: 'BULK', mode: 'MAPPING_ONLY', onlyHepsiburada: false, hasHepsiburada: false, supportsPendingApproval: true }))
      .toEqual({ showUpdateExisting: false, showHepsiburadaReadOnlyNote: false, showArchived: true, showPendingApproval: true, showOptionsSection: true })
  })

  it('shows per mode the choices the API reads (Trendyol bulk)', () => {
    const visible = (mode: 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY') => productImportOptionVisibility({ method: 'BULK', mode, onlyHepsiburada: false, hasHepsiburada: false, supportsPendingApproval: true })
    expect(visible('FULL')).toMatchObject({ showUpdateExisting: false, showArchived: true, showPendingApproval: true })
    expect(visible('NEW_ONLY')).toMatchObject({ showUpdateExisting: false, showArchived: true, showPendingApproval: true })
    // The API drops listings awaiting approval from Mevcut Ürünleri Güncelle, so the choice is not offered there.
    expect(visible('EXISTING_ONLY')).toMatchObject({ showUpdateExisting: true, showArchived: true, showPendingApproval: false })
    expect(visible('MAPPING_ONLY')).toMatchObject({ showUpdateExisting: false, showArchived: true, showPendingApproval: true })
  })

  it('keeps Hepsiburada read-only guidance and archive selection in the shared options layout', () => {
    expect(productImportOptionVisibility({
      method: 'BULK',
      mode: 'FULL',
      onlyHepsiburada: true,
      hasHepsiburada: true,
      supportsPendingApproval: false
    })).toMatchObject({
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: true,
      showArchived: true,
      showOptionsSection: true
    })
  })

  it('offers the update and archive choices for a single product, not the bulk-only pending approval', () => {
    expect(productImportOptionVisibility({
      method: 'SINGLE',
      mode: 'FULL',
      onlyHepsiburada: false,
      hasHepsiburada: false,
      supportsPendingApproval: true
    })).toEqual({ showUpdateExisting: true, showHepsiburadaReadOnlyNote: false, showArchived: true, showPendingApproval: false, showOptionsSection: true })
  })

  it('keeps Hepsiburada single products read only: no update choices, the archive choice stays', () => {
    const visibility = productImportOptionVisibility({ method: 'SINGLE', mode: 'FULL', onlyHepsiburada: true, hasHepsiburada: true, supportsPendingApproval: false })
    expect(visibility.showUpdateExisting).toBe(false)
    expect(visibility.showArchived).toBe(true)
  })

  it('keeps the Trendyol option-only repair free of unrelated import switches', () => {
    expect(productImportOptionVisibility({
      method: 'BULK',
      mode: 'OPTIONS_ONLY',
      onlyHepsiburada: false,
      hasHepsiburada: false,
      supportsPendingApproval: false
    })).toEqual({
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    })
  })
})
