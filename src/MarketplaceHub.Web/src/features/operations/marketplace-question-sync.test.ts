import { describe, expect, it } from 'vitest'
import { marketplaceQuestionSyncChange, marketplaceQuestionSyncPresentation } from './marketplace-question-sync'

describe('marketplace question sync presentation', () => {
  it('uses a Turkish name and explains the read-only scope', () => {
    expect(marketplaceQuestionSyncPresentation.title).toBe('Pazaryeri Soru Senkronizasyonu')
    expect(marketplaceQuestionSyncPresentation.description).toContain('salt okunur')
    expect(marketplaceQuestionSyncPresentation.description).toContain('cevap göndermez')
  })

  it('describes the completed sync without claiming an outbound reply', () => {
    expect(marketplaceQuestionSyncChange.value).toBe('Soru senkronizasyonu')
    expect(marketplaceQuestionSyncChange.detail).toContain('panele aktarıldı')
    expect(marketplaceQuestionSyncChange.detail).toContain('cevap göndermedi')
  })
})
