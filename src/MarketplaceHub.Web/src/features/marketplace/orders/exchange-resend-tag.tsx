import './order-row-marks.css'
import '../returns/return-request-tag.css'

// shippedSource: MARKETPLACE (Trendyol gave the tracking link) or MANUAL (the owner's "Kargoya Verildi" mark, 23 Nov 2026).
export type OrderExchangeResend = { packageId?: string | null; cargoProviderName?: string | null; cargoTrackingNumber?: string | null; cargoTrackingLink?: string | null; shipped: boolean; claimId?: string | null; claimVersion?: number; shippedSource?: string | null }

const manual = (resend: OrderExchangeResend) => resend.shipped && resend.shippedSource === 'MANUAL'

/** The tag's hover: the exchange package as Trendyol named it, or that only the owner's mark lists it. */
export function exchangeResendTitle(resend: OrderExchangeResend) {
  const carrier = [resend.cargoProviderName, resend.cargoTrackingNumber].filter(Boolean).join(' · ')
  if (!resend.packageId) return 'Değişim onaylandı olarak işaretlendi; Trendyol değişim paketini bildirmedi.'
  return `Değişim paketi ${resend.packageId}${carrier ? ` · ${carrier}` : ''}${resend.shipped ? ` · kargoya verildi${manual(resend) ? ' (elle işaretlendi)' : ''}` : ''}`
}

/**
 * The exchange package's state on its row: Trendyol names a tracking number but no link or status for it, so it waits
 * for the carrier until the link comes or the owner marks it handed over (a tracking number alone is not "shipped").
 */
export function exchangeResendStatus(resend: OrderExchangeResend) {
  return !resend.shipped ? 'Kargoya Verilmesi Bekleniyor' : manual(resend) ? 'Kargoya Verildi (Elle)' : 'Kargoya Verildi'
}

/**
 * "Değişim" beside an order the Yeniden Gönderimler tab lists for an exchange (owner, 18 Nov 2026): its claim names
 * Trendyol's exchange package, or the owner marked the exchange made. No tag when it is no such exchange.
 */
export function ExchangeResendTag({ resend }: { resend: OrderExchangeResend | null | undefined }) {
  if (!resend) return null
  return <span className="rr-buyer-tag rr-return-exchange" title={exchangeResendTitle(resend)}>Değişim</span>
}
