export type QuestionConversation = { author: string; text: string; createdAt: string; rejectionReason?: string | null }

function isSellerMessage(message: QuestionConversation) {
  const author = message.author.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('tr-TR')
  return author.includes('merchant') || author.includes('seller') || author.includes('satici')
}

function sellerMessageKey(message: QuestionConversation) {
  const timestamp = Date.parse(message.createdAt)
  const displayedMinute = Number.isFinite(timestamp) ? Math.floor(timestamp / 60_000) : message.createdAt
  const normalizedText = message.text.normalize('NFC').trim().replace(/\s+/g, ' ')
  return `${displayedMinute}\u0000${normalizedText}`
}

/**
 * Removes duplicate seller echoes that render with the same text and minute.
 * Marketplaces can include the same sent answer more than once in conversation history.
 */
export function visibleQuestionConversation(messages: QuestionConversation[], questionText: string) {
  const ordered = [...messages].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt))
  const questionIndex = ordered.findIndex(message => message.text.trim() === questionText.trim())
  const visible: QuestionConversation[] = []
  const sellerPositions = new Map<string, number>()

  ordered.forEach((message, index) => {
    if (index === questionIndex) return
    if (!isSellerMessage(message)) {
      visible.push(message)
      return
    }

    const key = sellerMessageKey(message)
    const existingPosition = sellerPositions.get(key)
    if (existingPosition === undefined) {
      sellerPositions.set(key, visible.length)
      visible.push(message)
      return
    }

    const existing = visible[existingPosition]
    if (!existing.rejectionReason?.trim() && message.rejectionReason?.trim())
      visible[existingPosition] = message
  })

  return visible
}
