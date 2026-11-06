import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { JobWriteDetail } from './JobWriteDetail'
import type { JobWriteSummary } from './job-write-summary'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

const write: JobWriteSummary = {
  productTitle: 'Triko Kazak',
  modelCodes: ['RYP007'],
  mode: 'APPROVED',
  phase: 'SUBMIT_DELIVERY',
  parts: [
    { key: 'CONTENT', itemCount: 6, fields: ['contentId', 'title', 'description', 'images', 'attributes'], barcodes: [], contentCount: 6, attributes: [{ id: '338', name: 'Beden' }, { id: '99', name: null }] },
    { key: 'VARIANTS', itemCount: 0, fields: [], barcodes: [], contentCount: 0, attributes: [] }
  ],
  requests: [{ key: 'PRODUCT', id: 'batch-1' }]
}

describe('JobWriteDetail', () => {
  it('shows the product, what each request carried, the request id and the answer per variant', () => {
    act(() => root.render(<MemoryRouter><JobWriteDetail write={write} approvalCheck={false} running={false} links={[{ href: '/products/p-1', label: 'Ürüne Git' }]} note={null}
      variantResults={{ source: 'RECORDED', items: [{ barcode: 'RYP007-01', stockCode: 'RYP007-01', options: 'Beden: L', status: 'REJECTED', reason: 'Görsel eksik' }] }} /></MemoryRouter>))
    const text = container.textContent ?? ''
    expect(text).toContain('Triko Kazak')
    expect(text).toContain('RYP007')
    expect(text).toContain('batch-1')
    expect(text).toContain('Başlık, Açıklama, Görseller, Özellikler')
    expect(text).toContain('Beden, Özellik #99')
    expect(text).toContain('6 içerik kartı')
    expect(text).toContain('Gönderilecek kalem yoktu; istek yapılmadı')
    expect(text).toContain('Görsel eksik')
    // A finished job does not show a step.
    expect(text).not.toContain('Şu Anki Adım')
  })

  it('says the product and the variant answers are unknown when the job stored none', () => {
    act(() => root.render(<MemoryRouter><JobWriteDetail write={{ ...write, productTitle: null, modelCodes: [], requests: [] }} approvalCheck={false} running links={[]} note={null} variantResults={{ source: 'NONE', items: [] }} /></MemoryRouter>))
    const text = container.textContent ?? ''
    expect(text).toContain('Ürün adı bilinmiyor')
    expect(text).toContain('Kaydedilmedi')
    expect(text).toContain('Teslimat gönderilecek')
    expect(text).toContain('Bu iş için varyant ayrıntısı yok.')
  })
})
