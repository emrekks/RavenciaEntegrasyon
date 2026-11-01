import { mediaImageKey } from './product-media-editor'

// "Tümünü Temizle" on the edit page clears the card's own images and the same model's other colours' images shown in
// the strip. The other colours' images belong to those products: they are only marked here and removed on Kaydet.

export type FamilyMediaItem = { url: string; mediaIds: string[]; sourceProductTitles: string[] }

export function familyMediaClearPlan(items: readonly FamilyMediaItem[]) {
  const removable = items.filter(item => item.mediaIds.length > 0)
  return { removable, keptCount: items.length - removable.length }
}

export function withPendingFamilyMediaRemovals(current: readonly FamilyMediaItem[], added: readonly FamilyMediaItem[]) {
  const keys = new Set(current.map(item => mediaImageKey(item.url)))
  return [...current, ...added.filter(item => !keys.has(mediaImageKey(item.url)))]
}

export function pendingFamilyMediaIds(items: readonly FamilyMediaItem[]) {
  return [...new Set(items.flatMap(item => item.mediaIds))]
}

export function familyMediaSourceTitles(items: readonly FamilyMediaItem[]) {
  return [...new Set(items.flatMap(item => item.sourceProductTitles))]
}

export function clearAllMediaConfirmationText(ownCount: number, familyItems: readonly FamilyMediaItem[]) {
  const titles = familyMediaSourceTitles(familyItems)
  const owners = titles.length ? ` (${titles.join(', ')})` : ''
  const own = ownCount ? `Bu ürünün ${ownCount} görseli ve ` : ''
  return `${own}aynı modelin diğer renklerine ait ${familyItems.length} görsel${owners} kaldırılacak. Kaydet’e bastığınızda uygulanır; diğer renk ürünlerinin kayıtlarından da kaldırılır. Pazaryerindeki görseller değişmez.`
}

export function clearAllMediaFeedbackText(ownCount: number, familyCount: number, keptCount: number) {
  const parts = [ownCount ? `bu ürünün ${ownCount} görseli` : '', familyCount ? `diğer renklerin ${familyCount} görseli` : ''].filter(Boolean)
  const removed = parts.length ? `${parts.join(' ve ')} kaldırıldı` : 'Görseller kaldırıldı'
  const sentence = `${removed.charAt(0).toLocaleUpperCase('tr-TR')}${removed.slice(1)}. Kalıcı olması için Kaydet’e basın.`
  return keptCount ? `${sentence} ${keptCount} renk ailesi görselinin kaynak kaydı bulunamadığı için kaldırılamadı; sayfayı yenileyip tekrar deneyin.` : sentence
}

// Runs on Kaydet, after the product itself was saved: one request for every marked image of the other colours.
export async function removePendingFamilyMedia(items: readonly FamilyMediaItem[], send: (mediaIds: string[]) => Promise<{ deletedCount: number }>) {
  const mediaIds = pendingFamilyMediaIds(items)
  if (!mediaIds.length) return 0
  const result = await send(mediaIds)
  return result.deletedCount
}
