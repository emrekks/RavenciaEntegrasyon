import { describe, expect, it } from 'vitest'
import { invoiceStatusLabel, invoiceStatusTone, statusLabel } from './status-labels'

describe('package status enum names', () => {
  it('read the same as their underscored codes', () => {
    expect(statusLabel('READYTOSHIP')).toBe('Kargoya hazır')
    expect(statusLabel('ONHOLD')).toBe('Beklemede')
    expect(statusLabel('RETURNINTRANSIT')).toBe('İade taşımada')
    expect(statusLabel('PARTIALLYCANCELLED')).toBe('Kısmi iptal')
  })
})

describe('invoice status labels', () => {
  it('groups invoice states into creating, delivering and failed labels', () => {
    expect(invoiceStatusLabel('FATURA_BEKLIYOR')).toBe('Fatura Bekliyor')
    expect(invoiceStatusLabel('FATURA_ISLENIYOR')).toBe('Fatura İletiliyor')
    expect(invoiceStatusLabel('FATURA_PLATFORMA_BEKLIYOR')).toBe('Fatura İletiliyor')
    expect(invoiceStatusLabel('FATURA_PLATFORMA_AKTARILMADI')).toBe('Fatura İletilemedi')
    expect(invoiceStatusLabel('MARKETPLACE_FAILED')).toBe('Fatura İletilemedi')
    expect(invoiceStatusLabel('MANUAL_REVIEW')).toBe('Fatura Oluşturulamadı')
    expect(invoiceStatusLabel('REJECTED')).toBe('Fatura Oluşturulamadı')
    expect(invoiceStatusLabel('FATURA_KESILDI')).toBe('Faturası Mevcut')
    expect(invoiceStatusTone('FATURA_PLATFORMA_AKTARILMADI')).toBe('danger')
    expect(invoiceStatusTone('FATURA_REDDEDILDI')).toBe('danger')
    expect(invoiceStatusTone('FATURA_ISLENIYOR')).toBe('info')
  })

  it('shows a lost provider answer as uncertain, not as a failed invoice', () => {
    expect(invoiceStatusLabel('FATURA_BELIRSIZ')).toBe('Fatura Sonucu Belirsiz')
    expect(invoiceStatusLabel('UNKNOWN_RESULT')).toBe('Fatura Sonucu Belirsiz')
    expect(invoiceStatusTone('FATURA_BELIRSIZ')).toBe('warning')
    expect(statusLabel('FATURA_BELIRSIZ')).toBe('Fatura sonucu belirsiz')
  })

  it('names invoices that need an adjustment and links that must be removed', () => {
    expect(invoiceStatusLabel('ADJUSTMENT_REQUIRED')).toBe('Fatura Düzeltme Gerekli')
    expect(invoiceStatusTone('ADJUSTMENT_REQUIRED')).toBe('warning')
    expect(statusLabel('REVOCATION_REQUIRED')).toBe('Pazaryerinden kaldırılmalı')
  })

  it('shows a marketplace invoice awaiting reconciliation as issued and successful', () => {
    expect(invoiceStatusLabel('FATURA_KONTROLDE')).toBe('Faturası Mevcut')
    expect(invoiceStatusTone('FATURA_KONTROLDE')).toBe('success')
  })

  it('translates invoice workspace values returned in English', () => {
    expect(statusLabel('None')).toBe('Yok')
    expect(statusLabel('NotInvoiced')).toBe('Fatura kesilmedi')
    expect(statusLabel('Not Sent')).toBe('Gönderilmedi')
    expect(statusLabel('Invoiced')).toBe('Fatura kesildi')
    expect(statusLabel('Remote Present Unmatched')).toBe('Pazaryerinde mevcut, eşleşmedi')
    expect(statusLabel('REMOTE_PRESENT_UNMATCHED')).toBe('Pazaryerinde mevcut, eşleşmedi')
    expect(statusLabel('Delivered')).toBe('Teslim Edildi')
  })
})
