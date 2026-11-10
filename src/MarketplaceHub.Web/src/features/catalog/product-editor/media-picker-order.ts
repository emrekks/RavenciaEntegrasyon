// "Seçeneklere Görsel Ata" / "Varyant Görsellerini Seç" list the pictures exactly as the Görseller strip shows them
// (owner, 10 Nov 2026: "Birebir ordaki sıralama olmuyor"): the strip's refs in its order, new uploads where they stand,
// then any picture a variant or the family still holds that the strip lacks, and the size chart always last. Görsel NN
// numbers that order, so a label names the same tile as on the page.
import { mediaImageKey } from '../product-media-editor'
import { pinSizeChartLast } from './size-chart-order'

export function mediaPickerRefs(strip: readonly string[], extraUrls: readonly string[], chartKey: string | null): string[] {
  const seen = new Set(strip.filter(ref => ref.startsWith('url|')).map(ref => mediaImageKey(ref.slice(4))))
  const refs = [...strip]
  for (const url of extraUrls) {
    const key = mediaImageKey(url)
    if (seen.has(key)) continue
    seen.add(key)
    refs.push(`url|${url}`)
  }
  return pinSizeChartLast(refs, chartKey)
}

export function mediaPickerLabel(index: number, pendingUpload: boolean): string {
  const label = `Görsel ${String(index + 1).padStart(2, '0')}`
  return pendingUpload ? `${label} · Yüklenen` : label
}
