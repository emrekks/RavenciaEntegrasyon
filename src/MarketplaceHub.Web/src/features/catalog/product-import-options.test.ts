import { describe, expect, it } from 'vitest'
import { productImportOptionVisibility, productImportOverwriteConfirmation, productImportPriceStockLabel } from './product-import-options'

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

describe('product import options', () => {
  it('hides an empty options section for Hepsiburada mapping-only runs', () => {
    expect(productImportOptionVisibility({
      method: 'BULK',
      mode: 'MAPPING_ONLY',
      onlyHepsiburada: true,
      hasHepsiburada: true,
      supportsPendingApproval: false
    })).toEqual({
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    })
  })

  it('hides the options heading when mapping has no selected connection or platform-specific options', () => {
    expect(productImportOptionVisibility({
      method: 'BULK',
      mode: 'MAPPING_ONLY',
      onlyHepsiburada: false,
      hasHepsiburada: false,
      supportsPendingApproval: false
    })).toMatchObject({
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    })
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

  it('does not render bulk options in single-product mode', () => {
    expect(productImportOptionVisibility({
      method: 'SINGLE',
      mode: 'FULL',
      onlyHepsiburada: true,
      hasHepsiburada: true,
      supportsPendingApproval: true
    }).showOptionsSection).toBe(false)
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
