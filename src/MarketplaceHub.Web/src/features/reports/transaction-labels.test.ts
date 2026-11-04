import { describe, expect, it, vi } from 'vitest'
import { hepsiburadaTransactionLabels, transactionLabel } from './transaction-labels'

// Every record type of Hepsiburada's muhasebe guide ("Kayıt Tipi Bilgileri", 4 Nov 2026), the category names left out.
const documented = ['Payment', 'DeliveryProcessingFee', 'DeliveryProcessingFeeRefund', 'Return', 'Commission', 'CommissionInvoiceRefund', 'CommissionRefund',
  'CommissionCorrection', 'ShipmentCostSharingExpense', 'ShipmentCostSharingIncome', 'ProcessingFeeExpense', 'ProcessingFeeExpenseRefund', 'CustomerSatisfaction',
  'CustomerSatisfactionRefund', 'LineItemTransferExpense', 'LineItemTransferIncome', 'CampaignDiscount', 'CampaignDiscountRefund', 'RevenueExpense', 'RevenueIncome',
  'CargoMargin', 'CargoCompensationIncomeRefund', 'MpScrapIncome', 'RefusedInvoiceExpenseRefund', 'RefusedInvoiceExpense', 'Deposit', 'AdSharingExpense',
  'AdSharingExpenseRefund', 'DropShipmentCostSharingExpense', 'DropShipmentCostSharingIncome', 'Chargeout', 'CargoCompensationSellerSatisfactionIncome',
  'ReturnShipmentCostSharingExpense', 'MarketingExpense', 'MarketingExpenseRefund', 'StudioExpense', 'ReturnDeliveryProcessingFee', 'PriceDifferenceExpense',
  'PriceDifferenceRefund', 'LateInterestExpense', 'CargoCostRefund', 'RoadAssistanceExpenseRefund', 'RoadAssistanceExpense', 'SponsorshipFee',
  'OverseasCommissionRefund', 'MpScrapExpense', 'MpCompensationIncome', 'CargoCompensationExpenseRefund', 'VAT', 'BalanceCorrection', 'SfsInterest',
  'ProductsReportedByService', 'FacebookAdExpense', 'CargoLimitExcessCompensationIncome', 'AdBalanceTopUpFromAllowance', 'EInvoiceSalesRefund', 'EInvoiceSales',
  'BnplOrder', 'BnplRefund', 'MpOutletProductExpense', 'AdBalanceTopUpFromAllowanceRefund', 'PaymentServiceCostReflection', 'PaymentServiceCostReflectionRefund',
  'MarketingSupportParticipation', 'Stoppage', 'StoppageRefund', 'TransportExpense', 'TransportExpenseRefund', 'BnplProcessingFee', 'BnplProcessingFeeRefund',
  'MarketingSupportParticipationRefund', 'ReturnProcessingFeeExpense', 'GoldLaborStoppage', 'GoldLaborStoppageRefund']

describe('payout record names', () => {
  it('name every documented Hepsiburada type in Turkish', () => {
    expect(documented.filter(type => !hepsiburadaTransactionLabels[type])).toEqual([])
    expect(transactionLabel({ platformCode: 'HEPSIBURADA', transactionType: 'Payment', category: 'SALE' })).toBe('Satış Ödemesi')
    expect(transactionLabel({ platformCode: 'HEPSIBURADA', transactionType: 'Stoppage', category: 'STOPPAGE' })).toBe('Stopaj')
  })

  it('keep the Turkish names Trendyol and Pazarama send and name n11 lines by what they are', () => {
    expect(transactionLabel({ platformCode: 'TRENDYOL', transactionType: 'Satış', category: 'SALE' })).toBe('Satış')
    expect(transactionLabel({ platformCode: 'PAZARAMA', transactionType: 'Hakediş Ödemesi Bekliyor', category: 'SALE' })).toBe('Hakediş Ödemesi Bekliyor')
    expect(transactionLabel({ platformCode: 'N11', transactionType: 'ODEME_BEKLENIYOR', category: 'RETURN' })).toBe('İade Hakedişi')
  })

  it('show an unknown code as sent and report it once', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    expect(transactionLabel({ platformCode: 'HEPSIBURADA', transactionType: 'SomethingNew', category: 'OTHER' })).toBe('SomethingNew')
    transactionLabel({ platformCode: 'HEPSIBURADA', transactionType: 'SomethingNew', category: 'OTHER' })
    expect(warn).toHaveBeenCalledTimes(1)
    warn.mockRestore()
  })
})
