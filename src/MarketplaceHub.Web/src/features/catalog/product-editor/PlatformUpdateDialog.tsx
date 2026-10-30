import { createPortal } from 'react-dom'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { bulkPublishPlatformName } from '../products/bulk-publish-status'
import type { MarketplaceConnection } from '../shared'

export type PlatformUpdateTarget = { connection: MarketplaceConnection; capabilities: { writable: boolean; content: boolean } }

export function isPlatformUpdateWritable(target: PlatformUpdateTarget) {
  return target.capabilities.writable && target.connection.status.trim().toUpperCase() === 'ACTIVE'
}

// The editor's "Platformlarda Güncelle": the same cards, switches and bottom bar as "Platformlarda Yayınla", so a product
// is sent to its marketplaces through one familiar popup. Each card says what goes to that store: price and stock, plus
// the product information when the switch is on and the marketplace takes it.
export function PlatformUpdateDialog({ targets, selectedIds, onToggle, productInformation, onProductInformationChange, submitting, onClose, onConfirm }: {
  targets: PlatformUpdateTarget[]
  selectedIds: string[]
  onToggle: (connectionId: string) => void
  productInformation: boolean
  onProductInformationChange: (value: boolean) => void
  submitting: boolean
  onClose: () => void
  onConfirm: () => void
}) {
  const selected = targets.filter(target => isPlatformUpdateWritable(target) && selectedIds.includes(target.connection.id))
  const contentAvailable = selected.some(target => target.capabilities.content)
  const sendsContent = productInformation && contentAvailable
  const close = () => { if (!submitting) onClose() }

  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}>
      <DialogSurface as="section" onClose={close} className="workspace-modal product-publish-popup-modal platform-update-modal" aria-labelledby="platform-update-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Ürün Güncelleme</p><h2 id="platform-update-title">Platformlarda Güncelle</h2><p>Değişiklikler önce panelde kaydedilir, sonra seçtiğiniz her mağazaya ayrı bir güncelleme işi gönderilir.</p></div><button type="button" className="modal-close" onClick={close} disabled={submitting} aria-label="Platform güncelleme penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><div className="product-add-page product-publish-popup"><section className="product-publish-step" aria-label="Ürünü platformlarda güncelle"><div className="product-publish-layout"><div className="product-publish-main">
          <label className={`platform-update-content-switch marketplace-pricing-switch${contentAvailable ? '' : ' is-disabled'}`}>
            <span><strong>Ürün Bilgilerini De Güncelle</strong><small>{contentAvailable
              ? 'Ad, açıklama, kategori özellikleri, desi ve teslimat bilgileri ile görseller, platformun kabul ettiği alanlarla gönderilir. Kapalıyken yalnız fiyat ve stok gider.'
              : 'Seçili platformlar yalnız fiyat ve stok güncellemesi alıyor.'}</small></span>
            <input type="checkbox" role="switch" checked={sendsContent} disabled={!contentAvailable || submitting} onChange={event => onProductInformationChange(event.target.checked)} aria-label="Ürün bilgilerini de güncelle" />
          </label>
          {targets.length
            ? <div className="publish-platform-grid" role="group" aria-label="Güncellenecek platformlar">{targets.map(target => <PlatformUpdateCard key={target.connection.id} target={target} selected={selectedIds.includes(target.connection.id)} productInformation={productInformation} locked={submitting} onToggle={() => onToggle(target.connection.id)} />)}</div>
            : <div className="publish-connections-empty"><strong>Güncellenebilir Bağlantı Yok</strong><p>Ürün güncellemek için Platformlar sayfasından yazma yetkisi olan aktif bir bağlantı ekleyin.</p></div>}
          <div className="publish-selection-footer">
            <div><strong>{selected.length ? `${selected.length} platform seçildi` : 'Güncellenecek platform seçilmedi'}</strong><small>{!selected.length ? 'Güncellenecek mağazaları kartların üstünden seçin.' : sendsContent ? 'Ürün bilgileri, fiyat ve stok gönderilecek.' : 'Fiyat ve stok gönderilecek.'}</small></div>
            {!selected.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            <div className="platform-update-actions">
              <button type="button" className="secondary" onClick={close} disabled={submitting}>Vazgeç</button>
              <button type="button" onClick={onConfirm} disabled={submitting || !selected.length}>{submitting ? 'Güncelleniyor…' : 'Güncelle'}</button>
            </div>
          </div>
        </div></div></section></div></div>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

function PlatformUpdateCard({ target, selected, productInformation, locked, onToggle }: {
  target: PlatformUpdateTarget
  selected: boolean
  productInformation: boolean
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
  const sends = !writable ? '—' : productInformation && capabilities.content ? 'Ürün bilgileri, fiyat, stok' : 'Fiyat, stok'
  return <article className={`publish-platform-card${on ? ' selected' : ''}${writable ? '' : ' is-readonly'}`}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={on} disabled={locked || !writable}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${on ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div className="platform-update-sends"><dt>Gönderilecek</dt><dd>{sends}</dd></div>
    </dl>
  </article>
}
