import { UiIcon } from '../../../shared/components'
import { optionValueToAdd, type AttributeOptionValue } from '../attribute-value-search'

// Under an option group's search box (Ürün Seçenekleri → Renk / Beden): when no value of the group is the typed one,
// the typed value can be added to the group and selected. It is a panel value only; no marketplace value is mapped to it.
export function OptionValueAddButton({ attributeName, values, query, adding, onAdd }: {
  attributeName: string
  values: readonly AttributeOptionValue[]
  query: string
  adding: boolean
  onAdd: (value: string) => void
}) {
  const value = optionValueToAdd(attributeName, values, query)
  if (!value) return null
  return <div className="option-value-add">
    <button type="button" className="secondary option-value-add-button" disabled={adding} onClick={() => onAdd(value)} title={`“${value}” değerini ${attributeName} seçeneklerine ekler ve seçer; pazaryeri değeriyle eşleştirmeyi Seçenek Eşitleme ekranından siz yaparsınız.`}>
      <UiIcon name={adding ? 'loader' : 'plus'} size={16} /> {adding ? 'Ekleniyor…' : `“${value}” Değerini Ekle`}
    </button>
    <small>Yeni değer hiçbir pazaryeri değerine otomatik eşlenmez.</small>
  </div>
}
