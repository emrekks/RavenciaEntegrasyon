import type { ReactNode } from 'react'

export type CargoCarrier = { label: string; code: string; iconUrl?: string; aliases?: string[] }

export const cargoCarriers: CargoCarrier[] = [
  { label: 'Yurtiçi Kargo', code: 'YKMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/4.png' },
  { label: 'Sürat Kargo', code: 'SURATMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/9.png' },
  { label: 'DHL eCommerce', code: 'DHLECOMMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/10.png' },
  { label: 'PTT Kargo', code: 'PTTMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/19.png' },
  { label: 'Kolay Gelsin', code: 'KOLAYGELSINMP', aliases: ['SENDEOMP'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/38.png' },
  { label: 'Aras Kargo', code: 'ARASMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/7.png' },
  { label: 'Horoz Kargo', code: 'HOROZMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/6.png' },
  { label: 'CEVA Tedarik', code: 'CEVATEDARIK', aliases: ['Ceva Tedarik Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/30.png' },
  { label: 'CEVA Kargo', code: 'CEVAMP', aliases: ['CEVA', 'CEVA Logistics', 'CEVA Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/20.png' },
  { label: 'Trendyol Express', code: 'TEXMP', aliases: ['Trendyol Express Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/17.png' },
  { label: 'hepsiJET', code: 'HEPSIJET', aliases: ['HEPSIJETMP', 'HEPSIJET KARGO', 'HEPSIJET MARKETPLACE', 'D FAST DAĞITIM'], iconUrl: 'https://www.hepsijet.com/images/hepsijet.svg' },
  { label: 'UPS', code: 'UPSMP' }
]

function normalizedCargo(value: string | null | undefined) { return (value ?? '').toLocaleUpperCase('tr-TR').replace(/[^\p{L}\p{N}]/gu, '') }
function cargoFallbackLabel(value: string | null | undefined) {
  const label = value?.trim()
  if (!label) return 'Kargo bekleniyor'
  return label.toLocaleLowerCase('tr-TR') === 'other' ? 'Diğer' : label
}

// The carrier of a raw name, worked out once per name: every order row asks for it several times per render (icon, label,
// filter counts), and each answer upper-cased and stripped every alias of every carrier again (~100 ms per tab change of
// Siparişler on a 4x slowed CPU). The carriers never change while the panel runs.
let carrierKeys: Array<{ carrier: CargoCarrier; keys: Array<[full: string, short: string]> }> | null = null
const carrierByName = new Map<string, CargoCarrier | undefined>()

export function cargoCarrier(value: string | null | undefined) {
  const name = value ?? ''
  if (carrierByName.has(name)) return carrierByName.get(name)
  carrierKeys ??= cargoCarriers.map(carrier => ({ carrier, keys: [carrier.code, carrier.label, ...(carrier.aliases ?? [])].map(candidate => [normalizedCargo(candidate), normalizedCargo(candidate.replace(' Kargo', ''))] as [string, string]) }))
  const normalized = normalizedCargo(value)
  const found = carrierKeys.find(({ keys }) => keys.some(([full, short]) => normalized === full || normalized.includes(short)))?.carrier
  if (carrierByName.size >= 500) carrierByName.clear()
  carrierByName.set(name, found)
  return found
}

export function cargoLabel(value: string | null | undefined) {
  return cargoCarrier(value)?.label ?? cargoFallbackLabel(value)
}

/** Filter choices for raw carrier names. Two raw names can share a label (Aras Kargo / Aras Kargo Marketplace); those show the raw name too, so no two rows read the same. */
export function cargoFilterOptions(values: readonly string[]): Array<{ value: string; label: string }> {
  const labelCounts = new Map<string, number>()
  for (const value of values) labelCounts.set(cargoLabel(value), (labelCounts.get(cargoLabel(value)) ?? 0) + 1)
  return values.map(value => {
    const label = cargoLabel(value)
    const raw = value.trim()
    return { value, label: (labelCounts.get(label) ?? 0) > 1 && raw && raw !== label ? raw : label }
  })
}

export function cargoMatches(value: string | null | undefined, carrier: CargoCarrier) {
  return cargoCarrier(value)?.code === carrier.code
}

export function CargoProviderIcon({ value, fallbackText = false }: { value: string | null | undefined; fallbackText?: boolean }): ReactNode {
  const carrier = cargoCarrier(value)
  const label = carrier?.label ?? cargoFallbackLabel(value)
  if (!carrier?.iconUrl) return <span className="cargo-provider-icon cargo-provider-icon-fallback" aria-hidden={!fallbackText}>{fallbackText ? label : label.slice(0, 2).toUpperCase()}</span>
  return <img className="cargo-provider-icon" src={carrier.iconUrl} alt={fallbackText ? label : ''} loading="lazy" decoding="async" referrerPolicy="no-referrer" />
}
