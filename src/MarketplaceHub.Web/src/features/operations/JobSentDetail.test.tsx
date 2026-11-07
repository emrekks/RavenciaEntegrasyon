import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { JobSentDetail } from './JobSentDetail'
import { sentItemStatus, writePartLabel } from './job-write-summary'
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

const base: JobWriteSummary = { productTitle: null, modelCodes: [], mode: null, phase: null, parts: [], requests: [] }

describe('JobSentDetail', () => {
  it('shows each price/stock request, its id, the trigger and the answer per line', () => {
    act(() => root.render(<JobSentDetail write={{
      ...base,
      trigger: 'ORDER',
      parts: [
        { key: 'PRICE', itemCount: 1, fields: ['code', 'listPrice', 'salePrice'], barcodes: ['B-1'], contentCount: 0, attributes: [] },
        { key: 'STOCK', itemCount: 0, fields: [], barcodes: [], contentCount: 0, attributes: [] }
      ],
      requests: [{ key: 'PRICE_STOCK', id: 'p:77:1' }],
      items: [
        { reference: 'B-1', detail: 'Satış 300,00 TRY', status: 'APPLIED', reason: null },
        { reference: 'B-2', detail: 'Stok 0', status: 'REJECTED', reason: 'Barkod bulunamadı' }
      ]
    }} />))
    const text = container.textContent ?? ''
    expect(text).toContain('Gönderilen Bilgiler')
    expect(text).toContain('Sipariş')
    expect(text).toContain('Fiyat/Stok İsteği No')
    expect(text).toContain('p:77:1')
    expect(text).toContain('Liste Fiyatı, Satış Fiyatı')
    expect(text).toContain('Gönderilecek kalem yoktu; istek yapılmadı')
    expect(text).toContain('Uygulandı')
    expect(text).toContain('Barkod bulunamadı')
    expect(text).toContain('2 kayıt · 1 sorunlu')
  })

  it('says the request id and the answers were not recorded instead of guessing', () => {
    act(() => root.render(<JobSentDetail write={{ ...base, parts: [{ key: 'SHIPMENT_PICKING', itemCount: 2, fields: ['lines', 'status'], barcodes: [], contentCount: 0, attributes: [] }], items: [] }} />))
    const text = container.textContent ?? ''
    expect(text).toContain('Paket İşlemi: İşleme Alma')
    expect(text).toContain('Kalemler, Durum')
    expect(text).toContain('Kaydedilmedi')
    expect(text).toContain('Bu iş için kalem bazında sonuç kaydı yok.')
  })

  it('shows only the note for a job that sends nothing itself', () => {
    act(() => root.render(<JobSentDetail write={{ ...base, note: 'Bu iş pazaryerine istek göndermez.' }} />))
    const text = container.textContent ?? ''
    expect(text).toContain('Bu iş pazaryerine istek göndermez.')
    expect(text).not.toContain('Kaydedilmedi')
    expect(text).not.toContain('Gönderim Sonuçları')
  })
})

describe('sent labels', () => {
  it('names the parts and keeps an unknown status as it came', () => {
    expect(writePartLabel('INVOICE_DELIVERY')).toBe('Fatura Bağlantısı')
    expect(writePartLabel('SHIPMENT_SOMETHING_NEW')).toBe('Paket İşlemi: SOMETHING_NEW')
    expect(sentItemStatus('accepted').label).toBe('Pazaryeri Kabul Etti')
    expect(sentItemStatus('ODD')).toEqual({ label: 'ODD', tone: 'info' })
  })
})
