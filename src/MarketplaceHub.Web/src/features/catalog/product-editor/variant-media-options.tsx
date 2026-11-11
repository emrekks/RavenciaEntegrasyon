// The tiles of "Varyant Görsellerini Seç" / "Seçeneklere Görsel Ata". The model's size chart is not a choice: it goes
// last with every variant of every colour card on every marketplace, whatever a variant was assigned (the server's
// PublicationVariantMedia, owner 12.11.2026: "51bcy beden tablosu seçilmemiş neden?"). Its tile is therefore shown
// ticked and locked, with "Otomatik eklenir (sonda)", and no click or Görselleri Kaldır changes it.
import { useEffect, useState } from 'react'
import { UiIcon } from '../../../shared/components'
import { isSizeChartRef } from './size-chart-order'

export type ProductMediaOption = { value: string; label: string; url?: string; file?: File }

export const SIZE_CHART_AUTOMATIC_NOTE = 'Otomatik eklenir (sonda)'
const SIZE_CHART_AUTOMATIC_HINT = 'Beden tablosu her varyantın görsellerine sonda otomatik eklenir; seçimi değiştirilemez.'

export function VariantImageIcon() {
  return <svg className="variant-media-icon" viewBox="0 0 24 24" focusable="false" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2" /><circle cx="8.5" cy="9" r="1.5" /><path d="m4 17 5-5 3 3 2-2 6 6" /></svg>
}

// A click on a tile: the chart never toggles; a list left with the chart alone is no assignment (the variant then goes
// with its card's gallery, the chart last), never "only the chart".
export function toggleVariantMediaRef(options: readonly ProductMediaOption[], selectedRefs: readonly string[], value: string, chartKey: string | null): string[] {
  if (isSizeChartRef(value, chartKey)) return [...selectedRefs]
  const next = selectedRefs.includes(value) ? selectedRefs.filter(ref => ref !== value) : [...selectedRefs, value]
  return withoutChartOnly(options.filter(item => next.includes(item.value)).map(item => item.value), chartKey)
}

export function withoutChartOnly(refs: readonly string[], chartKey: string | null): string[] {
  return refs.length && refs.every(ref => isSizeChartRef(ref, chartKey)) ? [] : [...refs]
}

// The row icon's count and hint: the outgoing list (the chart last, counted once) said as "N görsel + beden tablosu".
export function variantMediaHint(outgoing: { refs: readonly string[]; assigned: boolean } | undefined, chartKey: string | null): { count: number; hint: string } {
  const refs = outgoing?.refs ?? []
  const withChart = refs.some(ref => isSizeChartRef(ref, chartKey))
  const pictures = refs.length - (withChart ? 1 : 0)
  const said = withChart ? `${pictures} görsel + beden tablosu` : `${pictures} görsel`
  const hint = outgoing?.assigned ? `Bu varyanta atanan ${said} ile gider (beden tablosu sonda, otomatik)`
    : refs.length ? `Atama yok: kartın galerisinden ${said} ile gider (diğer renklere atananlar hariç${withChart ? '; beden tablosu sonda, otomatik' : ''})`
      : 'Atama yok, gidecek görsel yok'
  return { count: refs.length, hint }
}

export function MediaOptionThumb({ option, selected, automatic = false, onClick }: { option: ProductMediaOption; selected: boolean; automatic?: boolean; onClick: () => void }) {
  const [src, setSrc] = useState(option.url ?? '')
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    if (!option.file) return
    const objectUrl = URL.createObjectURL(option.file)
    setSrc(objectUrl)
    setFailed(false)
    return () => URL.revokeObjectURL(objectUrl)
  }, [option.file])
  const ticked = automatic || selected
  const label = automatic ? `${option.label} · Beden Tablosu` : option.label
  return <button type="button" className={`variant-media-option ${ticked ? 'selected' : ''}${automatic ? ' is-automatic' : ''}`}
    aria-label={automatic ? `${label}: ${SIZE_CHART_AUTOMATIC_NOTE}` : `${option.label}${selected ? ' seçimini kaldır' : ''}`}
    aria-pressed={ticked} aria-disabled={automatic || undefined} title={automatic ? SIZE_CHART_AUTOMATIC_HINT : undefined}
    onClick={automatic ? undefined : onClick}>
    <span className="variant-media-option-image">{src && !failed ? <img src={src} alt="" onError={() => setFailed(true)} /> : <VariantImageIcon />}</span>
    <span className="variant-media-option-label" title={label}>{label}</span>
    {automatic && <span className="variant-media-option-note">{SIZE_CHART_AUTOMATIC_NOTE}</span>}
    <i aria-hidden="true">{ticked ? <UiIcon name="check" /> : null}</i>
  </button>
}
