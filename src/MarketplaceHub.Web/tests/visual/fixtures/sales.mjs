// Sales screens: the dashboard (revenue series, integration matrix, product summary), orders (list, summary, order
// detail, shipping-label settings) and returns (list, detail, rejection reasons). Fixed data only: every timestamp sits
// around the frozen clock (2026-10-20 09:30 Istanbul) and every "random" value comes from a hash of fixed text.

// "2026-10-19 14:22" Istanbul time -> UTC ISO string (Istanbul is UTC+3 all year).
export const ist = value => new Date(`${value.replace(' ', 'T')}:00+03:00`).toISOString()

// A same-origin picture that exists under public/: product photos are not part of the fixture set.
const photo = '/pack/brand/ravencia-symbol-256.webp'

// Cargo names the panel shows as text: the carriers with a logo (Yurtiçi, Aras, Trendyol Express, hepsiJET…) load it
// from an outside CDN, which would make the captures depend on the network.
const MNG = 'MNG Kargo Marketplace', UPS = 'UPS Kargo', SENDEO = 'Sendeo', KARGOIST = 'Kargoist', GELIVER = 'Geliver'

const platformNames = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify', N11: 'N11', PAZARAMA: 'Pazarama' }
const connectionIds = { TRENDYOL: 'c-ty', HEPSIBURADA: 'c-hb', SHOPIFY: 'c-sh', N11: 'c-n11', PAZARAMA: 'c-pz' }

// ---------------------------------------------------------------------------------------------------------- orders

const line = (id, { sku, barcode, title, qty = 1, price, vat = 10, raw = 'Created', model = null, options = null, image = null, shipped = 0, delivered = 0, cancelled = 0, returned = 0 }) => ({
  id, sku, barcode, title, quantity: qty, orderedQuantity: qty, cancelledQuantity: cancelled, shippedQuantity: shipped, allocatedQuantity: 0,
  deliveredQuantity: delivered, returnedQuantity: returned, unitPrice: price, vatRate: vat, rawStatus: raw, variantId: `var-${id}`,
  modelCode: model, optionSignature: options, imageUrl: image
})

const address = (fullName, address1, neighborhood, district, city, postalCode, countryCode = 'TR') => ({
  fullName, fullAddress: `${address1} ${neighborhood} ${district}/${city}`, address1, address2: null, neighborhood, district, city, postalCode,
  countryCode, isDeliveryPoint: false, deliveryPointType: null
})

function order({ id, platformCode, orderNumber, status, orderedAt, dueAt = null, shippedAt = null, customer, email = null, taxId = null, shipTo, lines, packages = [], invoiceStatus, invoiceId = null, invoiceDocumentUrl = null, discount = 0, microExport = false, labelPrintedAt = null, platformInfo = null, phone = null }) {
  const grossAmount = Math.round(lines.reduce((sum, item) => sum + item.unitPrice * item.orderedQuantity, 0) * 100) / 100
  const shipments = packages.map((item, index) => ({
    id: `${id}-pkg${index + 1}`, orderId: id, orderNumber, externalPackageId: item.externalPackageId, status: item.status, rawStatus: item.rawStatus,
    cargoTrackingNumber: item.tracking ?? null, cargoProviderName: item.cargo ?? null, statusOccurredAt: ist(item.at), version: 2 + index, isResend: false
  }))
  return {
    id, labelPrintedAt: labelPrintedAt ? ist(labelPrintedAt) : null, orderNumber, derivedStatus: status, currency: 'TRY',
    grossAmount, discountAmount: discount, netAmount: Math.round((grossAmount - discount) * 100) / 100,
    orderedAt: ist(orderedAt), shippedAt: shippedAt ? ist(shippedAt) : null, lineCount: lines.length, packageCount: shipments.length, version: 4,
    connectionId: connectionIds[platformCode], platformCode, platformDisplayName: platformNames[platformCode], customerName: customer, customerEmail: email,
    customerTaxOrIdentityNumber: taxId, orderType: taxId ? 'CORPORATE' : 'INDIVIDUAL', isMicroExport: microExport, shipmentAddress: shipTo,
    shipmentDueAt: dueAt ? ist(dueAt) : null, isDeadlineCritical: false, invoiceStatus, invoiceId, invoiceDocumentUrl,
    cargoProviderName: shipments[0]?.cargoProviderName ?? null, cargoTrackingNumber: shipments[0]?.cargoTrackingNumber ?? null,
    primaryImageUrl: lines.find(item => item.imageUrl)?.imageUrl ?? null,
    productQuantity: lines.reduce((sum, item) => sum + item.orderedQuantity - item.cancelledQuantity, 0),
    lines, packages: shipments, platformInfo, customerPhone: phone
  }
}

export const orders = [
  order({
    id: 'o-1001', platformCode: 'TRENDYOL', orderNumber: '10458213377', status: 'NEW', orderedAt: '2026-10-20 08:47', dueAt: '2026-10-21 23:59',
    customer: 'Elif Demir', phone: '+90 532 418 26 90', shipTo: address('Elif Demir', 'Caferağa Mah. Moda Cad. No: 41 D: 6', 'Caferağa', 'Kadıköy', 'İstanbul', '34710'),
    lines: [
      line('o-1001-l1', { sku: 'RVK-TRK-2241-EKR-M', barcode: '8684471220419', title: 'Kadın Oversize Triko Kazak', price: 649.9, model: 'RVK-TRK-2241', options: 'Renk: Ekru | Beden: M', image: photo }),
      line('o-1001-l2', { sku: 'RVK-JN-1180-AMV-38', barcode: '8684471219987', title: 'Yüksek Bel Mom Jean', price: 799.9, model: 'RVK-JN-1180', options: 'Renk: Açık Mavi | Beden: 38' }),
    ],
    packages: [{ externalPackageId: '3189204417', status: 'NEW', rawStatus: 'Created', cargo: MNG, tracking: '7330029117624185', at: '2026-10-20 08:47' }],
    invoiceStatus: 'FATURA_BEKLIYOR'
  }),
  order({
    id: 'o-1002', platformCode: 'HEPSIBURADA', orderNumber: '4290571836', status: 'NEW', orderedAt: '2026-10-19 21:15', dueAt: '2026-10-20 18:00',
    customer: 'Mehmet Kaya', shipTo: address('Mehmet Kaya', 'Kızılırmak Mah. 1443. Cad. No: 25 D: 12', 'Kızılırmak', 'Çankaya', 'Ankara', '06510'),
    lines: [line('o-1002-l1', { sku: 'RVE-GML-0932-LCV-L', barcode: '0RVE09320L', title: 'Erkek Slim Fit Keten Gömlek', qty: 2, price: 459.9, raw: 'Open', model: 'RVE-GML-0932', options: 'Renk: Lacivert | Beden: L' })],
    invoiceStatus: 'FATURA_BEKLIYOR', discount: 50
  }),
  order({
    id: 'o-1003', platformCode: 'SHOPIFY', orderNumber: '#1048', status: 'PROCESSING', orderedAt: '2026-10-19 16:02', dueAt: '2026-10-22 18:00',
    customer: 'Zeynep Arslan', email: 'zeynep.arslan@example.com', phone: '+90 555 301 77 12',
    shipTo: address('Zeynep Arslan', 'Bostanlı Mah. Cemal Gürsel Cad. No: 318 D: 4', 'Bostanlı', 'Karşıyaka', 'İzmir', '35590'),
    lines: [
      line('o-1003-l1', { sku: 'RVA-CNT-0457-TAB', barcode: '8684471230548', title: 'Deri Görünümlü Çapraz Çanta', price: 1249, vat: 20, raw: 'unfulfilled', model: 'RVA-CNT-0457', options: 'Renk: Taba', image: photo }),
      line('o-1003-l2', { sku: 'RVA-FLR-0112-BRD', barcode: '8684471230920', title: 'Saten Fular', qty: 2, price: 189, vat: 20, raw: 'unfulfilled', model: 'RVA-FLR-0112', options: 'Renk: Bordo' }),
    ],
    packages: [{ externalPackageId: 'shopify-fo-6021458', status: 'PROCESSING', rawStatus: 'unfulfilled', cargo: GELIVER, tracking: null, at: '2026-10-19 16:05' }],
    invoiceStatus: 'FATURA_BEKLIYOR'
  }),
  order({
    id: 'o-1004', platformCode: 'TRENDYOL', orderNumber: '10451877206', status: 'PROCESSING', orderedAt: '2026-10-16 13:40', dueAt: '2026-10-18 08:00',
    customer: 'Ayşe Çelik', shipTo: address('Ayşe Çelik', 'Ataevler Mah. Mavi Cad. No: 9 D: 3', 'Ataevler', 'Nilüfer', 'Bursa', '16140'),
    lines: [line('o-1004-l1', { sku: 'RVK-TST-0310-SYH-S', barcode: '8684471201104', title: 'Kadın Basic Bisiklet Yaka Tişört', qty: 3, price: 199.9, raw: 'Picking', model: 'RVK-TST-0310', options: 'Renk: Siyah | Beden: S' })],
    packages: [{ externalPackageId: '3188615092', status: 'PROCESSING', rawStatus: 'Picking', cargo: MNG, tracking: '7330029117555302', at: '2026-10-17 09:12' }],
    invoiceStatus: 'FATURA_ISLENIYOR', labelPrintedAt: '2026-10-17 09:20'
  }),
  order({
    id: 'o-1005', platformCode: 'N11', orderNumber: '211698830412', status: 'SHIPPED', orderedAt: '2026-10-17 10:20', dueAt: '2026-10-19 10:20', shippedAt: '2026-10-18 15:05',
    customer: 'Burak Şahin', shipTo: address('Burak Şahin', 'Fener Mah. Tekelioğlu Cad. No: 52 D: 8', 'Fener', 'Muratpaşa', 'Antalya', '07160'),
    lines: [line('o-1005-l1', { sku: 'RVU-SWT-0705-GRI-XL', barcode: '8684471212203', title: 'Unisex Kapüşonlu Sweatshirt', price: 549, raw: 'Shipped', model: 'RVU-SWT-0705', options: 'Renk: Gri Melanj | Beden: XL', shipped: 1, image: photo })],
    packages: [{ externalPackageId: '50419978321', status: 'SHIPPED', rawStatus: 'Shipped', cargo: UPS, tracking: '1Z84E9W60412257731', at: '2026-10-18 15:05' }],
    invoiceStatus: 'FATURA_KONTROLDE', invoiceId: 'inv-1005', platformInfo: [{ label: 'n11 Sipariş Durumu', value: 'Kargoya Verildi' }]
  }),
  order({
    id: 'o-1006', platformCode: 'PAZARAMA', orderNumber: 'PZ-58120734', status: 'SHIPPED', orderedAt: '2026-10-17 19:44', dueAt: '2026-10-19 19:44', shippedAt: '2026-10-19 11:30',
    customer: 'Selin Yıldız', shipTo: address('Selin Yıldız', 'Hoşnudiye Mah. Ismet İnönü 1 Cad. No: 77 D: 2', 'Hoşnudiye', 'Tepebaşı', 'Eskişehir', '26130'),
    lines: [line('o-1006-l1', { sku: 'RVC-PJM-0218-LCV-78', barcode: '8684471240776', title: 'Çocuk Desenli Pijama Takımı', qty: 2, price: 279.9, raw: 'Kargoya Verildi', model: 'RVC-PJM-0218', options: 'Renk: Lacivert | Beden: 7-8 Yaş', shipped: 2 })],
    packages: [{ externalPackageId: 'PZP-90331842', status: 'SHIPPED', rawStatus: 'Kargoya Verildi', cargo: KARGOIST, tracking: 'KGI40961188', at: '2026-10-19 11:30' }],
    invoiceStatus: 'FATURA_PLATFORMA_AKTARILMADI', invoiceId: 'inv-1006'
  }),
  order({
    id: 'o-1007', platformCode: 'TRENDYOL', orderNumber: '10437702915', status: 'DELIVERED', orderedAt: '2026-10-13 09:12', dueAt: '2026-10-15 09:12', shippedAt: '2026-10-14 14:20',
    customer: 'Lena Hoffmann', microExport: true, shipTo: address('Lena Hoffmann', 'Kastanienallee 12', 'Prenzlauer Berg', 'Pankow', 'Berlin', '10435', 'DE'),
    lines: [line('o-1007-l1', { sku: 'RVK-PNT-0544-BEJ-40', barcode: '8684471225445', title: 'Keten Karışımlı Palazzo Pantolon', price: 899, raw: 'Delivered', model: 'RVK-PNT-0544', options: 'Renk: Bej | Beden: 40', shipped: 1, delivered: 1, image: photo })],
    packages: [{ externalPackageId: '3186120774', status: 'DELIVERED', rawStatus: 'Delivered', cargo: MNG, tracking: '7330029116987703', at: '2026-10-16 12:47' }],
    invoiceStatus: 'FATURA_KONTROLDE', invoiceId: 'inv-1007'
  }),
  order({
    id: 'o-1008', platformCode: 'HEPSIBURADA', orderNumber: '4288140027', status: 'DELIVERED', orderedAt: '2026-10-12 22:31', dueAt: '2026-10-14 22:31', shippedAt: '2026-10-13 16:00',
    customer: 'Can Öztürk', taxId: '4810573926', shipTo: address('Can Öztürk', 'Sinanpaşa Mah. Ihlamurdere Cad. No: 18 D: 5', 'Sinanpaşa', 'Beşiktaş', 'İstanbul', '34353'),
    lines: [
      line('o-1008-l1', { sku: 'RVE-PNT-0811-HAK-32', barcode: '8684471218812', title: 'Erkek Chino Pantolon', price: 699.9, raw: 'Delivered', model: 'RVE-PNT-0811', options: 'Renk: Haki | Beden: 32', shipped: 1, delivered: 1 }),
      line('o-1008-l2', { sku: 'RVA-BRE-0021-ANT', barcode: '8684471231001', title: 'Örme Bere', price: 149.9, raw: 'Delivered', model: 'RVA-BRE-0021', options: 'Renk: Antrasit', shipped: 1, delivered: 1 }),
    ],
    packages: [{ externalPackageId: '5435694424', status: 'DELIVERED', rawStatus: 'Delivered', cargo: SENDEO, tracking: 'SND2026101300417', at: '2026-10-15 10:05' }],
    invoiceStatus: 'FATURA_KONTROLDE', invoiceId: 'inv-1008', discount: 85
  }),
  order({
    id: 'o-1009', platformCode: 'SHOPIFY', orderNumber: '#1041', status: 'DELIVERED', orderedAt: '2026-10-11 18:06', shippedAt: '2026-10-12 12:40',
    customer: 'Deniz Aydın', email: 'deniz.aydin@example.com', shipTo: address('Deniz Aydın', 'Kuzguncuk Mah. İcadiye Cad. No: 63', 'Kuzguncuk', 'Üsküdar', 'İstanbul', '34674'),
    lines: [line('o-1009-l1', { sku: 'RVK-KBN-0090-ANT-M', barcode: '8684471209902', title: 'Yün Karışımlı Kaşe Kaban', price: 2899, vat: 20, raw: 'fulfilled', model: 'RVK-KBN-0090', options: 'Renk: Antrasit | Beden: M', shipped: 1, delivered: 1, image: photo })],
    packages: [{ externalPackageId: 'shopify-fo-6019932', status: 'DELIVERED', rawStatus: 'delivered', cargo: GELIVER, tracking: 'GLV20261012008', at: '2026-10-14 13:22' }],
    invoiceStatus: 'FATURA_YUKLENDI', invoiceId: 'inv-1009', invoiceDocumentUrl: '/api/v1/invoices/inv-1009/documents/latest/content'
  }),
  order({
    id: 'o-1010', platformCode: 'TRENDYOL', orderNumber: '10449930188', status: 'CANCELLED', orderedAt: '2026-10-15 11:58', dueAt: '2026-10-17 11:58',
    customer: 'Emre Koç', shipTo: address('Emre Koç', 'Kozluk Mah. Ankara Yolu Cad. No: 104 D: 9', 'Kozluk', 'İzmit', 'Kocaeli', '41040'),
    lines: [line('o-1010-l1', { sku: 'RVK-TRC-0377-BEJ-38', barcode: '8684471203771', title: 'Kadın Kemerli Trençkot', price: 1599.9, raw: 'Cancelled', model: 'RVK-TRC-0377', options: 'Renk: Bej | Beden: 38', cancelled: 1 })],
    packages: [{ externalPackageId: '3187530661', status: 'CANCELLED', rawStatus: 'Cancelled', cargo: MNG, tracking: null, at: '2026-10-15 16:20' }],
    invoiceStatus: 'FATURA_IPTAL'
  }),
]

// The status tabs read their counts from the summary; they agree with the list above (per platform too).
const summaryKeys = { NEW: 'new', PROCESSING: 'processing', READY_TO_SHIP: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview', UNVERIFIED: 'unverified', PAYMENT_PENDING: 'paymentPending' }
function summarize(platform) {
  const counts = { all: 0, new: 0, processing: 0, shipped: 0, delivered: 0, resent: 0, onHold: 0, cancelled: 0, returned: 0, returnInTransit: 0, partiallyCancelled: 0, manualReview: 0, pending: 0, unverified: 0, paymentPending: 0 }
  for (const item of orders) {
    if (platform && item.platformCode !== platform.toUpperCase()) continue
    counts.all += 1
    counts[summaryKeys[item.derivedStatus]] += 1
  }
  counts.pending = counts.new
  return counts
}
export const orderSummary = { ...summarize(null), forPlatform: platform => summarize(platform) }

const orderStatusFilter = { PROCESSING: ['PROCESSING', 'READY_TO_SHIP'] }
function orderPage({ url }) {
  const query = url.searchParams
  const status = query.get('status')
  const platforms = (query.get('platforms') ?? query.get('platform') ?? '').split(',').map(value => value.trim().toUpperCase()).filter(Boolean)
  const search = (query.get('search') ?? '').toLocaleLowerCase('tr-TR')
  const labelPrinted = query.get('labelPrinted')
  let items = orders.filter(item =>
    (!status || (orderStatusFilter[status] ?? [status]).includes(item.derivedStatus))
    && (!platforms.length || platforms.includes(item.platformCode))
    && (!search || [item.orderNumber, item.customerName, ...item.lines.flatMap(entry => [entry.title, entry.sku, entry.barcode])].some(value => value?.toLocaleLowerCase('tr-TR').includes(search)))
    && (labelPrinted === null || (labelPrinted === 'true') === Boolean(item.labelPrintedAt)))
  const sort = query.get('sort') ?? 'DATE_DESC'
  const key = sort.startsWith('DUE') ? 'shipmentDueAt' : sort.startsWith('SHIP') ? 'shippedAt' : 'orderedAt'
  items = [...items].sort((left, right) => (left[key] ?? '').localeCompare(right[key] ?? '') * (sort.endsWith('ASC') ? 1 : -1) || left.id.localeCompare(right.id))
  return { items, nextCursor: null, hasMore: false, totalCount: items.length, supportedFilters: ['labelPrinted'] }
}

// GET /orders/{id}: the detail every invoice window reads (addresses as the stored JSON snapshots).
function orderDetail({ match }) {
  const item = orders.find(entry => entry.id === match[1]) ?? orders[2]
  const shipTo = item.shipmentAddress
  const snapshot = JSON.stringify({ firstName: shipTo.fullName.split(' ')[0], lastName: shipTo.fullName.split(' ').slice(1).join(' '), fullName: shipTo.fullName, fullAddress: shipTo.fullAddress, address1: shipTo.address1, neighborhood: shipTo.neighborhood, district: shipTo.district, city: shipTo.city, postalCode: shipTo.postalCode, countryCode: shipTo.countryCode })
  return {
    id: item.id, orderNumber: item.orderNumber, derivedStatus: item.derivedStatus, currency: item.currency, grossAmount: item.grossAmount, discountAmount: item.discountAmount,
    netAmount: item.netAmount, orderedAt: item.orderedAt, connectionId: item.connectionId, platformCode: item.platformCode, platformDisplayName: item.platformDisplayName,
    customerName: item.customerName, customerEmail: item.customerEmail, customerPhone: item.customerPhone, customerTaxOrIdentityNumber: item.customerTaxOrIdentityNumber,
    orderType: item.orderType, isMicroExport: item.isMicroExport, isEInvoiceAvailable: item.customerTaxOrIdentityNumber ? true : item.platformCode === 'SHOPIFY' ? null : false,
    shipmentAddressJson: snapshot, invoiceAddressJson: snapshot, shipmentDueAt: item.shipmentDueAt, invoiceStatus: item.invoiceStatus,
    invoiceDocumentUrl: item.invoiceDocumentUrl, lines: item.lines, packages: item.packages, version: item.version
  }
}

// ---------------------------------------------------------------------------------------------------------- returns

function claim({ id, platformCode, externalClaimId, orderNumber, status, rawStatus, reasonCode = null, reasonText = null, actionDueAt = null, approvedAt = null, customer, orderedAt, cargo = null, tracking = null, orderCargo = null, orderTracking = null, packageNumber, invoiceStatus = 'FATURA_KONTROLDE', lines, discount = 0, lineStatuses = null, estimated = false, sellerDescription = null }) {
  const grossAmount = Math.round(lines.reduce((sum, item) => sum + item.unitPrice * item.orderedQuantity, 0) * 100) / 100
  return {
    id, externalClaimId, orderNumber, status, rawStatus, reasonText, reasonCode, actionDueAt: actionDueAt ? ist(actionDueAt) : null, approvedAt: approvedAt ? ist(approvedAt) : null,
    systemNote: null, sellerDescription, platformApprovalReason: null, platformDescription: null, version: 3, customerName: customer, orderedAt: ist(orderedAt),
    orderAmount: Math.round((grossAmount - discount) * 100) / 100, currency: 'TRY', cargoProviderName: cargo, cargoTrackingNumber: tracking, cargoTrackingLink: null,
    orderCargoProviderName: orderCargo, orderCargoTrackingNumber: orderTracking, primaryImageUrl: lines.find(item => item.imageUrl)?.imageUrl ?? null,
    productCount: lines.length, primaryBarcode: lines[0]?.barcode ?? null, lines, packageNumber, invoiceStatus, grossAmount, discountAmount: discount, isMicroExport: false,
    connectionId: connectionIds[platformCode], platformCode, platformDisplayName: platformNames[platformCode], lineStatuses, actionDueAtEstimated: estimated
  }
}

export const returns = [
  claim({
    id: 'r-2001', platformCode: 'TRENDYOL', externalClaimId: 'f3a9c1e2-7d44-4b8a-9a51-2c6e0d9b1a77', orderNumber: '10421188530', status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction',
    reasonCode: 'SIZE_TOO_SMALL', reasonText: 'Normalde M giyiyorum ama kalıbı dar, bir beden büyüğünü alacağım.', actionDueAt: '2026-10-21 08:00',
    customer: 'Gizem Aksoy', orderedAt: '2026-10-08 14:10', cargo: MNG, tracking: '7330029101188452', packageNumber: '3187741290',
    lines: [line('r-2001-l1', { sku: 'RVK-ELB-0661-YSL-M', barcode: '8684471266617', title: 'Kadın Saten Midi Elbise', price: 749.9, raw: 'WaitingInAction', model: 'RVK-ELB-0661', options: 'Renk: Yeşil | Beden: M', image: photo })]
  }),
  claim({
    id: 'r-2002', platformCode: 'HEPSIBURADA', externalClaimId: 'HBR-77120458', orderNumber: '4281057719', status: 'ACTION_REQUIRED', rawStatus: 'AwaitingPreApproval',
    reasonCode: 'Ürünü almaktan vazgeçtim / beğenmedim', reasonText: 'Rengi fotoğraftakinden daha koyu geldi.', actionDueAt: '2026-10-23 17:00', estimated: true,
    customer: 'Oğuzhan Polat', orderedAt: '2026-10-09 20:44', cargo: SENDEO, tracking: 'SND2026101700981', packageNumber: '5435120087', discount: 40,
    lines: [line('r-2002-l1', { sku: 'RVE-MNT-0148-HAK-L', barcode: '0RVE01480L', title: 'Erkek Şişme Mont', price: 1899.9, vat: 20, raw: 'AwaitingPreApproval', model: 'RVE-MNT-0148', options: 'Renk: Haki | Beden: L' })]
  }),
  claim({
    id: 'r-2003', platformCode: 'TRENDYOL', externalClaimId: '0b6e2d91-4c3f-4f6a-8e27-91d5a3c7e410', orderNumber: '10419920671', status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction',
    reasonCode: 'PRODUCT_IS_DAMAGED', reasonText: 'Bir tanesinin dikişi sökük geldi.', actionDueAt: '2026-10-22 12:00', approvedAt: '2026-10-19 10:05',
    customer: 'Hakan Yurt', orderedAt: '2026-10-06 11:32', cargo: MNG, tracking: '7330029100954118', packageNumber: '3187250134',
    lines: [
      line('r-2003-l1', { sku: 'RVE-TST-0402-BYZ-L', barcode: '8684471204020', title: 'Erkek Polo Yaka Tişört', price: 329.9, raw: 'Accepted', model: 'RVE-TST-0402', options: 'Renk: Beyaz | Beden: L' }),
      line('r-2003-l2', { sku: 'RVE-TST-0402-LCV-L', barcode: '8684471204037', title: 'Erkek Polo Yaka Tişört', price: 329.9, raw: 'WaitingInAction', model: 'RVE-TST-0402', options: 'Renk: Lacivert | Beden: L' }),
    ],
    lineStatuses: { 'r-2003-l1': 'APPROVED', 'r-2003-l2': 'ACTION_REQUIRED' }
  }),
  claim({
    id: 'r-2004', platformCode: 'N11', externalClaimId: 'N11-IAD-3390715', orderNumber: '211690044817', status: 'REQUESTED', rawStatus: 'Requested',
    reasonCode: 'ProductIsDamaged', reasonText: 'Kutusu ezik geldi, sapı kırık.', customer: 'Tuğba Erdem', orderedAt: '2026-10-14 08:55',
    orderCargo: UPS, orderTracking: '1Z84E9W60409981120', packageNumber: '50419012655', invoiceStatus: 'FATURA_BEKLIYOR',
    lines: [line('r-2004-l1', { sku: 'RVA-CNT-0461-SYH', barcode: '8684471230616', title: 'Hasır Plaj Çantası', price: 429, raw: 'Requested', model: 'RVA-CNT-0461', options: 'Renk: Siyah' })]
  }),
  claim({
    id: 'r-2005', platformCode: 'PAZARAMA', externalClaimId: 'PZR-1188420', orderNumber: 'PZ-57998410', status: 'IN_TRANSIT', rawStatus: 'InTransit',
    reasonCode: 'WrongProductSentByMerchant', reasonText: null, customer: 'Murat Güneş', orderedAt: '2026-10-10 17:20',
    cargo: KARGOIST, tracking: 'KGI40955021', packageNumber: 'PZP-90288170',
    lines: [line('r-2005-l1', { sku: 'RVC-MNT-0033-KRM-56', barcode: '8684471240332', title: 'Çocuk Kapüşonlu Yağmurluk', price: 489.9, raw: 'InTransit', model: 'RVC-MNT-0033', options: 'Renk: Kırmızı | Beden: 5-6 Yaş', image: photo })]
  }),
  claim({
    id: 'r-2006', platformCode: 'TRENDYOL', externalClaimId: '5d2f8a17-0e93-4d1b-b6c4-7a8e2f9c3b05', orderNumber: '10402263394', status: 'APPROVED', rawStatus: 'Accepted',
    reasonCode: 'POOR_QUALITY', reasonText: 'Bir yıkamada tüylendi.', approvedAt: '2026-10-17 15:42', customer: 'Esra Koçak', orderedAt: '2026-10-01 10:02',
    cargo: MNG, tracking: '7330029099874310', packageNumber: '3186099821',
    lines: [line('r-2006-l1', { sku: 'RVK-HRK-0275-PMB-S', barcode: '8684471227753', title: 'Kadın Örgü Hırka', price: 579.9, raw: 'Accepted', model: 'RVK-HRK-0275', options: 'Renk: Pembe | Beden: S' })]
  }),
  claim({
    id: 'r-2007', platformCode: 'HEPSIBURADA', externalClaimId: 'HBR-77098311', orderNumber: '4279912048', status: 'REJECTED', rawStatus: 'Rejected',
    reasonCode: 'Ürünüm hasarlı geldi', reasonText: 'Fermuar bozuk.', customer: 'Kerem Aslan', orderedAt: '2026-10-03 13:47',
    cargo: SENDEO, tracking: 'SND2026101800233', packageNumber: '5434988102', sellerDescription: 'Ürün kullanılmış ve etiketi sökülmüş olarak geldi.',
    lines: [line('r-2007-l1', { sku: 'RVE-CNT-0333-SYH', barcode: '0RVE03330', title: 'Erkek Sırt Çantası', price: 899.9, vat: 20, raw: 'Rejected', model: 'RVE-CNT-0333', options: 'Renk: Siyah' })]
  }),
  claim({
    id: 'r-2008', platformCode: 'TRENDYOL', externalClaimId: '9c41b7e3-2a6d-4f08-8d15-e3b0a6c94f21', orderNumber: '10398750126', status: 'DISPUTED', rawStatus: 'Unresolved',
    reasonCode: 'OTHER', reasonText: 'Ürün beklediğim gibi değil.', customer: 'Nazlı Tekin', orderedAt: '2026-09-28 16:15',
    cargo: MNG, tracking: '7330029098120775', packageNumber: '3185520716', sellerDescription: 'Ürün kullanılmış olarak geri gönderildi; itiraz edildi.',
    lines: [line('r-2008-l1', { sku: 'RVK-BOT-0820-SYH-38', barcode: '8684471208202', title: 'Kadın Bilekte Deri Bot', price: 1349.9, vat: 20, raw: 'Unresolved', model: 'RVK-BOT-0820', options: 'Renk: Siyah | Beden: 38' })]
  }),
]

// The server filters by the same groups as the page's tabs; a partly accepted claim belongs to both of its groups.
function returnGroup(status) {
  const value = status.toUpperCase()
  if (['APPROVED', 'COMPLETED'].includes(value)) return 'APPROVED'
  if (['REJECTED', 'CANCELLED'].includes(value)) return 'REJECTED'
  if (['REQUESTED', 'CREATED'].includes(value)) return 'REQUESTED'
  if (['ACTION_REQUIRED', 'WAITING_FOR_SELLER_ACTION'].includes(value)) return 'ACTION_REQUIRED'
  if (['WAITING_FOR_SHIPMENT', 'IN_TRANSIT', 'RETURN_IN_TRANSIT', 'SHIPPED'].includes(value)) return 'SHIPPING'
  if (value === 'DISPUTED') return 'DISPUTED'
  if (['SUSPENDED', 'ON_HOLD'].includes(value)) return 'SUSPENDED'
  return 'REVIEW'
}
function returnPage({ url }) {
  const status = url.searchParams.get('status')
  const search = (url.searchParams.get('search') ?? '').toLocaleLowerCase('tr-TR')
  const items = returns.filter(item =>
    (!status || returnGroup(item.status) === status || Object.values(item.lineStatuses ?? {}).some(value => returnGroup(value) === status))
    && (!search || [item.customerName, item.orderNumber, item.externalClaimId, ...item.lines.flatMap(entry => [entry.title, entry.sku, entry.barcode])].some(value => value?.toLocaleLowerCase('tr-TR').includes(search))))
  return { items, nextCursor: null, hasMore: false, totalCount: items.length }
}

function returnDetail({ match }) {
  const item = returns.find(entry => entry.id === match[1]) ?? returns[0]
  const waiting = returnGroup(item.status) === 'ACTION_REQUIRED'
  const [color, size] = ['Renk', 'Beden']
  return {
    ...item,
    allowedActions: waiting ? (item.rawStatus === 'AwaitingPreApproval' ? ['APPROVE', 'REJECT', 'PREAPPROVAL_CONFIRM'] : ['APPROVE', 'REJECT']) : [],
    stockDispositionAvailable: returnGroup(item.status) === 'APPROVED', externalWritesEnabled: true, decisionPending: false,
    lines: item.lines.map((entry, index) => {
      const options = Object.fromEntries((entry.optionSignature ?? '').split('|').map(part => part.split(':').map(value => value.trim())).filter(pair => pair.length === 2))
      const lineStatus = item.lineStatuses?.[entry.id] ?? (waiting ? 'ACTION_REQUIRED' : item.status)
      return {
        status: lineStatus, id: entry.id, externalLineId: `${item.externalClaimId.slice(0, 8)}-${index + 1}`, orderLineId: `${entry.id}-order`, sku: entry.sku, barcode: entry.barcode,
        title: entry.title, quantity: entry.orderedQuantity, disposedQuantity: 0, remainingQuantity: lineStatus === 'ACTION_REQUIRED' ? entry.orderedQuantity : 0,
        unitPrice: entry.unitPrice, imageUrl: entry.imageUrl, hasInventoryMapping: index === 0, optionSignature: entry.optionSignature, modelCode: entry.modelCode,
        color: options[color] ?? null, size: options[size] ?? null, approvedAt: lineStatus === 'APPROVED' ? item.approvedAt : null, systemNote: null,
        reasonText: item.reasonText, sellerDescription: item.sellerDescription, platformApprovalReason: null, platformDescription: null
      }
    })
  }
}

// ---------------------------------------------------------------------------------------------------------- dashboard

// FNV-1a: a stable 0..1 value for a piece of text.
function unit(text) {
  let hash = 2166136261
  for (const char of text) { hash ^= char.charCodeAt(0); hash = Math.imul(hash, 16777619) }
  return (hash >>> 0) / 4294967296
}
const istanbulDay = iso => new Date(Date.parse(iso) + 3 * 3_600_000).toISOString().slice(0, 10)
const channelProfiles = { TRENDYOL: [42000, 610], HEPSIBURADA: [18500, 720], SHOPIFY: [9200, 1150], N11: [6100, 540], PAZARAMA: [3400, 460] }
function channelDay(code, day) {
  const [base, basket] = channelProfiles[code]
  const weekday = new Date(`${day}T12:00:00Z`).getUTCDay()
  const weekend = weekday === 0 || weekday === 6 ? 1.25 : 1
  const partial = day === '2026-10-20' ? 0.38 : day > '2026-10-20' ? 0 : 1
  const amount = Math.round(base * (0.7 + unit(`${code}:${day}`) * 0.6) * weekend * partial)
  const orderCount = Math.max(amount > 0 ? 1 : 0, Math.round(amount / (basket * (0.85 + unit(`${day}:${code}`) * 0.3))))
  return { amount, orderCount, productQuantity: Math.round(orderCount * 1.4), shipmentCount: Math.round(orderCount * 0.9) }
}
function revenueSeries({ url }) {
  const from = url.searchParams.get('from'), to = url.searchParams.get('to')
  const platform = (url.searchParams.get('platform') ?? 'ALL').toLocaleUpperCase('tr-TR').replace(/İ/g, 'I')
  const codes = Object.keys(channelProfiles).filter(code => platform === 'ALL' || platform.includes(code))
  if (!from || !to) return []
  const points = []
  for (let day = istanbulDay(from), last = istanbulDay(to), guard = 0; day <= last && guard < 400; guard += 1) {
    const parts = codes.map(code => channelDay(code, day))
    points.push({ day, amount: parts.reduce((sum, part) => sum + part.amount, 0), orderCount: parts.reduce((sum, part) => sum + part.orderCount, 0), productQuantity: parts.reduce((sum, part) => sum + part.productQuantity, 0), shipmentCount: parts.reduce((sum, part) => sum + part.shipmentCount, 0), currency: 'TRY' })
    day = new Date(Date.parse(`${day}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10)
  }
  return points
}

const cell = (state, lastSuccessAt, lastProblemAt = null) => ({ state, lastSuccessAt: lastSuccessAt ? ist(lastSuccessAt) : null, lastProblemAt: lastProblemAt ? ist(lastProblemAt) : null })
const integrationMatrix = [
  { connectionId: 'c-ty', platformCode: 'TRENDYOL', displayName: 'Ravencia Moda Trendyol', externalStoreId: '784512', status: 'ACTIVE', issue: null, cells: {
    orders: cell('ok', '2026-10-20 09:28'), products: cell('ok', '2026-10-20 09:02'), stock: cell('ok', '2026-10-20 09:25'), price: cell('ok', '2026-10-20 08:47'), returns: cell('ok', '2026-10-20 09:12'), invoices: cell('ok', '2026-10-20 09:15') } },
  { connectionId: 'c-hb', platformCode: 'HEPSIBURADA', displayName: 'Ravencia Moda Hepsiburada', externalStoreId: 'ravenciamoda', status: 'ACTIVE', issue: null, cells: {
    orders: cell('ok', '2026-10-20 09:26'), products: cell('ok', '2026-10-20 07:40'), stock: cell('warn', '2026-10-20 06:58', '2026-10-20 09:20'), price: cell('ok', '2026-10-20 08:10'), returns: cell('ok', '2026-10-20 09:11'), invoices: cell('ok', '2026-10-19 23:30') } },
  { connectionId: 'c-sh', platformCode: 'SHOPIFY', displayName: 'Ravencia Shopify Mağazası', externalStoreId: 'ravencia-moda.myshopify.com', status: 'ACTIVE', issue: null, cells: {
    orders: cell('ok', '2026-10-20 09:29'), products: cell('ok', '2026-10-20 05:00'), stock: cell('ok', '2026-10-20 09:18'), price: cell('ok', '2026-10-20 05:00'), returns: cell('off', null), invoices: cell('ok', '2026-10-19 18:42') } },
  { connectionId: 'c-n11', platformCode: 'N11', displayName: 'Ravencia Moda n11', externalStoreId: '1209833', status: 'ACTIVE', issue: null, cells: {
    orders: cell('ok', '2026-10-20 09:21'), products: cell('ok', '2026-10-19 22:15'), stock: cell('ok', '2026-10-20 09:05'), price: cell('ok', '2026-10-20 07:30'), returns: cell('ok', '2026-10-20 08:50'), invoices: cell('off', null) } },
  { connectionId: 'c-pz', platformCode: 'PAZARAMA', displayName: 'Ravencia Moda Pazarama', externalStoreId: '5d1f0c2a', status: 'ACTIVE', issue: 'Yetkilendirme süresi doldu: API anahtarını yenileyin', cells: {
    orders: cell('bad', '2026-10-19 22:40', '2026-10-20 09:10'), products: cell('bad', '2026-10-19 21:00', '2026-10-20 09:10'), stock: cell('bad', '2026-10-19 22:35', '2026-10-20 09:10'), price: cell('warn', '2026-10-19 22:00', '2026-10-20 08:00'), returns: cell('bad', '2026-10-19 22:38', '2026-10-20 09:10'), invoices: cell('off', null) } },
  { connectionId: 'c-ef', platformCode: 'TRENDYOL_EFATURAM', displayName: 'Trendyol E-Faturam', externalStoreId: '784512', status: 'ACTIVE', issue: null, cells: {} },
]

// ---------------------------------------------------------------------------------------------------------- export

export const sales = [
  // Dashboard (the product counts come from catalog.mjs, the same answer the products page shows)
  ['GET /dashboard/revenue-series', revenueSeries],
  ['GET /dashboard/integration-matrix', integrationMatrix],
  // Orders
  ['GET /orders', orderPage],
  ['^/orders/(?!summary$)([^/]+)$', orderDetail],
  // (shipping-label settings: back-office.mjs, shared with the settings page's label designer)
  // Returns
  ['GET /returns', returnPage],
  ['^/returns/([^/]+)/rejection-reasons$', [
    { id: 'rr-1', name: 'Ürün kullanılmış', evidenceRequired: true },
    { id: 'rr-2', name: 'Ürün etiketi sökülmüş', evidenceRequired: true },
    { id: 'rr-3', name: 'İade edilen ürün siparişle uyuşmuyor', evidenceRequired: true },
    { id: 'rr-4', name: 'Ürün eksik gönderildi', evidenceRequired: false },
  ]],
  ['^/returns/([^/]+)$', returnDetail],
]
