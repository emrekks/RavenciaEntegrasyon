// The Turkish names of the marketplaces' accounting record types, for the payout records (PayoutLine.transactionType).
// Trendyol and Pazarama already send Turkish names ("Satış", "Hakediş Ödemesi Bekliyor"); Hepsiburada sends codes
// (its muhasebe guide's "Kayıt Tipi Bilgileri" table, read 4 Nov 2026; two of its pairs are swapped in the guide, BNPL
// processing fee and e-invoice sales, and are named here the right way round); n11's lines carry the day's state, so
// they are named by what they are. A code no entry names is shown as sent and reported once on the console.
export const hepsiburadaTransactionLabels: Record<string, string> = {
  Payment: 'Satış Ödemesi', DeliveryProcessingFee: 'İşlem Bedeli', DeliveryProcessingFeeRefund: 'İşlem Bedeli İadesi', Return: 'İade',
  Commission: 'Komisyon', CommissionInvoiceRefund: 'Komisyon İadesi', CommissionRefund: 'Komisyon İadesi', CommissionCorrection: 'Komisyon Düzeltme',
  ShipmentCostSharingExpense: 'Kargo Katkı Payı', ShipmentCostSharingIncome: 'Kargo Katkı Payı İadesi', ProcessingFeeExpense: 'Hizmet Bedeli',
  ProcessingFeeExpenseRefund: 'Hizmet Bedeli İadesi', CustomerSatisfaction: 'Müşteri Memnuniyeti Yansıtma', CustomerSatisfactionRefund: 'Müşteri Memnuniyeti Yansıtma İadesi',
  LineItemTransferExpense: 'Sipariş Transfer Gideri', LineItemTransferIncome: 'Sipariş Transfer Geliri', CampaignDiscount: 'Kampanya İndirimi',
  CampaignDiscountRefund: 'Kampanya İndirimi İadesi', RevenueExpense: 'Ciro Gideri', RevenueIncome: 'Ciro Geliri', CargoMargin: 'Kargo Marjı',
  CargoCompensationIncomeRefund: 'Kargo Tazmin Geliri İadesi', MpScrapIncome: 'Hurda Geliri', RefusedInvoiceExpenseRefund: 'Reddedilen Fatura İadesi',
  RefusedInvoiceExpense: 'Reddedilen Fatura Bedeli', Deposit: 'Depozito', AdSharingExpense: 'Reklam Katılım Gideri', AdSharingExpenseRefund: 'Reklam Katılım Gideri İadesi',
  DropShipmentCostSharingExpense: 'Drop Kargo Katkı Payı', DropShipmentCostSharingIncome: 'Drop Kargo Katkı Payı Geliri', Chargeout: 'Masraf Yansıtma',
  CargoCompensationSellerSatisfactionIncome: 'Kargo Tazmin Satıcı Memnuniyet Geliri', ReturnShipmentCostSharingExpense: 'Kargo Bedeli (İade Sipariş)',
  MarketingExpense: 'Pazarlama Bedeli', MarketingExpenseRefund: 'Pazarlama Bedeli İadesi', StudioExpense: 'Stüdyo Kullanım Bedeli',
  ReturnDeliveryProcessingFee: 'İşlem Bedeli (İade Sipariş)', PriceDifferenceExpense: 'Fiyat Farkı Bedeli', PriceDifferenceRefund: 'Fiyat Farkı İadesi',
  LateInterestExpense: 'Vade Farkı Gideri', CargoCostRefund: 'Kargo İadesi', RoadAssistanceExpenseRefund: 'Yol Yardım İadesi', RoadAssistanceExpense: 'Yol Yardım Bedeli',
  SponsorshipFee: 'Sponsorluk Bedeli', OverseasCommissionRefund: 'Yurtdışı Komisyon İadesi', MpScrapExpense: 'Hurda Gideri',
  MpCompensationIncome: 'Operasyon Tazmin Geliri', CargoCompensationExpenseRefund: 'Kargo Tazmin Gideri İadesi', VAT: 'KDV', BalanceCorrection: 'Düzeltme Kaydı',
  SfsInterest: 'TFS Vade Farkı', ProductsReportedByService: 'Servis Raporlu Ürünler', FacebookAdExpense: 'Facebook Reklam Gideri',
  CargoLimitExcessCompensationIncome: 'Kargo Limit Aşımı Tazmin Geliri', AdBalanceTopUpFromAllowance: 'Reklam Bakiye Yüklemesi',
  AdBalanceTopUpFromAllowanceRefund: 'Reklam Bakiye İadesi', EInvoiceSales: 'E-Fatura Satış Tutarı', EInvoiceSalesRefund: 'E-Fatura Satış Tutarı İadesi',
  MpOutletProductExpense: 'Outlet Gider Kalemi', BnplOrder: 'BNPL Sipariş Tutarı', BnplRefund: 'BNPL İade Tutarı', BnplProcessingFee: 'BNPL İşlem Ücreti',
  BnplProcessingFeeRefund: 'BNPL İşlem Ücreti İadesi', PaymentServiceCostReflection: 'Ödeme Servisi Maliyet Yansıtma',
  PaymentServiceCostReflectionRefund: 'Ödeme Servisi Maliyet Yansıtma İadesi', MarketingSupportParticipation: 'Pazarlama Destek Katılımı',
  MarketingSupportParticipationRefund: 'Pazarlama Destek Katılımı İadesi', Stoppage: 'Stopaj', StoppageRefund: 'Stopaj İadesi', TransportExpense: 'Taşıma Gideri',
  TransportExpenseRefund: 'Taşıma Gideri İadesi', ReturnProcessingFeeExpense: 'Hizmet Bedeli (İade Sipariş)', GoldLaborStoppage: 'Altın İşçilik Bedeli Stopajı',
  GoldLaborStoppageRefund: 'Altın İşçilik Bedeli Stopajı İadesi'
}

// Codes the other marketplaces may send (Trendyol's statement types, generic names).
const otherTransactionLabels: Record<string, string> = {
  Sale: 'Satış', Return: 'İade', Refund: 'İade', Discount: 'İndirim', Coupon: 'Kupon', Cargo: 'Kargo', Shipping: 'Kargo', ServiceFee: 'Hizmet Bedeli',
  Penalty: 'Ceza', Advertising: 'Reklam', CashAdvance: 'Erken Ödeme', PaymentOrder: 'Ödeme Emri', DeductionInvoices: 'Kesinti Faturası',
  DeductionInvoice: 'Kesinti Faturası', WireTransfer: 'Virman', IncomingTransfer: 'Gelen Havale', PayByLink: 'Linkle Satış', DeliveryFee: 'Teslimat Ücreti'
}

// n11 sends the day's state on its lines, not a type: a line is the order item's settlement, or its return.
const categoryLabels: Record<string, string> = { SALE: 'Satış Hakedişi', RETURN: 'İade Hakedişi' }

const reported = new Set<string>()

// A code (Latin letters only, no space) is mapped; a Turkish name is shown as sent.
export function transactionLabel(line: { platformCode: string; transactionType: string | null; category: string }) {
  if (line.platformCode === 'N11') return categoryLabels[line.category] ?? 'Hakediş'
  const type = line.transactionType?.trim()
  if (!type) return categoryLabels[line.category] ?? 'Kayıt'
  const label = (line.platformCode === 'HEPSIBURADA' ? hepsiburadaTransactionLabels[type] : undefined) ?? otherTransactionLabels[type]
  if (label) return label
  if (/^[A-Za-z]+$/.test(type) && !reported.has(type)) {
    reported.add(type)
    console.warn(`Hakediş: "${type}" (${line.platformCode}) için Türkçe ad yok; transaction-labels.ts'e eklenmeli.`)
  }
  return type
}
