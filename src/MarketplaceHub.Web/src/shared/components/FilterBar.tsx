import { useState, useRef, useEffect, type ReactNode } from 'react';
import { UiIcon } from './UiIcon';

interface FilterOption {
  value: string;
  label: string;
}

interface FilterDropdown {
  key: string;
  label: string;
  options: FilterOption[];
  value: string;
  onChange: (value: string) => void;
}

interface FilterBarProps {
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  filters?: FilterDropdown[];
  actions?: ReactNode;
  className?: string;
}

export function FilterBar({
  searchValue,
  onSearchChange,
  searchPlaceholder = 'Ara...',
  filters = [],
  actions,
  className = ''
}: FilterBarProps) {
  const [localSearch, setLocalSearch] = useState(searchValue ?? '');
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    setLocalSearch(searchValue ?? '');
  }, [searchValue]);

  const handleSearch = (val: string) => {
    setLocalSearch(val);
    if (onSearchChange) {
      clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => onSearchChange(val), 250);
    }
  };

  return (
    <div className={`rv-filter-bar ${className}`}>
      {onSearchChange && (
        <div className="rv-filter-bar__search">
          <span className="rv-input-icon"><UiIcon name="search" /></span>
          <input
            type="text"
            className="rv-input rv-input--has-icon"
            placeholder={searchPlaceholder}
            value={localSearch}
            onChange={(e) => handleSearch(e.target.value)}
          />
        </div>
      )}

      {filters.map((f) => (
        <select
          key={f.key}
          className="rv-select rv-select--pill"
          value={f.value}
          onChange={(e) => f.onChange(e.target.value)}
          aria-label={f.label}
        >
          <option value="">{f.label}</option>
          {f.options.map((opt) => (
            <option key={opt.value} value={opt.value}>{opt.label}</option>
          ))}
        </select>
      ))}

      {actions && <div className="rv-filter-bar__actions">{actions}</div>}
    </div>
  );
}
