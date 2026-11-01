import type { ReactNode } from 'react'
import './order-row-marks.css'

// The small icons beside the order number, as the marketplace states them (OrderRowMarks on the API). Each mark
// comes only from a field the marketplace sent; a missing field shows nothing, never a "no" icon.
export type OrderRowMarks = {
  microExport: boolean
  microExportCountryCode: string | null
  deliveryPoint: boolean
  deliveryPointType: string | null
  fastDeliveryType: string | null
}

const countryNames: Record<string, string> = {
  AE: 'Birleşik Arap Emirlikleri', AZ: 'Azerbaycan', BG: 'Bulgaristan', BH: 'Bahreyn', CZ: 'Çekya', DE: 'Almanya',
  GR: 'Yunanistan', KW: 'Kuveyt', OM: 'Umman', QA: 'Katar', RO: 'Romanya', SA: 'Suudi Arabistan', SK: 'Slovakya',
}

export function countryName(code: string) {
  const upper = code.toUpperCase()
  return countryNames[upper] ?? upper
}

const fastDeliveryLabels: Record<string, string> = {
  todaydelivery: 'Bugün Teslimat',
  samedayshipping: 'Aynı Gün Kargo',
  fastdelivery: 'Hızlı Teslimat',
}

export function fastDeliveryLabel(type: string | null | undefined) {
  return type ? fastDeliveryLabels[type.toLowerCase()] ?? null : null
}

export function microExportLabel(code: string | null | undefined) {
  return code ? `Mikro İhracat · ${countryName(code)}` : 'Mikro İhracat'
}

// Local flag drawings (16 × 11), simplified to read at icon size; no emoji, no remote images.
const flags: Record<string, ReactNode> = {
  RO: <><rect width="5.34" height="11" fill="#002b7f" /><rect x="5.33" width="5.34" height="11" fill="#fcd116" /><rect x="10.66" width="5.34" height="11" fill="#ce1126" /></>,
  BG: <><rect width="16" height="3.67" fill="#fff" /><rect y="3.66" width="16" height="3.68" fill="#00966e" /><rect y="7.33" width="16" height="3.67" fill="#d62612" /></>,
  DE: <><rect width="16" height="3.67" fill="#000" /><rect y="3.66" width="16" height="3.68" fill="#dd0000" /><rect y="7.33" width="16" height="3.67" fill="#ffce00" /></>,
  AZ: <><rect width="16" height="3.67" fill="#00b5e2" /><rect y="3.66" width="16" height="3.68" fill="#ef3340" /><rect y="7.33" width="16" height="3.67" fill="#509e2f" /><circle cx="7.6" cy="5.5" r="1.45" fill="#fff" /><circle cx="8" cy="5.5" r="1.15" fill="#ef3340" /><circle cx="9.3" cy="5.5" r=".5" fill="#fff" /></>,
  GR: <><rect width="16" height="11" fill="#0d5eaf" /><path d="M0 2.44h16M0 4.88h16M0 7.33h16M0 9.77h16" stroke="#fff" strokeWidth="1.22" /><rect width="6.1" height="6.1" fill="#0d5eaf" /><path d="M3.05 0v6.1M0 3.05h6.1" stroke="#fff" strokeWidth="1.22" /></>,
  SA: <><rect width="16" height="11" fill="#006c35" /><path d="M4 4.4h8M4.6 5.4h6.8" stroke="#fff" strokeWidth=".7" /><path d="M4 7.6h7.4" stroke="#fff" strokeWidth=".6" /></>,
  AE: <><rect width="16" height="3.67" fill="#00732f" /><rect y="3.66" width="16" height="3.68" fill="#fff" /><rect y="7.33" width="16" height="3.67" fill="#000" /><rect width="4.2" height="11" fill="#ff0000" /></>,
  QA: <><rect width="16" height="11" fill="#8a1538" /><path d="M0 0h5l1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61 1.6.61-1.6.61L5 11H0z" fill="#fff" /></>,
  BH: <><rect width="16" height="11" fill="#ce1126" /><path d="M0 0h4.5l1.8 1.1-1.8 1.1 1.8 1.1-1.8 1.1 1.8 1.1-1.8 1.1 1.8 1.1-1.8 1.1 1.8 1.1L4.5 11H0z" fill="#fff" /></>,
  KW: <><rect width="16" height="3.67" fill="#007a3d" /><rect y="3.66" width="16" height="3.68" fill="#fff" /><rect y="7.33" width="16" height="3.67" fill="#ce1126" /><path d="M0 0l4 3.67v3.66L0 11z" fill="#000" /></>,
  OM: <><rect width="16" height="11" fill="#db161b" /><rect x="4.4" width="11.6" height="3.67" fill="#fff" /><rect x="4.4" y="7.33" width="11.6" height="3.67" fill="#008000" /></>,
  SK: <><rect width="16" height="3.67" fill="#fff" /><rect y="3.66" width="16" height="3.68" fill="#0b4ea2" /><rect y="7.33" width="16" height="3.67" fill="#ee1c25" /><path d="M3.6 2.6h3.6v3.6c0 1.5-1.8 2.3-1.8 2.3S3.6 7.7 3.6 6.2z" fill="#ee1c25" stroke="#fff" strokeWidth=".5" /></>,
  CZ: <><rect width="16" height="5.5" fill="#fff" /><rect y="5.5" width="16" height="5.5" fill="#d7141a" /><path d="M0 0l7 5.5L0 11z" fill="#11457e" /></>,
}

export function Flag({ code }: { code: string }) {
  const drawing = flags[code.toUpperCase()]
  if (!drawing) return <svg className="rr-omark-ico" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} aria-hidden="true"><circle cx="8" cy="8" r="6" /><path d="M2 8h12M8 2c-2.2 2.4-2.2 9.6 0 12M8 2c2.2 2.4 2.2 9.6 0 12" /></svg>
  return <svg className="rr-omark-flag" width="16" height="11" viewBox="0 0 16 11" aria-hidden="true">{drawing}</svg>
}

function Mark({ tip, kind, children }: { tip: string; kind: string; children: ReactNode }) {
  // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger: focusable so keyboards and taps can read the explanation
  return <span className={`rr-omark rr-omark-${kind}`} tabIndex={0} aria-label={tip} data-tip={tip}>{children}</span>
}

export function OrderRowMarkIcons({ marks }: { marks: OrderRowMarks | null | undefined }) {
  if (!marks) return null
  const fastLabel = fastDeliveryLabel(marks.fastDeliveryType)
  if (!marks.microExport && !marks.deliveryPoint && !fastLabel) return null
  return <span className="rr-omarks">
    {marks.microExport && <Mark kind="micro" tip={microExportLabel(marks.microExportCountryCode)}><Flag code={marks.microExportCountryCode ?? ''} /></Mark>}
    {marks.deliveryPoint && <Mark kind="point" tip="Teslimat Noktası Siparişi"><svg className="rr-omark-ico" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M8 14.2s4.6-4.3 4.6-7.8a4.6 4.6 0 0 0-9.2 0c0 3.5 4.6 7.8 4.6 7.8z" /><circle cx="8" cy="6.4" r="1.7" /></svg></Mark>}
    {fastLabel && <Mark kind="fast" tip={fastLabel}><svg className="rr-omark-ico" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M1.8 4h7.7v7H1.8zM9.5 6.5h2.8l2 2.3V11H9.5" /><circle cx="4.5" cy="11.8" r="1.2" /><circle cx="11.8" cy="11.8" r="1.2" /></svg></Mark>}
  </span>
}
