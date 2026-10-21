import { describe, expect, it } from 'vitest'
import { marketplaceQuestionSyncChange } from './marketplace-question-sync'

describe('marketplace question sync change', () => {
  it('describes the completed sync without claiming an outbound reply', () => {
    expect(marketplaceQuestionSyncChange.value).toBe('Soru senkronizasyonu')
    expect(marketplaceQuestionSyncChange.detail).toContain('panele aktarıldı')
    expect(marketplaceQuestionSyncChange.detail).toContain('cevap göndermedi')
  })
})
