import { describe, expect, it, vi } from 'vitest'
import { clearAllMediaConfirmationText, clearAllMediaFeedbackText, familyItemsShownInStrip, familyMediaClearPlan, pendingFamilyMediaIds, removePendingFamilyMedia, withPendingFamilyMediaRemovals } from './family-media-clear'
import { mediaImageKey } from './product-media-editor'

const red = { url: 'https://cdn.dsmcdn.com/red-1.jpg', mediaIds: ['m-red-1', 'm-red-1b'], sourceProductTitles: ['Bluz Kırmızı'] }
const green = { url: 'https://cdn.dsmcdn.com/green-1.jpg', mediaIds: ['m-green-1'], sourceProductTitles: ['Bluz Yeşil'] }
const orphan = { url: 'https://cdn.dsmcdn.com/orphan.jpg', mediaIds: [], sourceProductTitles: [] }

describe('Tümünü Temizle with other colours', () => {
  it('marks every family image that has a source record and counts the rest', () => {
    expect(familyMediaClearPlan([red, green, orphan])).toEqual({ removable: [red, green], keptCount: 1 })
  })

  it('does not mark the same image twice', () => {
    const once = withPendingFamilyMediaRemovals([], [red, green])
    expect(withPendingFamilyMediaRemovals(once, [{ ...red, url: 'https://CDN.dsmcdn.com/red-1.jpg/' }])).toEqual([red, green])
  })

  it('asks with the counts and the colours it touches', () => {
    const text = clearAllMediaConfirmationText(2, [red, green])
    expect(text).toContain('Bu ürünün 2 görseli ve aynı modelin diğer renklerine ait 2 görsel (Bluz Kırmızı, Bluz Yeşil) kaldırılacak.')
    expect(text).toContain('Kaydet’e bastığınızda uygulanır')
  })

  it('says what was cleared and what was left', () => {
    expect(clearAllMediaFeedbackText(2, 15, 0)).toBe('Bu ürünün 2 görseli ve diğer renklerin 15 görseli kaldırıldı. Kalıcı olması için Kaydet’e basın.')
    expect(clearAllMediaFeedbackText(0, 3, 1)).toContain('1 renk ailesi görselinin kaynak kaydı bulunamadığı için kaldırılamadı')
  })

  it('removes every marked image of the other colours in one request on Kaydet', async () => {
    const send = vi.fn().mockResolvedValue({ deletedCount: 3 })
    expect(await removePendingFamilyMedia([red, green], send)).toBe(3)
    expect(send).toHaveBeenCalledTimes(1)
    expect(send).toHaveBeenCalledWith(['m-red-1', 'm-red-1b', 'm-green-1'])
    expect(pendingFamilyMediaIds([red, red])).toEqual(['m-red-1', 'm-red-1b'])
  })

  it('clears every other-colour tile the strip shows in one press, variant pictures of a custom family order included', () => {
    // A custom family order shows each colour's variant pictures too; the family's default list (product-level pictures)
    // does not. The clear used to plan from that list, so the variant pictures stayed and came off one layer per save.
    const own = 'https://cdn.dsmcdn.com/own-1.jpg'
    const bordoVariant = { url: 'https://cdn.dsmcdn.com/bordo-variant.jpg', mediaIds: ['m-bordo-v'], sourceProductTitles: ['Bluz Bordo'] }
    const hakiVariant = { url: 'https://cdn.dsmcdn.com/haki-variant.jpg', mediaIds: ['m-haki-v'], sourceProductTitles: ['Bluz Haki'] }
    const visible = [own, red.url, bordoVariant.url, green.url, hakiVariant.url]
    const shown = familyItemsShownInStrip(visible, [red, green, bordoVariant, hakiVariant], new Set([mediaImageKey(own)]))
    expect(shown).toEqual([red, bordoVariant, green, hakiVariant])
    const plan = familyMediaClearPlan(shown)
    expect(plan).toEqual({ removable: shown, keptCount: 0 })
    // After the press nothing of the other colours is left on the strip.
    const pending = withPendingFamilyMediaRemovals([], plan.removable)
    const pendingKeys = new Set(pending.map(item => mediaImageKey(item.url)))
    expect(visible.filter(url => url !== own && !pendingKeys.has(mediaImageKey(url)))).toEqual([])
    expect(clearAllMediaFeedbackText(1, plan.removable.length, plan.keptCount)).toBe('Bu ürünün 1 görseli ve diğer renklerin 4 görseli kaldırıldı. Kalıcı olması için Kaydet’e basın.')
  })

  it('counts a shown tile without a source record as kept instead of hiding it from the count', () => {
    const shown = familyItemsShownInStrip([red.url, orphan.url, red.url], [red], new Set())
    expect(shown).toEqual([red, orphan])
    expect(familyMediaClearPlan(shown).keptCount).toBe(1)
  })

  it('sends nothing when nothing was marked', async () => {
    const send = vi.fn()
    expect(await removePendingFamilyMedia([], send)).toBe(0)
    expect(send).not.toHaveBeenCalled()
  })
})
