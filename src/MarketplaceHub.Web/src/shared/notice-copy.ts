// The one wording policy of every notification surface (toasts, the bell, the notification center).
//
//   Title: the short outcome in Title Case, two to five words ("Trendyol Bağlandı", "Webhook Açılamadı").
//   Body:  one or two short sentences that add what the title does not: why it happened and the next step. It never
//          repeats the title, never carries a raw parenthetical dump or an internal code.
//   Kind:  success; info (nothing to do); warning (partly done or action needed); error (it did not happen).
//          A partial result is a warning whose title names the problem.
//
// Producers should write title and body that follow this; composeNotice is the safety net every surface applies, so a
// producer that sends one sentence still reads as a title and a body, and a body that repeats its title is dropped.

export type NoticeKind = 'success' | 'error' | 'info' | 'warning' | 'pending'

export const defaultNoticeTitles: Record<NoticeKind, string> = {
  success: 'İşlem Tamamlandı',
  error: 'İşlem Başarısız',
  info: 'Bilgi',
  warning: 'Uyarı',
  pending: 'İşlem Sürüyor'
}

const lowerWords = new Set(['ve', 'veya', 'ile', 'için', 'ya', 'da', 'de'])

/** The text without case, quotes and punctuation, so "Bağlantı Etkinleştirildi" and "Bağlantı etkinleştirildi." compare equal. */
export function noticeKey(text: string) {
  return text.toLocaleLowerCase('tr-TR').replace(/[’'`´"“”]/gu, '').replace(/[^\p{L}\p{N}]+/gu, ' ').trim()
}

/** Sentences of a body; a full stop inside a number or an abbreviation does not split. */
export function noticeSentences(text: string): string[] {
  return text.split(/(?<=[.!?…])\s+(?=[A-ZÇĞİÖŞÜ0-9"“‘’'(])/u).map(part => part.trim()).filter(Boolean)
}

/** A short sentence as a Title Case title ("Ürün kaydedildi." → "Ürün Kaydedildi"); a long or compound one gives none. */
export function titleFromSentence(sentence: string): string | undefined {
  const clean = sentence.trim().replace(/[.…!]+$/u, '')
  const words = clean.split(/\s+/u).filter(Boolean)
  if (!clean || words.length > 6 || /[:;—,()]/u.test(clean)) return undefined
  // Only an outcome ("kaydedildi", "yüklenemedi", "yükleniyor") reads as a title; an instruction ("Bir ürün seçin") stays a body.
  if (!/(?:d[ıiuü]|t[ıiuü]|yor|[ae]cak)$/u.test(clean.toLocaleLowerCase('tr-TR'))) return undefined
  return words.map((word, index) => index > 0 && lowerWords.has(word.toLocaleLowerCase('tr-TR'))
    ? word.toLocaleLowerCase('tr-TR')
    : word.charAt(0).toLocaleUpperCase('tr-TR') + word.slice(1)).join(' ')
}

/** A long parenthetical ("… açılamadı (reason of two clauses). …") reads as its own clause instead of a dump in brackets. */
export function tidyNoticeBody(text: string) {
  return text.trim().replace(/\s+\(([^()]{24,})\)(?=[.;,:]|\s|$)/gu, ': $1')
}

export type NoticeCopy = { title: string; body: string }

/**
 * Splits a notice into the title and the body that go on screen. An explicit title stays; leading sentences of the body
 * that only say the title again are dropped. Without a title, a short outcome sentence ("Ürün kaydedildi.") becomes it
 * and the rest is the body; anything longer gets the generic title of its kind and keeps the whole body.
 */
export function composeNotice(kind: NoticeKind, title: string | undefined, message: string): NoticeCopy {
  const body = tidyNoticeBody(message)
  const explicit = title?.trim()
  if (explicit) {
    const titleKey = noticeKey(explicit)
    // A legacy body that starts with its title and a colon ("Durum Sorgulanamadı: …").
    const prefixed = /^([^:]{1,80}):\s+(.+)$/su.exec(body)
    const sentences = noticeSentences(prefixed && noticeKey(prefixed[1]) === titleKey ? prefixed[2] : body)
    while (sentences.length && noticeKey(sentences[0]) === titleKey) sentences.shift()
    return { title: explicit, body: sentences.join(' ') }
  }
  const sentences = noticeSentences(body)
  const derived = sentences.length ? titleFromSentence(sentences[0]) : undefined
  if (derived) return { title: derived, body: sentences.slice(1).join(' ') }
  return { title: defaultNoticeTitles[kind], body }
}

/** A notice as its producers write it: the outcome as the title, what it adds as the body. */
export type NoticeInput = { kind: Exclude<NoticeKind, 'pending'>; title: string; message: string }

export function successNotice(title: string, message: string): NoticeInput { return { kind: 'success', title, message } }
export function infoNotice(title: string, message: string): NoticeInput { return { kind: 'info', title, message } }
export function warningNotice(title: string, message: string): NoticeInput { return { kind: 'warning', title, message } }
export function errorNotice(title: string, message: string): NoticeInput { return { kind: 'error', title, message } }
