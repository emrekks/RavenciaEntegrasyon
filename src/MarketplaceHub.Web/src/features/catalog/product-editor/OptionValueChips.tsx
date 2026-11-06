import { cleanOptionValue, optionValueSortRank } from '../shared'

type OptionValue = { id: string; value: string }

// A-Z in Turkish collation, numbers in natural order, sizes in size order (XS S M L XL 2XL, numeric sizes ascending).
export function sortOptionValues<T extends { id: string; value: string }>(attributeName: string, values: T[]) {
  return values
    .map((value, index) => ({ value, index, rank: optionValueSortRank(attributeName, value.value) }))
    .sort((left, right) => left.rank.bucket - right.rank.bucket
      || left.rank.primary - right.rank.primary
      || left.rank.secondary - right.rank.secondary
      || left.rank.text.localeCompare(right.rank.text, 'tr', { numeric: true, sensitivity: 'base' })
      || left.index - right.index)
    .map(item => item.value)
}

// Chips share one size; a longer value (Standart, Kahverengi) takes two or three cells of the grid.
/**
 * The value picker of one option group: chosen values on top as removable chips, the rest below in a scrolling list.
 * `values` arrive already sorted (A-Z, sizes in size order); both areas keep that order, so an unselected chip returns to its place.
 */
export function OptionValueChips({ groupName, values, selectedIds, filter, onToggle }: {
  groupName: string
  values: OptionValue[]
  selectedIds: string[]
  filter: (values: OptionValue[]) => OptionValue[]
  onToggle: (valueId: string) => void
}) {
  const selected = values.filter(value => selectedIds.includes(value.id))
  const available = filter(values.filter(value => !selectedIds.includes(value.id)))
  return (
    <>
      {selected.length > 0 && (
        <div className="option-chip-selected" role="group" aria-label={`Seçili ${groupName} değerleri`}>
          {selected.map(value => {
            const label = cleanOptionValue(value.value)
            return (
              <button type="button" key={value.id} title={`${label} seçimini kaldır`} aria-pressed="true" className="option-chip option-chip-chosen active" onClick={() => onToggle(value.id)}>
                <span className="option-chip-label">{label}</span><span className="option-chip-remove" aria-hidden="true">×</span>
              </button>
            )
          })}
        </div>
      )}
      <div className="option-chip-list" role="group" aria-label={`${groupName} değerleri`}>
        {available.map(value => {
          const label = cleanOptionValue(value.value)
          return (
            <button type="button" key={value.id} title={label} aria-pressed="false" className="option-chip" onClick={() => onToggle(value.id)}>
              {label}
            </button>
          )
        })}
      </div>
    </>
  )
}
