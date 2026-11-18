import './order-row-marks.css'
import '../returns/return-request-tag.css'

export type OrderExchangeResend = { packageId?: string | null; cargoProviderName?: string | null; cargoTrackingNumber?: string | null; cargoTrackingLink?: string | null; shipped: boolean }

/** The tag's hover: the exchange package as Trendyol named it, or that only the owner's mark lists it. */
export function exchangeResendTitle(resend: OrderExchangeResend) {
  const carrier = [resend.cargoProviderName, resend.cargoTrackingNumber].filter(Boolean).join(' · ')
  if (!resend.packageId) return 'Değişim onaylandı olarak işaretlendi; Trendyol değişim paketini bildirmedi.'
  return `Değişim paketi ${resend.packageId}${carrier ? ` · ${carrier}` : ''}${resend.shipped ? ' · kargoya verildi' : ''}`
}

/**
 * "Değişim" beside an order the Yeniden Gönderimler tab lists for an exchange (owner, 18 Nov 2026): its claim names
 * Trendyol's exchange package, or the owner marked the exchange made. No tag when it is no such exchange.
 */
export function ExchangeResendTag({ resend }: { resend: OrderExchangeResend | null | undefined }) {
  if (!resend) return null
  return <span className="rr-buyer-tag rr-return-exchange" title={exchangeResendTitle(resend)}>Değişim</span>
}
