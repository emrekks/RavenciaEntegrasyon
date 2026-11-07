import { appendNotification, productNotificationSubject } from '../../../shared/notifications'

// The product update's answer: the job, and the notices the server raised while queuing it. A product matched to a
// marketplace listing has a listing profile without values of its own; where the update took the product card's value
// instead (Teslimat Süresi, Garanti Süresi), the server says so and the panel keeps it in the notification center.
export type ProductUpdateAccepted = { jobId: string; notices?: string[] | null }

export function recordProductUpdateNotices(accepted: ProductUpdateAccepted | null | undefined, context: { productId: string; productName: string; modelCode?: string | null; platformCode?: string | null; connectionId: string }) {
  const notices = (accepted?.notices ?? []).map(notice => notice.trim()).filter(notice => notice.length > 0)
  for (const message of notices) {
    appendNotification({
      title: 'Ürün Kartındaki Değer Gönderildi',
      message,
      kind: 'info',
      record: true,
      operation: 'Platformlarda Güncelle',
      platformCode: context.platformCode ?? undefined,
      connectionId: context.connectionId,
      jobId: accepted?.jobId,
      subject: productNotificationSubject({ id: context.productId, name: context.productName, modelCode: context.modelCode })
    })
  }
  return notices.length
}
