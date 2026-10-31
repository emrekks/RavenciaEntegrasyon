import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { bulkPublishPlatformName } from '../products/bulk-publish-status'
import type { MarketplaceConnection } from '../shared'
import { availablePlatformUpdateFields, platformUpdateFieldLabel, platformUpdateFixedInformation, platformUpdateGroups, platformUpdateNote, platformUpdatePlan, platformUpdateSummary, type PlatformUpdateField, type PlatformUpdateGroup, type PlatformUpdateGroupKey } from './platform-update-fields'

export type PlatformUpdateTarget = { connection: MarketplaceConnection; capabilities: { writable: boolean; content: boolean } }

export function isPlatformUpdateWritable(target: PlatformUpdateTarget) {
  return target.capabilities.writable && target.connection.status.trim().toUpperCase() === 'ACTIVE'
}

// The editor's "Platformlarda Güncelle": the same cards and bottom bar as "Platformlarda Yayınla", with the fields to send
// in collapsible groups (Ürün Bilgileri, Kargo ve Teslimat, Satış Bilgileri). Only fields a selected store can take are
// shown; each card lists what really goes there, including what its marketplace takes only together with a picked field.
// The bar is the window's own footer, so the cards scroll above it instead of under it.
export function PlatformUpdateDialog({ targets, selectedIds, onToggle, fields, onFieldsChange, openGroups, onOpenGroupsChange, submitting, onClose, onConfirm }: {
  targets: PlatformUpdateTarget[]
  selectedIds: string[]
  onToggle: (connectionId: string) => void
  fields: PlatformUpdateField[]
  onFieldsChange: (fields: PlatformUpdateField[]) => void
  openGroups: PlatformUpdateGroupKey[]
  onOpenGroupsChange: (groups: PlatformUpdateGroupKey[]) => void
  submitting: boolean
  onClose: () => void
  onConfirm: () => void
}) {
  const writable = targets.filter(isPlatformUpdateWritable)
  const selected = writable.filter(target => selectedIds.includes(target.connection.id))
  // With no store picked yet, the fields of every store that can be updated are shown.
  const available = availablePlatformUpdateFields((selected.length ? selected : writable).map(target => target.connection.platformCode))
  const plans = selected.map(target => platformUpdatePlan(target.connection.platformCode, fields))
  const sendsAnything = plans.some(plan => plan.sends.length > 0)
  const close = () => { if (!submitting) onClose() }
  const setField = (field: PlatformUpdateField, on: boolean) => onFieldsChange(on ? [...new Set([...fields, field])] : fields.filter(item => item !== field))
  const setGroup = (groupFields: PlatformUpdateField[], on: boolean) => onFieldsChange(on ? [...new Set([...fields, ...groupFields])] : fields.filter(item => !groupFields.includes(item)))
  const toggleOpen = (key: PlatformUpdateGroupKey) => onOpenGroupsChange(openGroups.includes(key) ? openGroups.filter(item => item !== key) : [...openGroups, key])
  const groups = platformUpdateGroups
    .map(group => ({ ...group, fields: group.fields.filter(field => available.includes(field.key)), linked: group.linked?.filter(item => available.includes(item.with)) }))
    .filter(group => group.fields.length > 0)

  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}>
      <DialogSurface as="section" onClose={close} className="workspace-modal product-publish-popup-modal platform-update-modal" aria-labelledby="platform-update-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Ürün Güncelleme</p><h2 id="platform-update-title">Platformlarda Güncelle</h2><p>Değişiklikler önce panelde kaydedilir, sonra seçtiğiniz her mağazaya ayrı bir güncelleme işi gönderilir.</p></div><button type="button" className="modal-close" onClick={close} disabled={submitting} aria-label="Platform güncelleme penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><div className="product-add-page product-publish-popup"><section className="product-publish-step" aria-label="Ürünü platformlarda güncelle"><div className="product-publish-layout"><div className="product-publish-main">
          {groups.length > 0 && <div className="platform-update-field-groups" role="group" aria-label="Gönderilecek bilgiler">
            {groups.map(group => <PlatformUpdateFieldGroup key={group.key} group={group} picked={fields} open={openGroups.includes(group.key)} locked={submitting} onToggleOpen={() => toggleOpen(group.key)} onGroupChange={on => setGroup(group.fields.map(field => field.key), on)} onFieldChange={setField} />)}
          </div>}
          {groups.length > 0 && <details className="platform-update-fixed">
            <summary>Değişmeyen ve Gönderilmeyen Bilgiler</summary>
            <dl>{platformUpdateFixedInformation.map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.note}</dd></div>)}</dl>
          </details>}
          {targets.length
            ? <div className="publish-platform-grid" role="group" aria-label="Güncellenecek platformlar">{targets.map(target => <PlatformUpdateCard key={target.connection.id} target={target} selected={selectedIds.includes(target.connection.id)} fields={fields} locked={submitting} onToggle={() => onToggle(target.connection.id)} />)}</div>
            : <div className="publish-connections-empty"><strong>Güncellenebilir Bağlantı Yok</strong><p>Ürün güncellemek için Platformlar sayfasından yazma yetkisi olan aktif bir bağlantı ekleyin.</p></div>}
        </div></div></section></div></div>
        <footer className="publish-selection-footer platform-update-footer">
          <div><strong>{selected.length ? `${selected.length} platform seçildi` : 'Güncellenecek platform seçilmedi'}</strong><small>{!selected.length ? 'Güncellenecek mağazaları kartların üstünden seçin.' : sendsAnything ? 'Her mağazaya kartında yazan bilgiler gönderilecek.' : 'Seçili mağazalara gidecek bir bilgi seçilmedi.'}</small></div>
          {!selected.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
          {selected.length > 0 && !sendsAnything && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir bilgi seçin</span>}
          <div className="platform-update-actions">
            <button type="button" className="secondary" onClick={close} disabled={submitting}>Vazgeç</button>
            <button type="button" onClick={onConfirm} disabled={submitting || !selected.length || !sendsAnything}>{submitting ? 'Güncelleniyor…' : 'Güncelle'}</button>
          </div>
        </footer>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

function PlatformUpdateFieldGroup({ group, picked, open, locked, onToggleOpen, onGroupChange, onFieldChange }: {
  group: PlatformUpdateGroup
  picked: PlatformUpdateField[]
  open: boolean
  locked: boolean
  onToggleOpen: () => void
  onGroupChange: (on: boolean) => void
  onFieldChange: (field: PlatformUpdateField, on: boolean) => void
}) {
  const { key, label, description, fields, linked = [], hint } = group
  const count = fields.filter(field => picked.includes(field.key)).length
  const all = count === fields.length
  const groupInput = useRef<HTMLInputElement>(null)
  useEffect(() => { if (groupInput.current) groupInput.current.indeterminate = count > 0 && !all }, [count, all])
  const bodyId = `platform-update-group-${key}`
  return <section className={`platform-update-field-group${open ? ' is-open' : ''}`}>
    <header>
      <label className="platform-update-group-check">
        <input ref={groupInput} type="checkbox" checked={all} disabled={locked} onChange={event => onGroupChange(event.target.checked)} aria-label={`${label} alanlarının tümü`} />
        <span><strong>{label}</strong><small>{count}/{fields.length} seçili</small></span>
      </label>
      <button type="button" className="platform-update-group-toggle" onClick={onToggleOpen} aria-expanded={open} aria-controls={bodyId} aria-label={`${label} alanlarını ${open ? 'gizle' : 'göster'}`}><UiIcon name="chevronDown" /></button>
    </header>
    {open && <div className="platform-update-group-body" id={bodyId}>
      <p>{description}</p>
      <div className="platform-update-field-list">
        {fields.map(field => <label key={field.key} className="platform-update-field"><input type="checkbox" checked={picked.includes(field.key)} disabled={locked} onChange={event => onFieldChange(field.key, event.target.checked)} /><span>{field.label}</span></label>)}
        {/* Not a pick of its own: it goes whenever the field it rides with goes, so its box follows that field. */}
        {linked.map(item => <label key={item.label} className="platform-update-field is-linked" title={item.note}><input type="checkbox" checked={picked.includes(item.with)} disabled aria-describedby={`${bodyId}-linked`} /><span>{item.label}<small>{platformUpdateFieldLabel(item.with)} ile</small></span></label>)}
      </div>
      {linked.length > 0 && <small className="platform-update-group-hint" id={`${bodyId}-linked`}>{linked.map(item => item.note).join(' ')}</small>}
      {hint && <small className="platform-update-group-hint">{hint}</small>}
    </div>}
  </section>
}

function PlatformUpdateCard({ target, selected, fields, locked, onToggle }: {
  target: PlatformUpdateTarget
  selected: boolean
  fields: PlatformUpdateField[]
  locked: boolean
  onToggle: () => void
}) {
  const { connection, capabilities } = target
  const writable = isPlatformUpdateWritable(target)
  const logo = platformLogoSource(connection.platformCode)
  const platformName = bulkPublishPlatformName(connection.platformCode)
  const storeName = connection.displayName || connection.externalStoreId || platformName
  const on = selected && writable
  const subtitle = !capabilities.writable ? 'Salt okunur, güncellenemez' : !writable ? 'Bağlantı aktif değil' : on ? 'Güncelleme için seçildi' : 'Güncelleme için seçilmedi'
  const plan = platformUpdatePlan(connection.platformCode, fields)
  const summary = !writable ? '—' : platformUpdateSummary(plan)
  const note = writable && plan.productFields.length ? platformUpdateNote(connection.platformCode) : undefined
  return <article className={`publish-platform-card${on ? ' selected' : ''}${writable ? '' : ' is-readonly'}`}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={on} disabled={locked || !writable}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${on ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div className="platform-update-sends"><dt>Gönderilecek</dt>
        <dd aria-label={summary}>{writable && plan.sends.length
          ? <ul className="platform-update-send-list">{plan.sends.map(item => <li key={item}>{item}</li>)}</ul>
          : summary}</dd>
        {writable && plan.bundled.length > 0 && <small>Seçilmedi ama aynı istekte birlikte gider: {plan.bundled.map(platformUpdateFieldLabel).join(', ')}.</small>}
        {note && <small>{note}</small>}
      </div>
    </dl>
  </article>
}
