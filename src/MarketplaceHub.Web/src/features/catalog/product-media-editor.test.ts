import { describe, expect, it } from 'vitest'
import { isStoredProductMediaUrl, mediaImageKey, mediaRefsEqual, mediaRefsSameSet, mediaUrlsInPreferredOrder, modelCodeForExistingVariant, publicProductMediaUrls, reorderMediaUrls, uniqueMediaUrls } from './product-media-editor'

describe('product media editing', () => {
  it('does not import a family image when it is outside the editable product media list', () => {
    const productMedia = ['https://cdn.example.test/product.jpg']

    expect(reorderMediaUrls(productMedia, 0, 1)).toBe(productMedia)
  })

  it('reorders only URLs owned by the editable media list', () => {
    expect(reorderMediaUrls(['first.jpg', 'second.jpg', 'third.jpg'], 2, 0))
      .toEqual(['third.jpg', 'first.jpg', 'second.jpg'])
  })

  it('supports repeated reorders by stable positions, including repeated image URLs', () => {
    const first = reorderMediaUrls(['same.jpg', 'same.jpg', 'third.jpg'], 2, 0)

    expect(first).toEqual(['third.jpg', 'same.jpg', 'same.jpg'])
    expect(reorderMediaUrls(first, 2, 0)).toEqual(['same.jpg', 'third.jpg', 'same.jpg'])
  })

  it('reorders a mixed product and family gallery as one visible sequence', () => {
    const visible = ['gray-front.jpg', 'gray-side.jpg', 'green-front.jpg', 'navy-front.jpg']
    const reordered = reorderMediaUrls(visible, 3, 1)

    expect(reordered).toEqual(['gray-front.jpg', 'navy-front.jpg', 'gray-side.jpg', 'green-front.jpg'])
    expect(mediaUrlsInPreferredOrder(['gray-front.jpg', 'gray-side.jpg'], reordered))
      .toEqual(['gray-front.jpg', 'gray-side.jpg'])
  })

  it('uses stable image paths to match family URLs across cache query changes', () => {
    expect(mediaImageKey('https://cdn.example.test/image.jpg?width=80')).toBe(mediaImageKey('https://cdn.example.test/image.jpg?width=800'))
    expect(uniqueMediaUrls(['https://cdn.example.test/image.jpg?width=80', 'https://cdn.example.test/image.jpg?width=800']))
      .toEqual(['https://cdn.example.test/image.jpg?width=80'])
    expect(mediaUrlsInPreferredOrder(['first.jpg', 'second.jpg'], ['second.jpg', 'missing.jpg']))
      .toEqual(['second.jpg', 'first.jpg'])
  })

  it('recognizes saved private media proxy paths without treating external URLs as saved assets', () => {
    const storedUrl = '/api/v1/files/product-media/01a0cf47-a8a7-782c-94d2-f6046d2f8ec5/content'
    expect(isStoredProductMediaUrl(storedUrl)).toBe(true)
    expect(isStoredProductMediaUrl('https://cdn.example.test/product.jpg')).toBe(false)
    expect(isStoredProductMediaUrl('/api/v1/files/product-media/not-a-guid/content')).toBe(false)
    expect(publicProductMediaUrls([storedUrl, 'https://cdn.example.test/product.jpg'])).toEqual(['https://cdn.example.test/product.jpg'])
  })

  it('recognizes unchanged variant image references so they are not deleted and re-uploaded on a product save', () => {
    expect(mediaRefsEqual(['/api/v1/files/product-media/asset/content'], ['/api/v1/files/product-media/asset/content'])).toBe(true)
    expect(mediaRefsEqual([], ['/api/v1/files/product-media/asset/content'])).toBe(false)
    expect(mediaRefsEqual(['second.jpg', 'first.jpg'], ['first.jpg', 'second.jpg'])).toBe(false)
  })

  it('recognizes a pure reorder without treating it as a product media replacement', () => {
    expect(mediaRefsSameSet(['second.jpg', 'first.jpg'], ['first.jpg', 'second.jpg'])).toBe(true)
    expect(mediaRefsSameSet(['first.jpg', 'new.jpg'], ['first.jpg', 'second.jpg'])).toBe(false)
    expect(mediaRefsSameSet(['same.jpg', 'same.jpg'], ['same.jpg', 'other.jpg'])).toBe(false)
  })

  it('preserves each existing model code unless the model-code field was actually edited', () => {
    expect(modelCodeForExistingVariant(' MZ049BOC ', 'MZ049BOC', 'MZ049BOC-GRAY')).toBe('MZ049BOC-GRAY')
    expect(modelCodeForExistingVariant('', '', null)).toBeNull()
    expect(modelCodeForExistingVariant('NEW-CODE', 'MZ049BOC', 'MZ049BOC-GRAY')).toBe('NEW-CODE')
    expect(modelCodeForExistingVariant('', 'MZ049BOC', 'MZ049BOC-GRAY')).toBeNull()
  })
})
