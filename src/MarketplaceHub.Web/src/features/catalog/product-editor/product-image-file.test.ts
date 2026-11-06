import { describe, expect, it } from 'vitest'
import { MAX_PRODUCT_IMAGE_BYTES, jpegFileName, productImageDecision, toProductImage } from './product-image-file'

const file = (name: string, type: string, size: number) => new File([new Uint8Array(Math.min(size, 16))], name, { type, lastModified: 0 }) as File & { size: number }
const sized = (name: string, type: string, size: number) => Object.defineProperty(file(name, type, size), 'size', { value: size })

describe('product picture files', () => {
  it('keeps JPEG/PNG up to 6 MB, converts WebP and oversize ones, refuses the rest', () => {
    expect(productImageDecision({ type: 'image/png', size: 4_000_000 })).toBe('keep')
    expect(productImageDecision({ type: 'image/webp', size: 300_000 })).toBe('convert')
    expect(productImageDecision({ type: 'image/jpeg', size: MAX_PRODUCT_IMAGE_BYTES + 1 })).toBe('convert')
    expect(productImageDecision({ type: 'image/gif', size: 1000 })).toBe('reject')
    expect(productImageDecision({ type: 'image/png', size: 0 })).toBe('reject')
    expect(jpegFileName('beden-tablosu.webp')).toBe('beden-tablosu.jpg')
  })

  it('turns a WebP size chart into a JPEG, smaller until it fits 6 MB', async () => {
    const calls: Array<[number, number]> = []
    const encode = async (_: File, quality: number, scale: number) => { calls.push([quality, scale]); return new Blob([new Uint8Array(calls.length === 1 ? 8 : 4)], { type: 'image/jpeg' }) }
    const result = await toProductImage(sized('beden-tablosu.webp', 'image/webp', 900_000), encode)
    expect('file' in result && result.file.name).toBe('beden-tablosu.jpg')
    expect('file' in result && result.file.type).toBe('image/jpeg')
    expect(calls).toEqual([[0.92, 1]])
  })

  it('says so when the browser cannot open the picture', async () => {
    const result = await toProductImage(sized('bozuk.webp', 'image/webp', 1000), async () => { throw new Error('decode') })
    expect('error' in result && result.error).toContain('JPEG')
  })
})
