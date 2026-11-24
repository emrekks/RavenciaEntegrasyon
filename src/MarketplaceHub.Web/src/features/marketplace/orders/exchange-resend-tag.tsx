import './order-row-marks.css'
import '../returns/return-request-tag.css'

// shippedSource: MARKETPLACE (Trendyol gave the tracking link, or Hepsiburada's exchange line was shipped) or MANUAL (the
// owner's "Kargoya Verildi" mark, 23 Nov 2026).
export type OrderExchangeResend = { packageId?: string | null; cargoProviderName?: string | null; cargoTrackingNumber?: string | null; cargoTrackingLink?: string | null; shipped: boolean; claimId?: string | null; claimVersion?: number; shippedSource?: string | null }

const manual = (resend: OrderExchangeResend) => resend.shipped && resend.shippedSource === 'MANUAL'

// Only Trendyol and Hepsiburada have exchanges (scan O3, 23 Nov 2026: the texts named Trendyol on a Hepsiburada row too).
const exchangePlatforms: Record<string, { name: string; to: string }> = {
  TRENDYOL: { name: 'Trendyol', to: 'Trendyol\'a' },
  HEPSIBURADA: { name: 'Hepsiburada', to: 'Hepsiburada\'ya' }
}

/** The marketplace as the exchange texts name it ("Trendyol", "Hepsiburada'ya"); "Pazaryeri" for any other. */
export function exchangePlatform(platformCode: string | null | undefined) {
  return exchangePlatforms[(platformCode ?? '').toUpperCase()] ?? { name: 'Pazaryeri', to: 'Pazaryerine' }
}

const isHepsiburada = (platformCode: string | null | undefined) => (platformCode ?? '').toUpperCase() === 'HEPSIBURADA'

/**
 * The row's note when no exchange package is named: Trendyol did not send it; Hepsiburada never names one on its claim,
 * its exchange closes when the exchange product (a ClaimChangeAccepted order line) is handed to the carrier, or by the mark.
 */
export function exchangeNoPackageNote(platformCode: string | null | undefined) {
  return isHepsiburada(platformCode)
    ? 'Hepsiburada değişim paketini talepte bildirmez; değişim ürünü kargoya verilince kapanır'
    : `Değişim paketi bilgisi ${exchangePlatform(platformCode).name}'dan gelmedi`
}

/** The tag's hover: the exchange package as the marketplace named it, or that it named none. */
export function exchangeResendTitle(resend: OrderExchangeResend, platformCode: string | null | undefined = 'TRENDYOL') {
  const carrier = [resend.cargoProviderName, resend.cargoTrackingNumber].filter(Boolean).join(' · ')
  if (!resend.packageId) {
    return isHepsiburada(platformCode)
      ? `Değişim onaylandı; Hepsiburada değişim paketini talepte bildirmez${resend.shipped ? ` · kargoya verildi${manual(resend) ? ' (elle işaretlendi)' : ''}` : ''}.`
      : `Değişim onaylandı olarak işaretlendi; ${exchangePlatform(platformCode).name} değişim paketini bildirmedi.`
  }
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
 * Trendyol's exchange package, or the exchange was made (the owner's Trendyol mark, Hepsiburada's finalizedWith Change).
 */
export function ExchangeResendTag({ resend, platformCode }: { resend: OrderExchangeResend | null | undefined; platformCode?: string | null }) {
  if (!resend) return null
  return <span className="rr-buyer-tag rr-return-exchange" title={exchangeResendTitle(resend, platformCode)}>Değişim</span>
}
