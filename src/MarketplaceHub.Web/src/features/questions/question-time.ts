export type QuestionDeadline = { text: string; urgent: boolean; expired?: true }

/** Returns null when the marketplace has not supplied a definitive deadline. */
export function questionDeadline(expiresAt: string | null | undefined, now: number): QuestionDeadline | null {
  if (!expiresAt) return null
  const remainingMs = new Date(expiresAt).getTime() - now
  if (!Number.isFinite(remainingMs)) return null
  if (remainingMs <= 0) return { text: 'Süre Doldu', urgent: true, expired: true }
  const hours = Math.floor(remainingMs / 3_600_000)
  const minutes = Math.floor((remainingMs % 3_600_000) / 60_000)
  const days = Math.floor(hours / 24)
  return { text: days ? `${days} gün ${hours % 24} saat` : `${hours} saat ${minutes} dakika`, urgent: remainingMs < 24 * 3_600_000 }
}

/** The list card's deadline line: "Süre Doldu" once it passed, else the time left with "kaldı". */
export function questionDeadlineLabel(deadline: QuestionDeadline) {
  return deadline.expired ? deadline.text : `${deadline.text} kaldı`
}
