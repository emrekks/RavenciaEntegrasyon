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
  { label: 'UPS', code: 'UPSMP' }
]

function normalizedCargo(value: string | null | undefined) { return (value ?? '').toLocaleUpperCase('tr-TR').replace(/[^\p{L}\p{N}]/gu, '') }

export function cargoCarrier(value: string | null | undefined) {
  const normalized = normalizedCargo(value)
  return cargoCarriers.find(carrier => [carrier.code, carrier.label, ...(carrier.aliases ?? [])].some(candidate => normalized === normalizedCargo(candidate) || normalized.includes(normalizedCargo(candidate.replace(' Kargo', '')))))
}

export function cargoLabel(value: string | null | undefined) {
  return cargoCarrier(value)?.label ?? value ?? 'Kargo bekleniyor'
}

export function cargoMatches(value: string | null | undefined, carrier: CargoCarrier) {
  return cargoCarrier(value)?.code === carrier.code
}

export function CargoProviderIcon({ value, fallbackText = false }: { value: string | null | undefined; fallbackText?: boolean }): ReactNode {
  const carrier = cargoCarrier(value)
  const label = carrier?.label ?? value ?? 'Kargo bekleniyor'
  if (!carrier?.iconUrl) return <span className="cargo-provider-icon cargo-provider-icon-fallback" aria-hidden={!fallbackText}>{fallbackText ? label : label.slice(0, 2).toUpperCase()}</span>
  return <img className="cargo-provider-icon" src={carrier.iconUrl} alt={fallbackText ? label : ''} loading="lazy" decoding="async" referrerPolicy="no-referrer" />
}
