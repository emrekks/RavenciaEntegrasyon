import { describe, expect, it } from 'vitest'
import { visibleQuestionConversation } from './question-conversation'

describe('visibleQuestionConversation', () => {
  it('shows a single seller reply when duplicate records differ only by seconds', () => {
    const visible = visibleQuestionConversation([
      { author: 'Customer', text: 'Ölçüsü nedir?', createdAt: '2026-10-09T09:00:00Z' },
      { author: 'Merchant', text: 'Tam kalıptır, kendi bedeninizi alabilirsiniz.', createdAt: '2026-10-09T09:15:08Z' },
      { author: 'Seller', text: 'Tam kalıptır, kendi bedeninizi alabilirsiniz.', createdAt: '2026-10-09T09:15:55Z' },
    ], 'Ölçüsü nedir?')

    expect(visible).toHaveLength(1)
    expect(visible[0].author).toBe('Merchant')
  })

  it('keeps later identical replies and retains rejection details from a duplicate', () => {
    const visible = visibleQuestionConversation([
      { author: 'Customer', text: 'Ölçüsü nedir?', createdAt: '2026-10-09T09:00:00Z' },
      { author: 'Merchant', text: 'Tam kalıptır.', createdAt: '2026-10-09T09:15:08Z' },
      { author: 'Seller', text: 'Tam kalıptır.', createdAt: '2026-10-09T09:15:55Z', rejectionReason: 'Kısa açıklama.' },
      { author: 'Merchant', text: 'Tam kalıptır.', createdAt: '2026-10-09T09:16:01Z' },
    ], 'Ölçüsü nedir?')

    expect(visible).toHaveLength(2)
    expect(visible[0].rejectionReason).toBe('Kısa açıklama.')
    expect(visible[1].createdAt).toBe('2026-10-09T09:16:01Z')
  })
})
