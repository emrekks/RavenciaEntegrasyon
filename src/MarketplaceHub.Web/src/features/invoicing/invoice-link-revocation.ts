import type { StatusTone } from '../../shared/components'

// A marketplace link a cancelled invoice left on a package (MarketplaceLinkRevocationView). AUTOMATIC: the panel removes
// it (Trendyol); VERIFY_ONLY: the operator removes it in the seller panel and the panel checks (Hepsiburada); MANUAL: only
// instructions (n11, Pazarama).
export type InvoiceLinkRevocation = {
  deliveryStateId: string
  packageId: string | null
  platformCode: string
  platformName: string
  packageNumber: string | null
  status: string
  errorCode: string | null
  mode: string
  instructions: string
  canAct: boolean
  blockedReason: string | null
  updatedAt: string
}

const statusLabels: Record<string, string> = {
  REVOCATION_REQUIRED: 'Kaldırılmalı',
  REVOCATION_STARTED: 'Kaldırılıyor',
  REVOCATION_UNKNOWN: 'Sonuç Bekleniyor',
  LINK_REMOVED: 'Kaldırıldı',
}

export function isLinkRevocationStatus(status: string) {
  return status in statusLabels
}

export function linkRevocationStatusLabel(status: string) {
  return statusLabels[status] ?? status
}

export function linkRevocationTone(status: string): StatusTone {
  if (status === 'LINK_REMOVED') return 'success'
  if (status === 'REVOCATION_REQUIRED') return 'danger'
  return 'warning'
}

export function linkRevocationActionLabel(item: Pick<InvoiceLinkRevocation, 'mode' | 'status'>): string | null {
  if (item.status === 'LINK_REMOVED') return null
  if (item.mode === 'AUTOMATIC') return 'Fatura Linkini Kaldır'
  if (item.mode === 'VERIFY_ONLY') return 'Kaldırıldığını Kontrol Et'
  return null
}

export function linkRevocationNote(item: Pick<InvoiceLinkRevocation, 'status'>): string | null {
  if (item.status === 'REVOCATION_UNKNOWN') return 'Önceki istek sonuçlanmadı. Yeniden denediğinizde panel önce paketi okur; link zaten kalktıysa yeniden silme isteği gönderilmez.'
  if (item.status === 'REVOCATION_STARTED') return 'Kaldırma isteği sürüyor. Birkaç dakika sonra sayfayı yenileyin.'
  return null
}

export function linkRevocationConfirmCopy(item: InvoiceLinkRevocation, invoiceNumber: string | null) {
  const packageName = item.packageNumber ? `${item.packageNumber} numaralı paketteki` : 'paketteki'
  const invoice = invoiceNumber?.trim() ? `${invoiceNumber.trim()} numaralı faturanın` : 'faturanın'
  if (item.mode === 'AUTOMATIC') return {
    title: 'Fatura Linkini Kaldır',
    message: `${item.platformName} ${packageName} ${invoice} linki silinecek; müşteri bu faturayı pazaryerinde artık görmez. Panel önce paketi okur ve link yalnız bu faturaya aitse siler, sonra paketi yeniden okuyup linkin kalktığını doğrular. Bu işlem geri alınamaz.`,
    confirmLabel: 'Linki Kaldır',
    destructive: true,
  }
  return {
    title: 'Kaldırıldığını Kontrol Et',
    message: `${item.platformName} ${packageName.replace('paketteki', 'paket')} okunacak. Fatura pazaryerinde görünmüyorsa kayıt “Kaldırıldı” olarak kapanır. Pazaryerine hiçbir şey gönderilmez.`,
    confirmLabel: 'Kontrol Et',
    destructive: false,
  }
}

export function linkRevocationSuccessMessage(item: InvoiceLinkRevocation) {
  return item.mode === 'AUTOMATIC'
    ? `${item.platformName} paketindeki fatura linki kaldırıldı ve paket yeniden okunarak doğrulandı.`
    : `${item.platformName} paketinde fatura görünmüyor; kayıt kaldırıldı olarak kapandı.`
}
