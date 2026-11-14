import { platformLogoSource } from '../../../shared/platform-logos'

// One platform tab of the Eşleştirmeler strips. A platform without an active connection stays on the strip, greyed out:
// not selectable, never selected, with the reason in its tooltip.
export function MappingPlatformTab({ code, label, available, selected, activeTitle, onSelect }: { code: string; label: string; available: boolean; selected: boolean; activeTitle: string; onSelect: () => void }) {
  const logo = platformLogoSource(code)
  const isSelected = available && selected
  return (
    <button type="button" role="tab" aria-selected={isSelected} aria-disabled={available ? undefined : true} className={`${isSelected ? 'active is-active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? activeTitle : 'Bağlantı etkin değil'} onClick={available ? onSelect : undefined}>
      <span className="rv-tab-label">{logo && <img className="rv-view-tab-logo" src={logo} alt="" aria-hidden="true" />}{label}</span>
    </button>
  )
}
