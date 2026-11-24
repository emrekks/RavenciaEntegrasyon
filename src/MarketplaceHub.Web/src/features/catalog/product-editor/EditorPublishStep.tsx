// The publish step: a card per marketplace connection and Yayınla.
import { Link } from 'react-router'
import { UiIcon, StickyActionBar } from '../../../shared/components'
import { manualPriceStockNote } from './PlatformUpdateDialog'
import { publishCardVariantLinks } from './publish-card-variants'
import { PublishPlatformCardSkeletons, PublishPlatformCard } from './PublishPlatformCard'
import type { EditorContext } from './editor-context'

export function EditorPublishStep({ editor }: { editor: EditorContext }) {
  const { publishOnly, editProductId, setFeedback, submitting, form, variantRows, selectedChannelIds, setSelectedChannelIds, setBlockedPublishCards, setWizardStep, productToEdit, connections, editorHydrating, requirements, leafCategories, publishLinkRows, platformCards, modelCodeCreateBlock, productChecks, publicationAttributeSelections, updateChannel } = editor
  return <>
    <section className="product-publish-step" aria-label={editProductId ? 'Ürün yayınlama ve güncelleme' : 'Ürün yayınlama'}>
      <div className="product-publish-layout">
        <div className="product-publish-main">
          <div className="publish-platform-grid">
            {connections.isPending ? <PublishPlatformCardSkeletons /> : platformCards.length ? platformCards.map(card => <PublishPlatformCard
              card={card}
              key={card.connection.id}
              productId={editProductId}
              familyProductIds={productToEdit.data?.familyProductIds}
              lookupKeys={{ modelCode: form.modelCode, barcode: variantRows[0]?.barcode || form.barcode, sku: variantRows[0]?.sku || form.baseSku }}
              variantLinks={publishCardVariantLinks(publishLinkRows, card.connection.id)}
              categoryId={form.categoryId}
              categoryName={leafCategories.find(item => item.id === form.categoryId)?.name ?? ''}
              selected={selectedChannelIds.includes(card.connection.id)}
              settling={editorHydrating || (Boolean(form.categoryId) && requirements.isLoading)}
              productChecks={productChecks}
              selectedAttributes={publicationAttributeSelections}
              onSelect={() => updateChannel(card.connection.id)}
              onDeselect={() => setSelectedChannelIds(current => current.filter(id => id !== card.connection.id))}
              onBlockedChange={gaps => setBlockedPublishCards(current => (current[card.connection.id] ?? 0) === gaps ? current : { ...current, [card.connection.id]: gaps })}
              onFeedback={setFeedback}
              onEditProductData={publishOnly ? undefined : () => setWizardStep(1)}
            />) : <div className="publish-connections-empty"><strong>Aktif Bağlantı Bulunamadı</strong><p>Yayınlama için önce Entegrasyonlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Entegrasyonları yönet <UiIcon name="arrowRight" /></Link></div>}
          </div>
          <StickyActionBar label="Yayınlama" className="publish-selection-footer" info={{ start: selectedChannelIds.length ? `${selectedChannelIds.length} Platform Seçildi` : <span className="rv-dock-chip is-warn"><UiIcon name="alert" size={14} />En Az Bir Platform Seçin</span> }} lead={<><div><strong>{selectedChannelIds.length ? `${selectedChannelIds.length} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{selectedChannelIds.length ? `Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur. ${manualPriceStockNote}` : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>{!selectedChannelIds.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}</>}>
            <button type="submit" data-dock-primary disabled={submitting || Boolean(modelCodeCreateBlock)} title={modelCodeCreateBlock || undefined}>{submitting ? (editProductId ? 'Yayınlanıyor…' : 'Ürün Oluşturuluyor…') : (editProductId ? 'Yayınla' : <><UiIcon name="sparkle" /> Ürünü Oluştur</>)}</button>
          </StickyActionBar>
        </div>
      </div>
    </section>
  </>
}
