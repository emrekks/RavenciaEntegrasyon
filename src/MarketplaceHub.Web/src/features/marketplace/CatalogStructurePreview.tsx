import { useMemo, useState } from 'react'
import { StatusBadge, UiIcon } from '../../shared/components'
import {
  catalogStructureAttributeStatus,
  catalogStructureCategoryNeedsAttention,
  catalogStructureDecisionExplanation,
  catalogStructureDecisionLabel,
  catalogStructureDecisionTone,
  catalogStructureFilterCounts,
  catalogStructureNarrative,
  catalogStructureNewValueCount,
  catalogStructureVisibleAttributes,
  matchesCatalogStructureFilter,
  searchCatalogStructureCategories,
  type CatalogStructureCategory,
  type CatalogStructureFilter,
  type CatalogStructureReport
} from './catalog-structure-report'

const filterLabels: [CatalogStructureFilter, string][] = [
  ['ALL', 'Tümü'],
  ['CHANGES', 'Değişenler'],
  ['NEW', 'Yeni'],
  ['SEPARATE', 'Ayrı tutulan'],
  ['REVIEW', 'İnceleme'],
  ['SYNCED', 'Eşleşmiş']
]

const visibleValueLimit = 12

function number(value: number) {
  return value.toLocaleString('tr-TR')
}

export function CatalogStructurePreview({ report, platformLabel }: { report: CatalogStructureReport; platformLabel: string }) {
  const counts = useMemo(() => catalogStructureFilterCounts(report.categories), [report.categories])
  // Start on what changes; fall back to everything when nothing does.
  const [chosenFilter, setFilter] = useState<CatalogStructureFilter | null>(null)
  const filter: CatalogStructureFilter = chosenFilter ?? (counts.CHANGES > 0 ? 'CHANGES' : 'ALL')
  const [query, setQuery] = useState('')
  const visible = useMemo(() => searchCatalogStructureCategories(report.categories, query).filter(category => matchesCatalogStructureFilter(category, filter)), [report.categories, query, filter])
  const { totals } = report
  const applied = report.mode === 'APPLY'

  return <div className="structure-preview">
    <dl className="structure-preview-tiles">
      <div><dt>Taranan ürün</dt><dd>{number(totals.products)}</dd>{totals.productsSkipped + totals.productsWithoutCategory > 0 && <small>{number(totals.productsSkipped + totals.productsWithoutCategory)} ürün kapsam dışı</small>}</div>
      <div><dt>Kategori</dt><dd>{number(totals.categories)}</dd><small>{number(totals.categoriesCreated)} yeni · {number(totals.categoriesReused)} bağlanan · {number(totals.categoriesAlreadyMapped)} eşli</small></div>
      <div className={totals.attributesCreated > 0 ? 'is-change' : undefined}><dt>{applied ? 'Eklenen özellik' : 'Eklenecek özellik'}</dt><dd>{number(totals.attributesCreated)}</dd></div>
      <div className={totals.valuesCreated > 0 ? 'is-change' : undefined}><dt>{applied ? 'Eklenen değer' : 'Eklenecek değer'}</dt><dd>{number(totals.valuesCreated)}</dd></div>
      <div className={totals.mappingsCreated > 0 ? 'is-change' : undefined}><dt>{applied ? 'Yazılan eşleme' : 'Yazılacak eşleme'}</dt><dd>{number(totals.mappingsCreated)}</dd></div>
    </dl>

    <section className="structure-preview-narrative" aria-label={applied ? 'Yapılanlar' : 'Uygulayınca olacaklar'}>
      <h3>{applied ? 'Panele yazılanlar' : 'Uygulayınca neler olacak'}</h3>
      <ul>{catalogStructureNarrative(report, platformLabel).map(line => <li key={line}>{line}</li>)}</ul>
    </section>

    {report.warnings.map(warning => <p key={warning} className="structure-import-warning" role="alert">{warning}</p>)}
    {report.review.length > 0 && <section aria-label="İnceleme gerekenler" className="structure-preview-review">
      <h3>İnceleme gerekenler</h3>
      <ul>{report.review.map(item => <li key={`${item.subject}:${item.reason}`}><strong>{item.subject}</strong><span>{item.reason}</span></li>)}</ul>
    </section>}

    <div className="structure-preview-toolbar">
      <div className="structure-preview-filters" role="group" aria-label="Kategori filtresi">
        {filterLabels.filter(([key]) => key === 'ALL' || counts[key] > 0).map(([key, label]) => <button key={key} type="button" className={key === filter ? 'is-active' : undefined} aria-pressed={key === filter} onClick={() => setFilter(key)}>{label}<span>{counts[key]}</span></button>)}
      </div>
      <input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Kategori, özellik veya değer ara…" aria-label="Kategorilerde ara" />
    </div>

    {visible.length === 0
      ? <p className="structure-import-hint">Bu filtreyle eşleşen kategori yok.</p>
      : <div className="structure-preview-list">{visible.map(category => <CategoryRow key={category.externalId} category={category} platformLabel={platformLabel} filter={filter} />)}</div>}

    {(report.responseFields?.length ?? 0) > 0 && <details className="structure-preview-technical">
      <summary>Teknik ayrıntılar</summary>
      <p className="structure-import-hint">{platformLabel} ürün yanıtındaki alanlar (yalnızca alan adları):</p>
      <p className="structure-preview-code">{report.responseFields!.join(' · ')}</p>
    </details>}
  </div>
}

function CategoryRow({ category, platformLabel, filter }: { category: CatalogStructureCategory; platformLabel: string; filter: CatalogStructureFilter }) {
  const attributes = catalogStructureVisibleAttributes(category, filter)
  const newValues = category.attributes.reduce((sum, attribute) => sum + catalogStructureNewValueCount(attribute), 0)
  const difference = category.difference
  return <details className="structure-row" open={catalogStructureCategoryNeedsAttention(category) && category.decision === 'NeedsReview'}>
    <summary>
      <StatusBadge tone={catalogStructureDecisionTone(category.decision)}>{catalogStructureDecisionLabel(category.decision)}</StatusBadge>
      <span className="structure-row-names"><strong>{category.remoteName}</strong>{category.remotePath && <small>{category.remotePath}</small>}</span>
      <span className="structure-row-link" aria-hidden="true"><UiIcon name="link" size={16} /></span>
      <strong className="structure-row-panel">{category.panelName || '—'}</strong>
      <span className="structure-row-meta"><span>{number(category.productCount)} ürün</span><span>{category.attributes.length} özellik</span>{newValues > 0 && <span className="is-change">+{number(newValues)} değer</span>}</span>
    </summary>
    <p className="structure-row-explanation">{catalogStructureDecisionExplanation(category, platformLabel)}</p>
    {category.newValues > 0 && category.attributes.every(attribute => catalogStructureNewValueCount(attribute) === 0) && <p className="structure-import-warning" role="note">Bu kategoride {number(category.newValues)} yeni değer var ama bu rapor eski sürümle üretildiği için hangi özelliklere ekleneceği kayıtlı değil. Ayrıntıyı görmek için önizlemeyi yeniden başlatın.</p>}
    {difference && <div className="structure-row-difference">
      {difference.onlyInMarketplace.length > 0 && <p><strong>Yalnızca {platformLabel} kategorisinde olan:</strong> {difference.onlyInMarketplace.join(', ')}</p>}
      {difference.onlyInPanel.length > 0 && <p><strong>Yalnızca {difference.comparedSource === 'Panel' ? 'panel' : difference.comparedSource} kategorisinde olan:</strong> {difference.onlyInPanel.join(', ')}</p>}
    </div>}
    {attributes.length > 0
      ? <table className="structure-import-attribute-table"><thead><tr><th>Özellik</th><th>Durum</th><th>Eklenecek değerler</th></tr></thead><tbody>{attributes.map(attribute => {
        const count = catalogStructureNewValueCount(attribute)
        return <tr key={`${attribute.remoteName}:${attribute.panelName}`}>
          <td><strong>{attribute.panelName}</strong>{attribute.remoteName !== attribute.panelName && <small>Pazar yerinde: {attribute.remoteName}</small>}</td>
          <td>{catalogStructureAttributeStatus(attribute)}</td>
          <td>{count > 0
            ? <span className="structure-import-values">{(attribute.newValues ?? []).slice(0, visibleValueLimit).map(value => <span key={value}>{value}</span>)}{count > visibleValueLimit && <small>+{number(count - visibleValueLimit)} değer daha</small>}</span>
            : <span className="structure-import-none" title={attribute.usedValueCount ? `Ürünlerdeki ${attribute.usedValueCount} değer panelde zaten mevcut` : undefined}>Eklenecek değer yok</span>}</td>
        </tr>
      })}</tbody></table>
      : <p className="structure-import-hint">{category.attributes.length > 0 ? 'Bu kategoride değişen özellik yok.' : 'Bu kategoride ürünlerin kullandığı özellik bulunamadı.'}</p>}
    {(category.unmatchedFields?.length ?? 0) > 0 && <p className="structure-import-hint">Ürünlerde olup kategori özelliğiyle eşleşmeyen alanlar: {category.unmatchedFields!.join(', ')}</p>}
  </details>
}
