import { describe, expect, it } from 'vitest'
import { thumbnailUrl } from './returns-ui'

describe('thumbnailUrl', () => {
  it('asks the Trendyol CDN for a small copy', () => {
    expect(thumbnailUrl('https://cdn.dsmcdn.com/ty1831/prod/a/1_org_zoom.jpg')).toBe('https://cdn.dsmcdn.com/mnresize/256/384/ty1831/prod/a/1_org_zoom.jpg')
  })

  it('collapses a doubled slash in the path', () => {
    expect(thumbnailUrl('https://cdn.dsmcdn.com//ty1940/prod/a/1_org_zoom.jpg')).toBe('https://cdn.dsmcdn.com/mnresize/256/384/ty1940/prod/a/1_org_zoom.jpg')
  })

  it('leaves resized, other-host and relative addresses alone', () => {
    expect(thumbnailUrl('https://cdn.dsmcdn.com/mnresize/128/192/ty1/a.jpg')).toBe('https://cdn.dsmcdn.com/mnresize/128/192/ty1/a.jpg')
    expect(thumbnailUrl('https://img.pzrmcdn.com/a.png')).toBe('https://img.pzrmcdn.com/a.png')
    expect(thumbnailUrl('/api/v1/files/product-media/1/content')).toBe('/api/v1/files/product-media/1/content')
  })
})
