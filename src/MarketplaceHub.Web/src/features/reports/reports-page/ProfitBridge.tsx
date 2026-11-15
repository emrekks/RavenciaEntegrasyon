import { useId, useState, type ReactNode } from 'react'
import { EmptyState, Modal, UiIcon } from '../../../shared/components'
import { formatCount, formatNumber, formatPercent, formatTl, platformColor, platformName, type ReportCosts, type ReportSales } from '../report-format'
import { Progress } from '../report-visuals'
import { PlatformSquareMark } from '../../../shared/platform-square-mark'
import { type OpenDetail, TipMark, TipText, Card, type Money, signedTl, costMeaning, explain, commissionGap, cargoGap, vatMore, unlinkedMeaning, unlinkedTitle, lossSentence, orderlessLines, kurus, MoreButton, financeCosts, feeCategories, chargeFeeCategories, apartAmount, costFigure, coveredNote, NoAmountNote } from './report-parts'

const unlinkedNote = 'Trendyol\'un bir siparişe bağlamadan, işlem tarihinde kestiği reklam, ceza ve kesinti faturaları.'

const resultSentence = (value: number) => value < 0 ? lossSentence : 'Tüm kesintiler, ürün maliyeti ve reklam düşüldükten sonra kalan tahmini kâr.'

// Satış Tutarı: the list price less the seller's discount (the marketplace's own discount is the marketplace's to pay).
const salesTip = 'Satıcı indirimi düşülmüş satış tutarı; iptaller sayılmaz, iadeler henüz düşülmedi.'

type BridgeRow = { key: string; label: string; from: number; to: number; kind: 'total' | 'minus' | 'end'; note: string }

// Kâr Köprüsü: the sales on one scale, each deduction taken off where the one before it ended (the returns, the cargo,
// the marketplace's commission and fees, the withholding tax), then the product cost, down to the estimated net profit
// (or, with no cost entered, what the deductions leave). The same account as the profit tab; the sales open their
// platforms in a window.
export function ProfitBridge({ data, money, onMore }: { data: ReportSales; money: Money | null; onMore: OpenDetail }) {
  const [open, setOpen] = useState(false)
  const [detail, setDetail] = useState<string | null>(null)
  const tip = useId()
  const more = <MoreButton title="Kâr Köprüsü" onClick={() => onMore('cost')} compact />
  if (!money) return <Card title="Kâr Köprüsü" className="reports-card-bridge" actions={more}>{() => <EmptyState icon="chart" title="Veri Yok">Bu dönemde tutarı olan sipariş yok.</EmptyState>}</Card>
  const part = (key: string) => money.deductions.find(item => item.key === key)
  // Each step is the account's figure (moneyBreakdown, costFigure), the same number Masraf Kalemleri shows: the Stopaj
  // step is that line's stopaj, and the fees beside the commission are the account's Kesintiler without it.
  const stoppage = costFigure(data, 'STOPPAGE') ?? 0
  const otherFees = feeCategories.filter(fee => chargeFeeCategories.includes(fee.category) && fee.category !== 'STOPPAGE')
    .map(fee => ({ label: fee.label, value: costFigure(data, fee.category) ?? 0 })).filter(fee => fee.value !== 0)
  const commission = part('commission')
  const cargo = part('cargo')
  const taken = [
    { key: 'returned', label: 'İade Edilen Tutar', value: part('returned')?.value ?? 0, note: explain('returned') },
    { key: 'cargo', label: 'Kargo', value: cargo?.value ?? 0, note: explain('cargo', cargoGap(data)) },
    { key: 'marketplace', label: 'Pazaryeri Masrafları', value: (commission?.value ?? 0) + (part('fees')?.value ?? 0) - stoppage,
      note: `${explain('marketplace', commissionGap(data))}\n${[commission ? `${commission.label} ${formatTl(commission.value)}` : null, ...otherFees.map(fee => `${fee.label} ${formatTl(fee.value)}`)].filter(Boolean).join(' · ')}` },
    { key: 'stoppage', label: 'Stopaj', value: stoppage, note: explain('stoppage', coveredNote(data, 'STOPPAGE')) },
    { key: 'vat', label: 'Net KDV', value: money.tax ?? 0, note: explain('vat', vatMore(money.tax)) },
    // The product cost only with the profit: with part of it missing the bridge ends at what the deductions leave.
    { key: 'cost', label: 'Ürün Maliyeti', value: money.profit === null ? 0 : money.cost ?? 0, note: explain('cost', money.estimate) },
    { key: 'unlinked', label: unlinkedTitle, value: money.unlinked, note: `${unlinkedNote} Net Kâr'dan düşülür.` }
  ].filter(item => item.value !== 0)
  const rows: BridgeRow[] = [{ key: 'sales', label: 'Satış Tutarı', from: 0, to: money.total, kind: 'total', note: salesTip + ' Platformlara göre görmek için tıklayın.' }]
  let at = money.total
  for (const item of taken) {
    rows.push({ key: item.key, label: item.label, from: at - item.value, to: at, kind: 'minus', note: item.note })
    at -= item.value
  }
  // Without a product cost the steps still take the net VAT and the unlinked deductions off, so the result is where they
  // end, "Maliyet Öncesi Kalan": never "Masraflar Sonrası", the ledger's figure before the VAT (one label, one value).
  const result = money.profit ?? money.left - (money.tax ?? 0) - money.unlinked
  // What the account could not take off is listed under the bridge (EstimateGaps), so the result's note stays short.
  rows.push({ key: 'result', label: money.profit === null ? 'Maliyet Öncesi Kalan' : result < 0 ? 'Tahmini Zarar' : 'Tahmini Net Kâr', from: 0, to: result, kind: 'end',
    note: money.profit === null ? `İade, pazaryeri kesintileri, stopaj, net KDV${money.unlinked !== 0 ? ' ve reklam / diğer kesintiler' : ''} düşüldükten sonra kalan; ürün maliyeti düşülmeden (Kârlılık'taki Masraflar Sonrası KDV'den öncedir). ${money.uncosted > 0 ? `Satılan ${formatNumber(money.uncosted)} adedin ürün maliyeti girilmediği için kâr hesaplanamadı; satılan adetlerin en az %95'inin maliyeti girilince hesaplanır.` : 'Ürün maliyeti girilmediği için kâr hesaplanamadı.'}`
      : `${resultSentence(result)}\nNet marj ${formatPercent(money.margin)}` })
  const low = Math.min(0, ...rows.flatMap(row => [row.from, row.to]))
  const span = Math.max(1, Math.max(money.total, ...rows.flatMap(row => [row.from, row.to])) - low)
  // A deduction's breakdown for its window; the result opens the whole account (Kâr Hesabı's lines).
  const details = new Map(rows.flatMap(row => {
    const detail = row.kind === 'minus' ? bridgeDetail(data, row.key, row.to - row.from) : null
    return detail ? [[row.key, detail] as const] : []
  }))
  const picked = rows.find(row => row.key === detail) ?? null
  const pickedDetail = picked ? details.get(picked.key) ?? null : null
  return <Card title="Kâr Köprüsü" className="reports-card-bridge" actions={more}>
    {shown => <>
      <ol className="reports-bridge">{rows.map((row, index) => {
        const start = Math.min(row.from, row.to)
        const width = Math.abs(row.to - row.from) / span * 100
        const opens = row.kind === 'end' || details.has(row.key)
        return <li key={row.key} className={`reports-tip-host is-${row.kind}${row.kind === 'end' && row.to < 0 ? ' is-negative' : ''}`}>
          <span className="reports-bridge-label">{row.key === 'sales'
            ? <><button type="button" onClick={() => setOpen(true)} aria-label="Satış tutarı: platformlara göre aç" aria-describedby={`${tip}-${row.key}`}>{row.label}<UiIcon name="chevronRight" size={14} /></button><TipMark /></>
            : <button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${row.key}`} aria-haspopup={opens ? 'dialog' : undefined}
              onClick={opens ? () => setDetail(row.key) : undefined}>{`${row.kind === 'minus' ? (row.to < row.from ? '+ ' : '− ') : ''}${row.label}`}<TipMark /></button>}</span>
          <span className="reports-bridge-track" aria-hidden="true"><i style={{ left: `${(start - low) / span * 100}%`, width: shown ? `${Math.max(0.5, width)}%` : '0%', transitionDelay: `${index * 80}ms` }} /></span>
          <b>{row.kind === 'minus' ? `${row.to < row.from ? '+' : '−'} ${formatTl(Math.abs(row.to - row.from))}` : signedTl(row.to)}</b>
          <span role="tooltip" id={`${tip}-${row.key}`} className="reports-hover-tip"><TipText title={row.label} text={row.note} /></span>
        </li>
      })}</ol>
      <EstimateGaps money={money} shown={shown} />
      {/* The platforms open over the page, so nothing under the card moves. */}
      <Modal open={open} onClose={() => setOpen(false)} className="reports-journey-modal" title="Satış Tutarı"><SalesDetail data={data} /></Modal>
      {/* A row's breakdown: its short explanation under the title, the lines adding up to its figure. */}
      <Modal open={picked !== null} onClose={() => setDetail(null)} className="reports-journey-modal reports-bridge-modal" title={picked?.label ?? ''}
        description={picked ? picked.note.split('\n')[0] : undefined}>
        {picked?.kind === 'end' ? <ProfitLedger data={data} money={money} />
          : picked && pickedDetail ? <BridgeDetail detail={pickedDetail} total={picked.to - picked.from} /> : null}
      </Modal>
    </>}
  </Card>
}

// What an estimate leaves out, always written and not only under the pointer: how much of the cost is entered (or
// that none is), whose cargo and fees are not taken off, and an unknown commission. Each row names the gap on the left
// with how to read it, and its measure on the right; nothing is drawn when nothing is left out.
export function EstimateGaps({ money, shown }: { money: Money; shown: boolean }) {
  const { gaps } = money
  const partly = money.cost !== null && (money.costCoverage ?? 1) < 1
  if (!partly && money.cost !== null && !gaps.platforms.length && !gaps.lines.length && !gaps.commission) return null
  return <dl className="reports-bridge-gaps" aria-label="Tahminin kapsamı">
    {money.cost === null && <div><dt>Ürün Maliyeti<small>Ürün kartında girilmedi</small></dt><dd><span>Kâr hesaplanamadı</span></dd></div>}
    {partly && <div><dt>Maliyeti Girili<small>{money.uncosted > 0 ? `${formatNumber(money.uncosted)} adet eksik, ${money.profit === null ? 'kâr hesaplanamadı' : 'maliyeti tahmini'}` : 'Satılan adetlerin payı'}</small></dt><dd>
      <Progress value={money.costCoverage} shown={shown} /><b>{formatPercent(money.costCoverage)}</b>
    </dd></div>}
    {gaps.platforms.length > 0 && <div><dt>Kargo ve Hizmet Bedeli<small>Düşülmedi</small></dt><dd>
      <span className="reports-bridge-chips">{gaps.platforms.map(code => <span key={code}><PlatformSquareMark code={code} name={platformName(code)} />{platformName(code)}</span>)}</span>
    </dd></div>}
    {gaps.lines.length > 0 && <div><dt>Düşülmeyen<small>Cari hesap okunmadı</small></dt><dd><span>{gaps.lines.join(', ')}</span></dd></div>}
    {gaps.commission && <div><dt>Komisyon<small>Düşülmedi</small></dt><dd><span>Bilinmiyor</span></dd></div>}
  </dl>
}

// A Kâr Köprüsü row's window (owner, 11 Nov 2026: "üstüne basınca detayları popupta açılmalıydı"): the row's figure
// split by kind and, where the report carries it, by platform. Every list adds up to the row's figure to the kuruş: a
// remainder is listed as Diğer with why, and a platform split that does not add up is left out rather than shown wrong.
type BridgeItem = { key: string; label: string; value: number; note?: string; platform?: string }
type BridgeGroup = { title: string; items: BridgeItem[] }
type BridgeDetailView = { groups: BridgeGroup[]; facts: Array<{ label: string; value: string }> }

const bridgeRemainder = (items: BridgeItem[], total: number) => kurus(total - items.reduce((sum, item) => sum + item.value, 0))
// The items that are not zero; what they leave of the total (a kuruş or two of rounding) as Diğer, explained.
function balanced(items: BridgeItem[], total: number, note: string): BridgeItem[] {
  const shown = items.filter(item => kurus(item.value) !== 0)
  const rest = bridgeRemainder(shown, total)
  return rest === 0 ? shown : [...shown, { key: 'other', label: 'Diğer', value: rest, note }]
}
const roundingNote = 'Kalemler kuruşa ayrı ayrı yuvarlandığı için kalan yuvarlama farkı.'
// By platform: only when the platforms' own figures (with the records tied to no order) add up to the total, give or take
// a few kuruş of rounding; otherwise nothing, never a split that disagrees with the row.
function byPlatform(values: Map<string, number>, apart: number, total: number): BridgeGroup | null {
  const items: BridgeItem[] = [...values].filter(([, value]) => kurus(value) !== 0).sort((a, b) => b[1] - a[1])
    .map(([code, value]) => ({ key: code, label: platformName(code), value, platform: code }))
  if (apart !== 0) items.push({ key: 'apart', label: 'Siparişe Bağlı Olmayan Kayıtlar', value: apart, note: 'Trendyol cari hesabında dönemin bir siparişine bağlanmayan kayıtlar.' })
  if (!items.length || Math.abs(bridgeRemainder(items, total)) > 0.05) return null
  return { title: 'Platformlara Göre', items: balanced(items, total, roundingNote) }
}
function channelSum(data: ReportSales, value: (channel: ReportCosts['channels'][number]) => number) {
  const sums = new Map<string, number>()
  for (const channel of data.costs?.channels ?? []) sums.set(channel.platformCode, (sums.get(channel.platformCode) ?? 0) + value(channel))
  return sums
}
const channelAmount = (channel: ReportCosts['channels'][number], categories: string[]) => channel.amounts.filter(amount => categories.includes(amount.category)).reduce((sum, amount) => sum + amount.amount, 0)

function bridgeDetail(data: ReportSales, key: string, total: number): BridgeDetailView | null {
  const finance = financeCosts(data)
  const apart = (categories: string[]) => categories.reduce((sum, category) => sum + apartAmount(data, category), 0)
  const kinds = (items: BridgeItem[], note = roundingNote): BridgeGroup => ({ title: 'Kalemlere Göre', items: balanced(items, total, note) })
  const groups = (...list: Array<BridgeGroup | null>) => list.filter((group): group is BridgeGroup => group !== null && group.items.length > 0)
  if (key === 'returned') {
    const values = new Map(data.platforms.filter(group => group.known).map(group => [group.key, group.grossSales - group.netRevenue]))
    return { groups: groups(byPlatform(values, 0, total)), facts: [{ label: 'İade Edilen Adet', value: formatCount(data.summary.returnedQuantity) }, { label: 'İade Talebi', value: formatCount(data.summary.returnClaims) }] }
  }
  if (key === 'marketplace') {
    const commissionApart = apart(['COMMISSION'])
    const commission = finance?.commission
      ? [{ key: 'real', label: 'Gerçek Komisyon', value: finance.commission.real, note: `${formatCount(finance.commission.realOrders)} siparişte pazaryerinin kendi kaydından.` },
        { key: 'estimated', label: 'Tahmini Komisyon', value: finance.commission.estimatedForUnsettled, note: 'Kaydı olmayan siparişlerde siparişteki komisyon oranından.' },
        { key: 'commissionApart', label: 'Siparişe Bağlı Olmayan Komisyon', value: commissionApart, note: unlinkedMeaning.COMMISSION }]
      : [{ key: 'estimated', label: 'Tahmini Komisyon', value: data.commission.estimatedCommission, note: `Siparişteki orandan; satışların ${formatPercent(data.commission.coverage)} kadarının oranı biliniyor.` }]
    const fees = finance ? [
      { key: 'fee', label: 'Hizmet Bedeli', value: costFigure(data, 'SERVICE_FEE') ?? 0, note: costMeaning.fee },
      { key: 'payment', label: 'Ödeme Komisyonu', value: costFigure(data, 'PAYMENT_FEE') ?? 0, note: costMeaning.payment }] : []
    const platforms = finance
      ? byPlatform(channelSum(data, channel => channel.realCommission + channel.estimatedCommission + channelAmount(channel, ['SERVICE_FEE', 'PAYMENT_FEE'])), apart(['COMMISSION', 'SERVICE_FEE', 'PAYMENT_FEE']), total)
      : byPlatform(new Map(data.platforms.filter(group => group.known).map(group => [group.key, group.estimatedCommission])), 0, total)
    return { groups: groups(kinds([...commission, ...fees]), platforms), facts: [] }
  }
  if (key === 'cargo' && finance?.cargo) {
    const cargo = finance.cargo
    const returnBilled = cargo.returnBilled ?? 0
    const platformEstimated = cargo.platformEstimated ?? 0
    const items = [
      { key: 'billed', label: 'Faturalı Gönderim Kargosu', value: cargo.real - returnBilled, note: `${formatCount(cargo.billedOrders)} siparişin kargo faturası geldi.` },
      { key: 'returnBilled', label: 'Faturalı İade Kargosu', value: returnBilled, note: `${formatCount(cargo.returnBilledOrders ?? 0)} iadenin dönüş kargo faturası geldi.` },
      { key: 'estimated', label: 'Faturası Gelmeyen, Tahmini', value: (cargo.estimated ?? 0) - platformEstimated, note: 'Trendyol\'da faturası henüz gelmeyen gönderim ve iadeler, mağazanın kargo ortalamasıyla.' },
      { key: 'platform', label: 'Diğer Platformlar, Tarifeden', value: platformEstimated, note: `${formatCount(cargo.platformOrders ?? 0)} sipariş, kargo firması ve desiye göre 2026 tarifelerinden.` },
      { key: 'cargoApart', label: 'Siparişe Bağlanamayan Kargo', value: apart(['CARGO']), note: unlinkedMeaning.CARGO }
    ]
    const platforms = byPlatform(channelSum(data, channel => channel.estimatedCargo + channelAmount(channel, ['CARGO'])), apart(['CARGO']), total)
    return { groups: groups(kinds(items), platforms), facts: [] }
  }
  if (key === 'stoppage' && finance) {
    const line = finance.lines.find(item => item.category === 'STOPPAGE')
    const items = [
      { key: 'linked', label: 'Ödeme Kayıtlarından', value: line?.orderLinked ?? 0, note: 'Siparişlerin ödeme günü faturasından payına düşen stopaj.' },
      { key: 'estimated', label: 'Tahmini', value: line?.estimated ?? 0, note: 'Faturası henüz paylaştırılmayan siparişlerde satışın %1\'i.' },
      { key: 'stoppageApart', label: 'Siparişe Bağlı Olmayan', value: apart(['STOPPAGE']), note: unlinkedMeaning.STOPPAGE }
    ]
    const covered = coveredNote(data, 'STOPPAGE')
    return { groups: groups(kinds(items), byPlatform(channelSum(data, channel => channelAmount(channel, ['STOPPAGE'])), apart(['STOPPAGE']), total)), facts: covered ? [{ label: 'Not', value: covered }] : [] }
  }
  if (key === 'vat' && finance?.netVat) {
    const vat = finance.netVat
    return { groups: groups(kinds([
      { key: 'sales', label: 'Satışların KDV\'si', value: vat.salesVat, note: 'Yurt içi satışların, iadeler düşülmüş KDV\'si.' },
      { key: 'cost', label: 'Ürün Maliyetinin KDV\'si', value: -vat.productCostVat, note: 'Maliyeti girili satılan ürünlerin alış KDV\'si, maliyet KDV dahil kabul edilerek; düşülür.' },
      { key: 'expense', label: 'Masrafların KDV\'si', value: -vat.expenseVat, note: 'Komisyon, kargo, hizmet bedeli ve reklamın KDV\'si; düşülür.' }
    ])), facts: [] }
  }
  if (key === 'cost') {
    const cost = data.cost
    return { groups: groups(kinds([{ key: 'costed', label: 'Maliyeti Girili Satışlar', value: cost.costTotal, note: `${formatCount(cost.costedQuantity)} adet, ${formatCount(cost.costedVariants)} varyant.` }])),
      facts: [
        { label: 'Maliyeti Girili Adet', value: `${formatCount(cost.costedQuantity)} / ${formatCount(cost.keptQuantity)} (${formatPercent(cost.coverage)})` },
        { label: 'Maliyeti Girili Satış', value: formatTl(cost.costedRevenue) },
        ...((cost.coverage ?? 1) < 1 ? [{ label: 'Düşülmeyen', value: 'Maliyeti girilmeyen ürünlerin maliyeti' }] : [])
      ] }
  }
  if (key === 'unlinked') {
    const { lines } = orderlessLines(data)
    return { groups: groups(kinds(lines.map(line => ({ key: line.key, label: line.label, value: line.amount, note: `${unlinkedMeaning[line.key] ?? 'Dönemin siparişlerine bağlanamayan kesinti.'} ${formatCount(line.records)} kayıt.` })))), facts: [] }
  }
  return null
}

function BridgeDetail({ detail, total }: { detail: BridgeDetailView; total: number }) {
  return <div className="reports-bridge-detail">
    {detail.groups.map(group => <section key={group.title}>
      <h3>{group.title}</h3>
      <ul>{group.items.map(item => <li key={item.key}>
        {item.platform ? <PlatformSquareMark code={item.platform} name={item.label} /> : <i aria-hidden="true" />}
        <span className="reports-bridge-detail-name">{item.label}{item.note && <small>{item.note}</small>}</span>
        <b>{signedTl(item.value)}</b>
      </li>)}
        {/* Below zero the row adds to the profit (a VAT carried forward): the total is written as the bridge writes it. */}
        <li className="is-total"><i aria-hidden="true" /><span className="reports-bridge-detail-name">{total < 0 ? 'Toplam · Kâra Eklenir' : 'Toplam'}</span><b>{total < 0 ? `+ ${formatTl(-total)}` : formatTl(total)}</b></li>
      </ul>
    </section>)}
    {detail.facts.length > 0 && <dl>{detail.facts.map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd>{fact.value}</dd></div>)}</dl>}
  </div>
}

// Satış Tutarı's breakdown: each platform's sales with its share, the total under them.
function SalesDetail({ data }: { data: ReportSales }) {
  const rows = data.platforms.filter(group => group.ordersWithAmount > 0 && group.grossSales > 0).sort((a, b) => b.grossSales - a.grossSales)
  const total = rows.reduce((sum, group) => sum + group.grossSales, 0)
  return <div className="reports-journey-pane">
    <h3>Platformlara Göre Satış</h3>
    {rows.length === 0 ? <EmptyState icon="chart" title="Veri Yok">Bu dönemde tutarı olan satış yok.</EmptyState> : <ul className="reports-platform-list is-columns">{rows.map(group => <li key={group.key}>
      <PlatformSquareMark code={group.key} name={platformName(group.label)} />
      <span>{platformName(group.label)}<small>{formatPercent(group.grossSales / total)} · {formatCount(group.orders)} sipariş</small></span>
      <strong>{formatTl(group.grossSales)}</strong>
      <Progress value={group.grossSales / total} shown tone={platformColor(group.key)} />
    </li>)}</ul>}
    <NoAmountNote platforms={data.platforms} />
  </div>
}

// The account from the sales to the profit, line by line; the overview's window and the profit tab show the same.
// notes: a sentence more under a row's explanation (the commission's rate and coverage, how much of the cost is entered).
export function ProfitLedger({ data, money, notes = {} }: { data: ReportSales; money: Money; notes?: Record<string, string> }) {
  const tip = useId()
  const amount = (value: number) => value < 0 ? `− ${formatTl(-value)}` : formatTl(value)
  const share = (value: number) => `Satış tutarının ${money.total > 0 ? formatPercent(Math.abs(value) / money.total) : '—'} kadarı.`
  const row = (key: string, className: string | undefined, label: string, value: string, note: string, extra?: ReactNode) =>
    <div key={key} className={`reports-tip-host${className ? ` ${className}` : ''}`}>
      <dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${key}`}>{label}<TipMark /></button>{extra}</dt>
      <dd>{value}</dd>
      <span role="tooltip" id={`${tip}-${key}`} className="reports-hover-tip"><TipText title={label} text={note} /></span>
    </div>
  // The commission's coverage is in its note (ledgerNotes); the cargo says what could not be estimated.
  const gap: Record<string, string> = { cargo: cargoGap(data) }
  return <dl className="reports-ledger">
    {row('sales', undefined, 'Satış Tutarı', formatTl(money.total), salesTip)}
    {money.deductions.map(part => row(part.key, part.value < 0 ? undefined : 'is-minus', part.label, part.value < 0 ? `+ ${formatTl(-part.value)}` : `− ${formatTl(part.value)}`, `${explain(part.key, gap[part.key])}\n${share(part.value)}${notes[part.key] ? ` ${notes[part.key]}` : ''}`))}
    {row('left', `is-total is-${money.left < 0 ? 'bad' : 'good'}`, 'Masraflar Sonrası', amount(money.left), `Satıştan iade ve pazaryeri kesintileri düşüldükten sonra kalan; ürün maliyeti düşülmeden.\n${share(money.left)}`)}
    {money.tax !== null && row('vat', money.tax < 0 ? undefined : 'is-minus', 'Net KDV', money.tax < 0 ? `+ ${formatTl(-money.tax)}` : `− ${formatTl(money.tax)}`,
      `${explain('vat', vatMore(money.tax))}\n${share(money.tax)}`)}
    {money.cost !== null && money.profit !== null && row('cost', 'is-minus', 'Ürün Maliyeti', `− ${formatTl(money.cost)}`, `${costMeaning.cost}\n${share(money.cost)}${money.estimate ? ` ${money.estimate}` : notes.cost ? ` ${notes.cost}` : ''}`)}
    {money.unlinked !== 0 && row('unlinked', money.unlinked < 0 ? undefined : 'is-minus', unlinkedTitle, money.unlinked < 0 ? `+ ${formatTl(-money.unlinked)}` : `− ${formatTl(money.unlinked)}`,
      `${unlinkedNote}\n${share(money.unlinked)}`)}
    {money.profit !== null && row('profit', `is-result is-${money.profit < 0 ? 'bad' : 'good'}`, money.profit < 0 ? 'Tahmini Zarar' : 'Tahmini Net Kâr', amount(money.profit),
      `${resultSentence(money.profit)}\nNet Ciro'nun ${formatPercent(money.margin === null ? null : Math.abs(money.margin))} kadarı; bu oran net marjdır.`, <small>Net marj {formatPercent(money.margin)}</small>)}
  </dl>
}
