// Product pictures go to the panel and the marketplaces as JPEG or PNG of at most 6 MB. A WebP (a common export from
// design tools and browsers), or a JPEG/PNG over 6 MB, is turned into a JPEG in the browser before it is added, so the
// owner can pick it as it is (6 Nov 2026: a WebP size chart could not be added at all).
export const MAX_PRODUCT_IMAGE_BYTES = 6 * 1024 * 1024
export const PRODUCT_IMAGE_ACCEPT = 'image/jpeg,image/png,image/webp'

export type ProductImageDecision = 'keep' | 'convert' | 'reject'

export function productImageDecision(file: Pick<File, 'type' | 'size'>): ProductImageDecision {
  if (file.size <= 0) return 'reject'
  const kept = file.type === 'image/jpeg' || file.type === 'image/png'
  if (kept && file.size <= MAX_PRODUCT_IMAGE_BYTES) return 'keep'
  if (kept || file.type === 'image/webp') return 'convert'
  return 'reject'
}

export function jpegFileName(name: string) {
  const base = name.replace(/\.[^./\\]+$/u, '')
  return `${base || 'gorsel'}.jpg`
}

export type ImageEncoder = (file: File, quality: number, scale: number) => Promise<Blob | null>

// Draws the picture on a white canvas (a transparent PNG/WebP gets a white ground, as marketplaces show it) and encodes
// a JPEG.
export const canvasJpegEncoder: ImageEncoder = async (file, quality, scale) => {
  const bitmap = await createImageBitmap(file)
  try {
    const width = Math.max(1, Math.round(bitmap.width * scale)); const height = Math.max(1, Math.round(bitmap.height * scale))
    const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height
    const context = canvas.getContext('2d'); if (!context) return null
    context.fillStyle = '#ffffff'; context.fillRect(0, 0, width, height)
    context.drawImage(bitmap, 0, 0, width, height)
    return await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', quality))
  } finally { bitmap.close() }
}

// Full size first at a high quality; only a picture still over 6 MB is encoded smaller.
const attempts: ReadonlyArray<readonly [quality: number, scale: number]> = [[0.92, 1], [0.85, 1], [0.8, 0.85], [0.8, 0.7], [0.75, 0.55]]

export async function toProductImage(file: File, encode: ImageEncoder = canvasJpegEncoder): Promise<{ file: File; converted: boolean } | { error: string }> {
  const decision = productImageDecision(file)
  if (decision === 'keep') return { file, converted: false }
  if (decision === 'reject') return { error: `${file.name}: yalnız JPEG, PNG veya WebP kabul edilir.` }
  try {
    for (const [quality, scale] of attempts) {
      const blob = await encode(file, quality, scale)
      if (!blob) break
      if (blob.size > 0 && blob.size <= MAX_PRODUCT_IMAGE_BYTES) return { file: new File([blob], jpegFileName(file.name), { type: 'image/jpeg' }), converted: true }
    }
  } catch { /* an image the browser cannot open */ }
  return { error: `${file.name}: görsel JPEG'e çevrilemedi; dosyayı JPEG veya PNG olarak kaydedip yeniden deneyin.` }
}
