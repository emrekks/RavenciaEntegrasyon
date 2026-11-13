import { useEffect, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { DialogSurface, TitleHint, UiIcon } from '../../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { bulkPublishPlatformName } from '../products/bulk-publish-status'
import type { MarketplaceConnection } from '../shared'
import { availablePlatformUpdateFields, platformUpdateFieldLabel, platformUpdateFieldsForStores, platformUpdateGroups, platformUpdatePlan, platformUpdateSendsPriceOrStock, platformUpdateSendsWholeCard, platformUpdateSummary, type PlatformUpdateField, type PlatformUpdateGroup, type PlatformUpdateGroupKey } from './platform-update-fields'

// blockReason: why the caller holds the store back (the product list: none of the selected products is on it); detail:
// a line the card shows under its facts (the product list: how many of the selected products are on it);
// manualWritesEnabled: PttAVM's "Manuel Dış Yazmalar" switch (undefined: not read, or not PttAVM).
export type PlatformUpdateTarget = { connection: MarketplaceConnection; capabilities: { writable: boolean; content: boolean }; blockReason?: string; detail?: string; manualWritesEnabled?: boolean }

// Where the write switches are turned on: Entegrasyonlar › the store › the "Pazaryerine Yazma" tab.
const writeSwitchPlace = 'Entegrasyonlar › mağaza › Pazaryerine Yazma’dan açılır'

// A store the window can pick: it can be updated at all. Its write switches may still be closed: then Kaydet keeps it in
// the choice, and Gönder waits until they are open.
export function isPlatformUpdateSelectable(target: PlatformUpdateTarget) {
  return target.capabilities.writable && target.connection.status.trim().toUpperCase() === 'ACTIVE' && !target.blockReason
}

// An update from here is a manual write: the connection's Dış Yazma Ana Anahtarı (externalWritesEnabled) must be open,
// and on PttAVM its Manuel Dış Yazmalar too; the automatic price/stock switch ("Otomatik Dış Yazmalar") does not apply.
// The server's own switch is not shown to the panel; the send names it when it is closed. '' when nothing known is closed.
export function platformUpdateWriteClosed(target: PlatformUpdateTarget) {
  if (target.connection.externalWritesEnabled === false) return `Dış Yazma Ana Anahtarı kapalı · ${writeSwitchPlace}`
  if (target.connection.platformCode.trim().toUpperCase() === 'PTTAVM' && target.manualWritesEnabled === false) return `Manuel Dış Yazmalar kapalı · ${writeSwitchPlace}`
  return ''
}

export function isPlatformUpdateWritable(target: PlatformUpdateTarget) {
  return isPlatformUpdateSelectable(target) && !platformUpdateWriteClosed(target)
}

// Why a store cannot be updated, naming the switch and where it is turned on; '' when it can.
export function platformUpdateBlockReason(target: PlatformUpdateTarget) {
  if (!target.capabilities.writable) return 'Salt okunur, güncellenemez'
  if (target.connection.status.trim().toUpperCase() !== 'ACTIVE') return 'Bağlantı aktif değil'
  return platformUpdateWriteClosed(target) || (target.blockReason ?? '')
}

// Said where the user starts a send of price or stock by hand (owner, 12 Nov 2026): such a send goes although the
// automatic price/stock writes are off.
export const manualPriceStockNote = 'Elle gönderim: Otomatik Dış Yazmalar kapalı olsa da seçili fiyat ve stok pazaryerine gider.'

type ShownGroup = Omit<PlatformUpdateGroup, 'fields'> & { fields: { key: PlatformUpdateField; label: string }[] }

// The editor's "Platformlarda Güncelle" settings (the button right of Hızlı Güncelle): the same cards as "Platformlarda
// Yayınla", with the fields to send in collapsible groups (Ürün Bilgileri, Kargo ve Teslimat, Satış Bilgileri). Only
// fields a selected store can take are shown; each card lists what really goes there, including what its marketplace
// takes only together with a picked field. It sends nothing: Kaydet keeps the choice, and Hızlı Güncelle sends it
// (owner, 9 Nov 2026). The bar is the window's own footer, so the cards scroll above it instead of under it.
// The product list's "Platformlara Güncelle" opens the same window for several products (BulkPlatformUpdateDialog):
// with its own eyebrow and intro, the selected products above the fields, no Kaydet, and Gönder held while
// `sendBlocked` names a reason.
export function PlatformUpdateDialog({ targets, selectedIds, onSelectedIdsChange, fields, onFieldsChange, openGroups, onOpenGroupsChange, submitting, familyReach, onClose, onSave, onSend, eyebrow = 'Hızlı Güncelle Ayarı', intro, sendBlocked, children }: {
  targets: PlatformUpdateTarget[]
  selectedIds: string[]
  onSelectedIdsChange: (connectionIds: string[]) => void
  fields: PlatformUpdateField[]
  onFieldsChange: (fields: PlatformUpdateField[]) => void
  openGroups: PlatformUpdateGroupKey[]
  onOpenGroupsChange: (groups: PlatformUpdateGroupKey[]) => void
  submitting: boolean
  // A model family: which colours (and variants) the product information update reaches on one store.
  familyReach?: (connection: MarketplaceConnection) => string
  onClose: () => void
  // Keeps the choice for Hızlı Güncelle: the picked fields that are shown (a field no selected store takes never goes).
  // Without it the window has no Kaydet (the product list sends at once).
  onSave?: (fields: PlatformUpdateField[]) => void
  // Keeps the same choice, then sends it the way Hızlı Güncelle does (the parent closes the window).
  onSend: (fields: PlatformUpdateField[]) => void
  eyebrow?: string
  intro?: ReactNode
  // Why Gönder waits (the products are still being checked, none of them can go); empty when it can go.
  sendBlocked?: string
  // Shown above the fields (the product list: the selected products and their state).
  children?: ReactNode
}) {
  const selectable = targets.filter(isPlatformUpdateSelectable)
  const selected = selectable.filter(target => selectedIds.includes(target.connection.id))
  // With no store picked yet, the fields of every store that can be updated are shown.
  const availableFor = (stores: PlatformUpdateTarget[]) => availablePlatformUpdateFields((stores.length ? stores : selectable).map(target => target.connection.platformCode))
  const available = availableFor(selected)
  const visibleFields = fields.filter(field => available.includes(field))
  const plans = selected.map(target => platformUpdatePlan(target.connection.platformCode, visibleFields))
  const sendsAnything = plans.some(plan => plan.sends.length > 0)
  // Picked stores whose write switch is closed: Kaydet keeps them, Gönder waits (said before it is pressed, not after).
  const writeClosed = selected.filter(target => platformUpdateWriteClosed(target))
  const writeClosedNote = writeClosed.length ? `${writeClosed.map(target => target.connection.displayName || bulkPublishPlatformName(target.connection.platformCode)).join(', ')} için pazaryerine yazma kapalı; Gönder bekler${onSave ? ', Kaydet seçimi saklar' : ''}.` : ''
  const sendsPriceOrStock = selected.some(target => platformUpdateSendsPriceOrStock(target.connection.platformCode, visibleFields))
  const close = () => { if (!submitting) onClose() }
  const setField = (field: PlatformUpdateField, on: boolean) => onFieldsChange(on ? [...new Set([...fields, field])] : fields.filter(item => item !== field))
  const setGroup = (groupFields: PlatformUpdateField[], on: boolean) => onFieldsChange(on ? [...new Set([...fields, ...groupFields])] : fields.filter(item => !groupFields.includes(item)))
  const toggleOpen = (key: PlatformUpdateGroupKey) => onOpenGroupsChange(openGroups.includes(key) ? openGroups.filter(item => item !== key) : [...openGroups, key])
  // A store in or out changes the fields shown: one no longer shown is dropped, one shown only now comes in ticked.
  const selectStores = (nextIds: string[]) => {
    const next = platformUpdateFieldsForStores(fields, available, availableFor(selectable.filter(target => nextIds.includes(target.connection.id))))
    if (next.length !== fields.length || next.some(field => !fields.includes(field))) onFieldsChange(next)
    onSelectedIdsChange(nextIds)
  }
  const toggleStore = (connectionId: string) => selectStores(selectedIds.includes(connectionId) ? selectedIds.filter(id => id !== connectionId) : [...selectedIds, connectionId])
  // The bulk picks: a store that cannot be updated is never picked, and a linked field follows the field it rides with.
  const selectableIds = selectable.map(target => target.connection.id)
  const allStoresPicked = selectable.length > 0 && selected.length === selectable.length
  const allFieldsPicked = available.every(field => fields.includes(field))
  const groups: ShownGroup[] = platformUpdateGroups
    .map(group => ({
      ...group,
      fields: group.fields.filter(field => available.includes(field.key)),
      linked: group.linked?.filter(item => available.includes(item.with))
    }))
    .filter(group => group.fields.length > 0)

  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}>
      <DialogSurface as="section" onClose={close} className="workspace-modal product-publish-popup-modal platform-update-modal" aria-labelledby="platform-update-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">{eyebrow}</p><h2 id="platform-update-title">Platformlarda Güncelle</h2>{intro && <p>{intro}</p>}</div><button type="button" className="modal-close" onClick={close} disabled={submitting} aria-label="Platform güncelleme penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><div className="product-add-page product-publish-popup"><section className="product-publish-step" aria-label="Ürünü platformlarda güncelle"><div className="product-publish-layout"><div className="product-publish-main">
          {children}
          {groups.length > 0 && <section className="platform-update-section" aria-labelledby="platform-update-fields-title">
            <PlatformUpdateSectionHead id="platform-update-fields-title" title="Gönderilecek Bilgiler" hint={`${visibleFields.length}/${available.length} bilgi seçili`} help="Seçtiğiniz bilgiler her mağazaya, o mağazanın aldığı kadarıyla gönderilir. Shopify seçime bakmaz: tüm model kartını tek istekte alır.">
              <button type="button" disabled={submitting || allFieldsPicked} onClick={() => setGroup(available, true)}>Tümünü Seç</button>
              <button type="button" disabled={submitting || !visibleFields.length} onClick={() => setGroup(available, false)}>Temizle</button>
            </PlatformUpdateSectionHead>
            <div className="platform-update-field-groups" role="group" aria-label="Gönderilecek bilgiler">
              {groups.map(group => <PlatformUpdateFieldGroup key={group.key} group={group} picked={fields} open={openGroups.includes(group.key)} locked={submitting} onToggleOpen={() => toggleOpen(group.key)} onGroupChange={on => setGroup(group.fields.map(field => field.key), on)} onFieldChange={setField} />)}
            </div>
            {sendsPriceOrStock && <p className="platform-update-manual-note" role="note"><UiIcon name="info" size={14} /><span>{manualPriceStockNote}</span></p>}
          </section>}
          <section className="platform-update-section" aria-labelledby="platform-update-stores-title">
            <PlatformUpdateSectionHead id="platform-update-stores-title" title="Platformlar" hint={`${selected.length}/${selectable.length} mağaza seçili${selectable.length < targets.length ? ` · ${targets.length - selectable.length} mağaza güncellenemez` : ''}`}>
              {selectable.length > 0 && <>
                <button type="button" disabled={submitting || allStoresPicked} onClick={() => selectStores(selectableIds)}>Tümünü Seç</button>
                <button type="button" disabled={submitting || !selected.length} onClick={() => selectStores([])}>Hiçbirini Seçme</button>
              </>}
            </PlatformUpdateSectionHead>
            {targets.length
              ? <div className="publish-platform-grid" role="group" aria-label="Güncellenecek platformlar">{targets.map(target => <PlatformUpdateCard key={target.connection.id} target={target} selected={selectedIds.includes(target.connection.id)} fields={visibleFields} locked={submitting} familyReach={familyReach} onToggle={() => toggleStore(target.connection.id)} />)}</div>
              : <div className="publish-connections-empty"><strong>Güncellenebilir Bağlantı Yok</strong><p>Ürün güncellemek için Entegrasyonlar sayfasından yazma yetkisi olan aktif bir bağlantı ekleyin.</p></div>}
          </section>
        </div></div></section></div></div>
        <footer className="publish-selection-footer platform-update-footer">
          <div><strong>{selected.length ? `${selected.length} platform seçildi` : 'Güncellenecek platform seçilmedi'}</strong><small>{!selected.length ? 'Güncellenecek mağazaları kartların üstünden seçin.' : !sendsAnything ? 'Seçili mağazalara gidecek bir bilgi seçilmedi.' : sendBlocked || writeClosedNote || (onSave ? 'Kaydet seçimi saklar; Gönder saklayıp hemen gönderir.' : 'Gönder seçili bilgileri seçili mağazalara hemen gönderir.')}</small></div>
          {!selected.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
          {selected.length > 0 && !sendsAnything && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir bilgi seçin</span>}
          {sendsAnything && writeClosed.length > 0 && <span className="publish-selection-warning"><UiIcon name="alert" /> Yazma Kapalı</span>}
          <div className="platform-update-actions">
            <button type="button" className="secondary" onClick={close} disabled={submitting}>Vazgeç</button>
            {onSave && <button type="button" className="secondary" onClick={() => onSave(visibleFields)} disabled={submitting || !selected.length || !sendsAnything}>Kaydet</button>}
            <button type="button" onClick={() => onSend(visibleFields)} disabled={submitting || !selected.length || !sendsAnything || Boolean(sendBlocked) || writeClosed.length > 0} title={writeClosedNote || undefined}>{submitting ? 'Gönderiliyor…' : 'Gönder'}</button>
          </div>
        </footer>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

// A section's title (with its explanation behind a "?") and its selection count, with its bulk picks on the right; the line above it parts it from the section before.
function PlatformUpdateSectionHead({ id, title, hint, help, children }: { id: string; title: string; hint: string; help?: ReactNode; children?: ReactNode }) {
  return <header className="platform-update-section-head">
    <div><h3 id={id}>{title}{help ? <TitleHint label={title}>{help}</TitleHint> : null}</h3><small>{hint}</small></div>
    <div className="platform-update-bulk">{children}</div>
  </header>
}

function PlatformUpdateFieldGroup({ group, picked, open, locked, onToggleOpen, onGroupChange, onFieldChange }: {
  group: ShownGroup
  picked: PlatformUpdateField[]
  open: boolean
  locked: boolean
  onToggleOpen: () => void
  onGroupChange: (on: boolean) => void
  onFieldChange: (field: PlatformUpdateField, on: boolean) => void
}) {
  const { key, label, fields, linked = [] } = group
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
      <div className="platform-update-field-list">
        {fields.map(field => <label key={field.key} className="platform-update-field"><input type="checkbox" checked={picked.includes(field.key)} disabled={locked} onChange={event => onFieldChange(field.key, event.target.checked)} /><span>{field.label}</span></label>)}
        {/* Not a pick of its own: it goes whenever the field it rides with goes, so its box follows that field. */}
        {linked.map(item => <label key={item.label} className="platform-update-field is-linked"><input type="checkbox" checked={picked.includes(item.with)} disabled /><span>{item.label}<small>{platformUpdateFieldLabel(item.with)} ile</small></span></label>)}
      </div>
    </div>}
  </section>
}

function PlatformUpdateCard({ target, selected, fields, locked, familyReach, onToggle }: {
  target: PlatformUpdateTarget
  selected: boolean
  fields: PlatformUpdateField[]
  locked: boolean
  familyReach?: (connection: MarketplaceConnection) => string
  onToggle: () => void
}) {
  const { connection } = target
  // A store whose write switch is closed can still be picked (Kaydet keeps it); its card says which switch and where.
  const writable = isPlatformUpdateSelectable(target)
  const closed = writable && Boolean(platformUpdateWriteClosed(target))
  // Shopify takes the whole model card in one request: the pick does not narrow what goes there (9.3.10).
  const wholeCard = writable && platformUpdateSendsWholeCard(connection.platformCode)
  const logo = platformLogoSource(connection.platformCode)
  const platformName = bulkPublishPlatformName(connection.platformCode)
  const storeName = connection.displayName || connection.externalStoreId || platformName
  const on = selected && writable
  const subtitle = platformUpdateBlockReason(target) || (on ? 'Güncelleme için seçildi' : 'Güncelleme için seçilmedi')
  const plan = platformUpdatePlan(connection.platformCode, fields)
  const sends = !writable ? '—' : platformUpdateSummary(plan)
  // The product information goes for every colour of the model family on this store (family-update-plan.ts); said
  // here before the send, with the colours the store does not hold.
  const reach = writable && plan.productFields.length && familyReach ? familyReach(connection) : ''
  // What goes, as small chips: the dialog's fields first, then (dashed) what the requests also carry, last (struck
  // through) the picked fields this marketplace does not take.
  type Chip = { label: string; kind?: 'extra' | 'skipped' }
  const chips: Chip[] = writable && plan.sends.length ? [...plan.sends.map(label => ({ label })), ...plan.extras.map(label => ({ label, kind: 'extra' as const })), ...plan.skipped.map(field => ({ label: platformUpdateFieldLabel(field), kind: 'skipped' as const }))] : []
  return <article className={`publish-platform-card${on ? ' selected' : ''}${writable ? '' : ' is-readonly'}${closed ? ' is-write-closed' : ''}`}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={on} disabled={locked || !writable}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${on ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div className="platform-update-sends"><dt>Gönderilecek</dt><dd title={sends}>{chips.length ? <ul className="platform-update-chips">{chips.map(chip => <li key={`${chip.kind}-${chip.label}`} className={chip.kind ? `is-${chip.kind}` : undefined} title={chip.kind === 'extra' ? 'Seçim değil, aynı istekte birlikte gider' : undefined}>{chip.kind === 'skipped' ? <s>{chip.label}</s> : chip.label}</li>)}</ul> : writable ? sends : '—'}</dd>
        {wholeCard && <small className="platform-update-whole-card">Shopify seçime bakmaz: hangi bilgi seçilirse seçilsin tüm model kartı (fiyat ve stok dahil) tek istekte gider.</small>}
        {writable && !wholeCard && plan.bundled.length > 0 && <small>Seçilmedi ama aynı istekte birlikte gider: {plan.bundled.map(platformUpdateFieldLabel).join(', ')}.</small>}
      </div>
      {reach && <div className="platform-update-sends platform-update-family-reach"><dt>Model Ailesi</dt><dd>{reach}</dd></div>}
      {target.detail && <div className="platform-update-sends"><dt>Yayındaki Ürünler</dt><dd>{target.detail}</dd></div>}
    </dl>
  </article>
}
