// 2 Fiyat, Stok ve Vergi, with Pazaryerlerine Özel Fiyat.
import { TitleHint, UiIcon } from '../../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { PriceSuggestionLink } from './PriceSuggestionDialog'
import { SizeChartButton } from './SizeChartDialog'
import { TURKISH_VAT_RATES } from '../product-editor-state'
import { channelPricingInputs } from './marketplace-channel-pricing'
import { PanelSelect } from '../../../shared/PanelSelect'
import { EditorFieldError, RequiredLabel } from './EditorBits'
import type { EditorContext } from './editor-context'
import { priceInputNumber } from './card-prices'
import { channelFieldKey, readMoneyField } from './seeded-fields'
import { customChannelFieldIssue, customChannelMissingSale } from './validate-editor'
import { trEntryHelp } from '../../../shared/tr-number'
import { channelFollowMix, priceFieldText } from '../quick-price-edit'
import { formatMoney } from '../../../shared/format-money'

export function EditorPricingSection({ editor }: { editor: EditorContext }) {
  const { editProductId, form, variantRows, setVariantRows, selectedChannelIds, channelPricing, fieldErrors, setFieldErrors, updateField, requiredError, requiredFieldInvalid, variantStockTotal, marketplacePricingOpen, setMarketplacePricingOpen, platformCards, sizeChartSizes, sizeChartDisabledReason, showVariantStockTotal, updateVariantRow, applySizeChart, toggleCustomChannelPrice, updateChannelPrice, channelPriceEdited, channelPriceDraft } = editor
  // A card price saved for a marketplace becomes every variant's price there (a price typed in the variant table wins): said once
  // in the title's hint and, when the card price was really changed, under its row. Text only; the save logic is untouched.
  const manyVariants = variantRows.length > 1
  const channelNote = (connectionId: string, name: string) => {
    if (!manyVariants || !(connectionId in channelPricing) || !channelPriceEdited(connectionId)) return null
    const sale = readMoneyField(channelFieldKey(connectionId, 'salePrice'), channelPriceDraft(connectionId).salePrice)
    if (!(sale > 0)) return null
    const known = new Set(variantRows.flatMap(row => (row.platformStatuses ?? []).filter(status => status.connectionId === connectionId && status.salePrice != null).map(status => status.salePrice)))
    return <small className="marketplace-pricing-note" role="status">Kaydedince {name} pazaryerindeki {variantRows.length} varyantın fiyatı {formatMoney(sale)} olacak.{known.size > 1 && ' Şu an varyantlarda farklı fiyatlar var; bu fark silinir.'}</small>
  }
  return <>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div className="price-suggestion-title"><h2>Fiyat, Stok ve Vergi<TitleHint label="Fiyat, Stok ve Vergi">Başlangıç değerleri varyantlara uygulanır.</TitleHint></h2>
            {/* On the new and the edit page alike. A card with variants takes the suggested price on every variant row too
                (each row's list price is raised to it only when lower); the cost comes from the card or its first costed row. */}
            <PriceSuggestionLink fields={{ cost: form.costPrice.trim() !== '' ? form.costPrice : priceFieldText(variantRows.find(row => row.costPrice != null && row.costPrice > 0)?.costPrice), vatRate: form.vatRate, vatIncluded: form.vatIncluded !== 'EXCLUDED', listPrice: form.listPrice }} onApply={prices => {
              updateField('salePrice', prices.salePrice); updateField('listPrice', prices.listPrice)
              const sale = priceInputNumber(prices.salePrice)
              if (variantRows.length && Number.isFinite(sale)) setVariantRows(rows => rows.map(row => ({ ...row, salePrice: sale, listPrice: row.listPrice >= sale ? row.listPrice : sale })))
            }} /></div></div>
          <div className="product-step-grid">
            {/* Open on a card with variants too (owner, 10 Nov 2026): there they are the card's default price and the first
                price of every variant row added later; the rows already in the table keep theirs. Decimal comma read. */}
            <label title={variantRows.length > 1 ? 'Yeni eklenen varyantların ilk liste fiyatı; mevcut varyantların fiyatı değişmez.' : undefined}><RequiredLabel>Liste Fiyatı</RequiredLabel><input value={form.listPrice} aria-invalid={requiredFieldInvalid('listPrice')} onChange={event => updateField('listPrice', event.target.value)} type="text" inputMode="decimal" placeholder="0,00" /><EditorFieldError message={requiredError('listPrice')} /></label>
            <label title={variantRows.length > 1 ? 'Yeni eklenen varyantların ilk satış fiyatı; mevcut varyantların fiyatı değişmez.' : undefined}><RequiredLabel>Satış Fiyatı</RequiredLabel><input value={form.salePrice} aria-invalid={requiredFieldInvalid('salePrice')} onChange={event => updateField('salePrice', event.target.value)} type="text" inputMode="decimal" placeholder="0,00" /><EditorFieldError message={requiredError('salePrice') ?? fieldErrors.prices} /></label>
            {/* Maliyet takes two columns: the grid stays even, so Beden Tablosu sits beside Güvenlik Stoğu, not alone on a row. */}
            {variantRows.length <= 1 && <label className="product-cost-field">Maliyet<input value={form.costPrice} onChange={event => { updateField('costPrice', event.target.value); if (variantRows.length === 1) updateVariantRow(variantRows[0].key, 'costPrice', event.target.value) }} type="text" inputMode="decimal" autoComplete="off" /></label>}
            <label htmlFor="product-editor-currency">Para Birimi<PanelSelect id="product-editor-currency" ariaLabel="Para Birimi" value={form.currency} onChange={value => updateField('currency', value)} options={[{ value: 'TRY', label: 'TRY' }, { value: 'USD', label: 'USD' }, { value: 'EUR', label: 'EUR' }]} /></label>
            <label htmlFor="product-editor-vat-rate"><span>KDV Oranı<b className="required-marker" aria-hidden="true">*</b></span><PanelSelect id="product-editor-vat-rate" ariaLabel="KDV Oranı" placeholder="Seçin" className={fieldErrors.vatRate ? 'is-invalid' : undefined} value={form.vatRate} onChange={value => { updateField('vatRate', value); if (value) setFieldErrors(current => ({ ...current, vatRate: undefined })) }} options={[...(form.vatRate !== '' && !TURKISH_VAT_RATES.includes(form.vatRate) ? [{ value: form.vatRate, label: `%${form.vatRate}` }] : []), ...TURKISH_VAT_RATES.map(rate => ({ value: rate, label: `%${rate}` }))]} /><EditorFieldError message={fieldErrors.vatRate} /></label>
            <label htmlFor="product-editor-vat-included">KDV Dahil mi<PanelSelect id="product-editor-vat-included" ariaLabel="KDV Dahil mi" value={form.vatIncluded} onChange={value => updateField('vatIncluded', value)} options={[{ value: 'INCLUDED', label: 'Evet' }, { value: 'EXCLUDED', label: 'Hayır' }]} /></label>
            <label title={showVariantStockTotal || variantRows.length > 1 ? 'Varyantların toplam stoğu' : undefined}>{showVariantStockTotal || variantRows.length > 1 ? 'Toplam Stok' : 'Stok'}<input value={showVariantStockTotal || variantRows.length > 1 ? String(variantStockTotal) : form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="text" inputMode="numeric" autoComplete="off" readOnly={showVariantStockTotal || variantRows.length > 1} /></label>
            <label>Güvenlik Stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="text" inputMode="numeric" autoComplete="off" /></label>
            <SizeChartButton productId={editProductId ?? null} sizes={sizeChartSizes} disabledReason={sizeChartDisabledReason} onUse={applySizeChart} />
          </div>
          {platformCards.length > 0 && <div className="marketplace-pricing-openrow"><button type="button" className={`marketplace-pricing-open${marketplacePricingOpen ? ' is-open' : ''}`} aria-expanded={marketplacePricingOpen} aria-controls="marketplace-pricing-panel" onClick={() => setMarketplacePricingOpen(open => !open)}>
            <span className="marketplace-pricing-open-title"><strong>Pazaryerlerine Özel Fiyat</strong><UiIcon name="chevronDown" size={16} /></span>
            {/* Only the marketplaces with their own price are named; the rest take the main price. */}
            {(() => { const custom = platformCards.filter(card => card.connection.id in channelPricing); return custom.length ? <span className="marketplace-pricing-open-states">{custom.map(card => <span key={card.connection.id} className="marketplace-pricing-state is-on"><i aria-hidden="true" />{card.name}</span>)}</span> : <small>Tüm pazaryerleri ana fiyatla satılır.</small> })()}
          </button>{manyVariants && <TitleHint label="Pazaryerlerine Özel Fiyat">Buraya yazdığınız fiyat, o pazaryerindeki tüm varyantlara uygulanır. Bedene ya da renge göre farklı fiyat için varyant fiyat tablosunu ya da Hızlı Fiyat’ı kullanın.</TitleHint>}</div>}
          {marketplacePricingOpen && <div className="marketplace-pricing-inline" id="marketplace-pricing-panel"><div className="marketplace-pricing-bar-list">{platformCards.map(card => { const pricing = channelPricingInputs(channelPricing[card.connection.id]); const selected = selectedChannelIds.includes(card.connection.id); const customPrice = card.connection.id in channelPricing; const edited = customPrice && channelPriceEdited(card.connection.id); const listIssue = customPrice ? customChannelFieldIssue(card.connection.id, 'listPrice', pricing.listPrice, edited) : null; const saleIssue = customPrice ? customChannelFieldIssue(card.connection.id, 'salePrice', pricing.salePrice, edited) : null; const cardIssue = listIssue === 'unreadable' || saleIssue === 'unreadable' ? trEntryHelp : saleIssue === 'missing' ? customChannelMissingSale : undefined; return <fieldset className={`marketplace-pricing-bar${selected ? ' selected' : ''}`} key={card.connection.id} aria-label={`${card.name} pazaryeri fiyatlandırması`}><div className="marketplace-pricing-bar-heading"><span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span><span><strong>{card.name}</strong><small>{customPrice ? (mix => mix.mixed ? `Özel fiyat ${mix.own} varyantta açık, ${mix.follows} varyant ana fiyatla` : 'Özel fiyat açık')(channelFollowMix(variantRows, card.connection.id)) : 'Ana fiyatla satılır'}</small></span><label className="marketplace-pricing-switch" title="Bu pazaryeri için özel fiyat"><input type="checkbox" role="switch" aria-label={`${card.name} için özel fiyat`} checked={customPrice} onChange={() => toggleCustomChannelPrice(card.connection.id)} /></label></div><div className="marketplace-pricing-fields"><label>Liste Fiyatı<input value={pricing.listPrice} aria-invalid={listIssue !== null} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'listPrice', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label><label>Satış Fiyatı<input value={pricing.salePrice} aria-invalid={saleIssue !== null} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'salePrice', event.target.value)} type="text" inputMode="decimal" autoComplete="off" /></label></div><EditorFieldError message={cardIssue} />{channelNote(card.connection.id, card.name)}</fieldset>})}</div></div>}
        </section>
      </div>
  </>
}
