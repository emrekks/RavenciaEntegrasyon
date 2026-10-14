import { describe, expect, it } from 'vitest'
import { productImportOptionVisibility, supportsProductOptionsOnly } from './product-import-options'

describe('product import options', () => {
  it('allows option-only repair for bulk Trendyol connections only', () => {
    expect(supportsProductOptionsOnly('BULK', ['TRENDYOL'])).toBe(true)
    expect(supportsProductOptionsOnly('BULK', ['trendyol', 'TRENDYOL'])).toBe(true)
    expect(supportsProductOptionsOnly('SINGLE', ['TRENDYOL'])).toBe(false)
    expect(supportsProductOptionsOnly('BULK', ['TRENDYOL', 'HEPSIBURADA'])).toBe(false)
    expect(supportsProductOptionsOnly('BULK', [])).toBe(false)
  })

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
