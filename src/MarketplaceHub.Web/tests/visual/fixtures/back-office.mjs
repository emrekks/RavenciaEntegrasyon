// Back-office screens: Faturalar (/invoices), Ürün Soruları (/questions) and Sistem Ayarları (/settings). Fixed data
// around 15–20 October 2026 (Istanbul); the capture clock stands at 2026-10-20 09:30 (+03:00) = 06:30Z.
// Connection ids and store names are the shell's (fixtures/shell.mjs): c-ty, c-hb, c-sh, c-n11, c-pz, c-ef.
// Product images are same-origin files only; cargo names avoid carriers whose icon lives on an external CDN.

const productImage = '/pack/brand/ravencia-symbol-256.webp'
const productImageSmall = '/pack/brand/ravencia-symbol-128.webp'

// ── Faturalar ────────────────────────────────────────────────────────────────────────────────────────────────────────

const connectionIds = { TRENDYOL: 'c-ty', HEPSIBURADA: 'c-hb', SHOPIFY: 'c-sh', N11: 'c-n11', PAZARAMA: 'c-pz' }
const platformNames = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify', N11: 'n11', PAZARAMA: 'Pazarama' }
const address = (fullAddress, district, city) => JSON.stringify({ fullAddress, district, city, country: 'Türkiye' })

function workspaceItem(item) {
  const platformCode = item.platformCode
  const lines = item.lines ?? []
  return {
    orderId: `ord-${item.key}`,
    packageId: `pkg-${item.key}`,
    orderNumber: item.orderNumber,
    customerName: item.customerName,
    orderedAt: item.orderedAt,
    shipmentStatus: item.shipmentStatus,
    deliveredAt: item.deliveredAt ?? null,
    invoiceDueAt: item.invoiceDueAt ?? null,
    isDueSoon: item.isDueSoon ?? false,
    currency: 'TRY',
    amount: item.amount,
    productCount: lines.reduce((total, line) => total + line.quantity, 0),
    primaryImageUrl: lines[0]?.imageUrl ?? null,
    cargoProviderName: item.cargoProviderName ?? null,
    cargoTrackingNumber: item.cargoTrackingNumber ?? null,
    invoiceId: item.invoiceId ?? null,
    invoiceStatus: item.invoiceStatus,
    invoiceNumber: item.invoiceNumber ?? null,
    canCreateInvoice: item.canCreateInvoice ?? false,
    shipmentAddressJson: item.shipmentAddressJson ?? null,
    invoiceAddressJson: item.invoiceAddressJson ?? item.shipmentAddressJson ?? null,
    lines,
    invoiceErrorCode: item.invoiceErrorCode ?? null,
    invoiceDeliveryStatus: item.deliveryStatus ?? null,
    invoiceDeliveryReference: item.invoiceDeliveryReference ?? null,
    invoiceDocumentAvailable: item.invoiceDocumentAvailable ?? false,
    marketplaceInvoiceUrl: null,
    platformCode,
    platformDisplayName: platformNames[platformCode],
    invoiceCreationEnabled: item.invoiceCreationEnabled ?? platformCode !== 'TRENDYOL',
    marketplaceInvoiceReadErrorCode: item.marketplaceInvoiceReadErrorCode ?? null,
    marketplaceInvoiceReadErrorSummary: item.marketplaceInvoiceReadErrorSummary ?? null,
    environment: 'PRODUCTION',
    manualInvoiceDocumentId: item.manualInvoiceDocumentId ?? null,
    manualInvoiceDocumentType: item.manualInvoiceDocumentId ? 'PDF' : null,
    manualInvoiceDocumentUrl: null,
    manualInvoiceNumber: item.manualInvoiceNumber ?? null,
    manualInvoiceIssuedAt: item.manualInvoiceIssuedAt ?? null,
    manualInvoiceDeliveryStatus: item.manualInvoiceDeliveryStatus ?? null,
    manualInvoiceDeliveryErrorCode: null,
    fiscalStatus: item.fiscalStatus ?? (item.invoiceId ? 'ISSUED' : 'NONE'),
    platformInvoiceStatus: item.platformInvoiceStatus ?? 'NOT_INVOICED',
    deliveryStatus: item.deliveryStatus ?? null,
    currentPlatformInvoiceUrl: null,
    lastSuccessfulCheckAt: item.lastSuccessfulCheckAt ?? '2026-10-20T06:05:00Z',
    allowedActions: item.allowedActions ?? (platformCode === 'SHOPIFY' ? [] : ['CHECK_PLATFORM']),
    previousPlatformInvoiceReferences: item.previousPlatformInvoiceReferences ?? [],
    marketplaceConnectionId: connectionIds[platformCode],
    externalOrderId: item.orderNumber.replace(/\D/g, '') || item.orderNumber,
    externalPackageNumber: item.packageNumber ?? null,
    tab: item.tab,
  }
}

const line = (sku, barcode, description, quantity, unitPrice, imageUrl = null, vatRate = 20) => ({ sku, barcode, description, quantity, unitPrice, vatRate, imageUrl })

const invoiceRows = [
  workspaceItem({ tab: 'UNINVOICED', key: 'ty-10458823417', platformCode: 'TRENDYOL', orderNumber: '10458823417', packageNumber: '3289176540', customerName: 'Zeynep Kaya', orderedAt: '2026-10-13T07:15:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-15T11:20:00Z', invoiceDueAt: '2026-10-20T20:59:00Z', isDueSoon: true, amount: 1249.9, cargoProviderName: 'MNG Kargo', cargoTrackingNumber: '7330003656313', invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, invoiceCreationEnabled: false, shipmentAddressJson: address('Bağdat Cad. No:214 D:6 Suadiye', 'Kadıköy', 'İstanbul'), lines: [line('RV-KZK-001-S', '8690000000017', 'Yün Karışımlı Kazak · Lacivert · S', 1, 749.9, productImage), line('RV-ATK-014', '8690000000024', 'Örme Atkı · Gri', 1, 500, null)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'hb-4574136713', platformCode: 'HEPSIBURADA', orderNumber: '4574136713', packageNumber: 'HB4574136713', customerName: 'Mehmet Demir', orderedAt: '2026-10-17T13:05:00Z', shipmentStatus: 'SHIPPED', invoiceDueAt: '2026-10-24T20:59:00Z', amount: 389.5, cargoProviderName: 'Sendeo', cargoTrackingNumber: 'SND2026101700451', invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, shipmentAddressJson: address('Kızılay Mah. Atatürk Blv. No:91/4', 'Çankaya', 'Ankara'), lines: [line('RV-TSH-210-M', '8690000000031', 'Basic Tişört · Beyaz · M', 2, 194.75, productImageSmall)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'n11-221804417', platformCode: 'N11', orderNumber: '221804417', customerName: 'Elif Şahin', orderedAt: '2026-10-16T18:30:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-18T10:12:00Z', invoiceDueAt: '2026-10-23T20:59:00Z', amount: 2140, cargoProviderName: 'UPS Kargo', cargoTrackingNumber: '1Z84A7F20412', invoiceId: 'inv-n11-2207', invoiceNumber: 'RVN2026000000412', invoiceStatus: 'FATURA_PLATFORMA_AKTARILMADI', invoiceErrorCode: 'REMOTE_INVOICE_NOT_PRESENT', deliveryStatus: 'RETRYABLE_FAILURE', invoiceDocumentAvailable: true, shipmentAddressJson: address('Alsancak Mah. Kıbrıs Şehitleri Cad. No:38', 'Konak', 'İzmir'), lines: [line('RV-MNT-330-L', '8690000000048', 'Kaşe Mont · Camel · L', 1, 2140, productImage)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'shp-rv1048', platformCode: 'SHOPIFY', orderNumber: 'RV-1048', customerName: 'Selin Arslan', orderedAt: '2026-10-18T09:50:00Z', shipmentStatus: 'READY_TO_SHIP', invoiceDueAt: '2026-10-25T20:59:00Z', amount: 1675, cargoProviderName: 'Kargoist', cargoTrackingNumber: 'KGI-0048-2611', invoiceId: 'inv-shp-1048', invoiceStatus: 'FATURA_BEKLIYOR', fiscalStatus: 'NONE', shipmentAddressJson: address('Nilüfer Köyü Sk. No:7 Görükle', 'Nilüfer', 'Bursa'), lines: [line('RV-ELB-077-38', '8690000000055', 'Keten Elbise · Haki · 38', 1, 1150, productImage), line('RV-KMR-008', '8690000000062', 'Deri Kemer · Kahverengi', 1, 525, null)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'pzr-PZ20261014', platformCode: 'PAZARAMA', orderNumber: 'PZ202610140931', customerName: 'Burak Yıldız', orderedAt: '2026-10-14T10:10:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-16T14:40:00Z', invoiceDueAt: '2026-10-21T20:59:00Z', amount: 459.9, cargoProviderName: 'MNG Kargo', cargoTrackingNumber: '7330004120988', invoiceId: 'inv-pzr-0931', invoiceNumber: 'RVN2026000000398', invoiceStatus: 'FATURA_ISLENIYOR', deliveryStatus: 'SUBMITTED', shipmentAddressJson: address('Cumhuriyet Mah. İnönü Cad. No:12', 'Tepebaşı', 'Eskişehir'), lines: [line('RV-PNT-115-32', '8690000000079', 'Chino Pantolon · Bej · 32', 1, 459.9, productImageSmall)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'hb-4571220986', platformCode: 'HEPSIBURADA', orderNumber: '4571220986', packageNumber: 'HB4571220986', customerName: 'Ayşe Çelik', orderedAt: '2026-10-12T16:45:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-14T09:05:00Z', invoiceDueAt: '2026-10-21T08:00:00Z', isDueSoon: true, amount: 899, cargoProviderName: 'Sendeo', cargoTrackingNumber: 'SND2026101200317', invoiceId: 'inv-hb-3310', invoiceStatus: 'FATURA_BELIRSIZ', invoiceErrorCode: 'INVOICE_SUBMISSION_OUTCOME_UNKNOWN', deliveryStatus: 'UNKNOWN', shipmentAddressJson: address('Fevzi Çakmak Mah. Gazi Cad. No:55', 'Karatay', 'Konya'), lines: [line('RV-GML-402-L', '8690000000086', 'Oxford Gömlek · Mavi · L', 2, 449.5, productImage)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'ty-10461190258', platformCode: 'TRENDYOL', orderNumber: '10461190258', packageNumber: '3290044121', customerName: 'Can Öztürk', orderedAt: '2026-10-19T19:20:00Z', shipmentStatus: 'CREATED', amount: 329.99, invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, invoiceCreationEnabled: false, shipmentAddressJson: address('Yenişehir Mah. Kıbrıs Cad. No:3', 'Akdeniz', 'Mersin'), lines: [line('RV-BRT-019', '8690000000093', 'Triko Bere · Siyah', 1, 329.99, null)] }),
  workspaceItem({ tab: 'UNINVOICED', key: 'n11-221766035', platformCode: 'N11', orderNumber: '221766035', customerName: 'Deniz Aydın', orderedAt: '2026-10-15T12:00:00Z', shipmentStatus: 'CANCELLED', amount: 1320, cargoProviderName: 'UPS Kargo', cargoTrackingNumber: '1Z84A7F19877', invoiceId: 'inv-n11-2188', invoiceStatus: 'FATURA_REDDEDILDI', invoiceErrorCode: 'EINVOICE_RECIPIENT_REQUIRES_EFATURA', marketplaceInvoiceReadErrorCode: 'REMOTE_PACKAGE_NOT_FOUND', marketplaceInvoiceReadErrorSummary: 'n11 paketi bulamadı (404).', shipmentAddressJson: address('Atakum Mah. Atatürk Blv. No:240', 'Atakum', 'Samsun'), lines: [line('RV-YLK-020-M', '8690000000109', 'Şişme Yelek · Antrasit · M', 1, 1320, productImageSmall)] }),
  workspaceItem({ tab: 'INVOICED', key: 'shp-rv1039', platformCode: 'SHOPIFY', orderNumber: 'RV-1039', customerName: 'Merve Koç', orderedAt: '2026-10-11T08:25:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-13T15:10:00Z', amount: 2380, cargoProviderName: 'Kargoist', cargoTrackingNumber: 'KGI-0039-2588', invoiceId: 'inv-shp-1039', invoiceStatus: 'FATURA_YUKLENDI', invoiceDocumentAvailable: true, manualInvoiceDocumentId: 'mdoc-1039', manualInvoiceNumber: 'RVA2026000000371', manualInvoiceIssuedAt: '2026-10-13T16:00:00Z', manualInvoiceDeliveryStatus: 'CONFIRMED', shipmentAddressJson: address('Konyaaltı Cad. No:112 D:9', 'Muratpaşa', 'Antalya'), lines: [line('RV-TRC-501-M', '8690000000116', 'Trençkot · Bej · M', 1, 2380, productImage)] }),
  workspaceItem({ tab: 'INVOICED', key: 'ty-10452017764', platformCode: 'TRENDYOL', orderNumber: '10452017764', packageNumber: '3287720915', customerName: 'Hakan Polat', orderedAt: '2026-10-10T11:40:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-12T13:30:00Z', amount: 679.8, cargoProviderName: 'MNG Kargo', cargoTrackingNumber: '7330003512064', invoiceId: 'inv-ty-7764', invoiceNumber: 'RVN2026000000355', invoiceStatus: 'FATURA_KONTROLDE', platformInvoiceStatus: 'INVOICED', deliveryStatus: 'CONFIRMED', invoiceDocumentAvailable: true, shipmentAddressJson: address('Merkez Mah. Cumhuriyet Cad. No:18', 'Odunpazarı', 'Eskişehir'), lines: [line('RV-SWT-090-L', '8690000000123', 'Kapüşonlu Sweatshirt · Bordo · L', 2, 339.9, productImageSmall)] }),
  workspaceItem({ tab: 'INVOICED', key: 'hb-4569904412', platformCode: 'HEPSIBURADA', orderNumber: '4569904412', packageNumber: 'HB4569904412', customerName: 'Gizem Er', orderedAt: '2026-10-09T09:15:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-11T12:45:00Z', amount: 1099, cargoProviderName: 'Sendeo', cargoTrackingNumber: 'SND2026100900128', invoiceId: 'inv-hb-4412', invoiceNumber: 'RVN2026000000349', invoiceStatus: 'FATURA_KESILDI', platformInvoiceStatus: 'INVOICED', deliveryStatus: 'CONFIRMED', invoiceDeliveryReference: 'HB-INV-88412', invoiceDocumentAvailable: true, shipmentAddressJson: address('Yıldırım Mah. Ulubatlı Hasan Blv. No:7', 'Osmangazi', 'Bursa'), lines: [line('RV-CKT-220-40', '8690000000130', 'Deri Ceket · Siyah · 40', 1, 1099, productImage)] }),
  workspaceItem({ tab: 'INVOICED', key: 'pzr-PZ20261008', platformCode: 'PAZARAMA', orderNumber: 'PZ202610080514', customerName: 'Oğuz Kurt', orderedAt: '2026-10-08T17:55:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-10T10:30:00Z', amount: 249.9, cargoProviderName: 'MNG Kargo', cargoTrackingNumber: '7330003399417', invoiceId: 'inv-pzr-0514', invoiceNumber: 'RVN2026000000341', invoiceStatus: 'FATURA_KESILDI', platformInvoiceStatus: 'INVOICED', deliveryStatus: 'CONFIRMED', invoiceDocumentAvailable: true, shipmentAddressJson: address('Selçuklu Mah. Mevlana Cad. No:44', 'Selçuklu', 'Konya'), lines: [line('RV-COR-003', '8690000000147', 'Pamuklu Çorap · 3’lü Paket', 1, 249.9, null)] }),
  workspaceItem({ tab: 'HIDDEN', key: 'ty-10449981132', platformCode: 'TRENDYOL', orderNumber: '10449981132', packageNumber: '3286501177', customerName: 'Ece Yalçın', orderedAt: '2026-10-07T14:20:00Z', shipmentStatus: 'RETURNED', amount: 549.9, cargoProviderName: 'MNG Kargo', cargoTrackingNumber: '7330003301298', invoiceStatus: 'FATURA_IPTAL', invoiceCreationEnabled: false, shipmentAddressJson: address('Çarşı Mah. Uzun Sk. No:21', 'Ortahisar', 'Trabzon'), lines: [line('RV-ETK-061-38', '8690000000154', 'Pileli Etek · Siyah · 38', 1, 549.9, null)] }),
]

const tabCounts = {
  uninvoicedCount: invoiceRows.filter(row => row.tab === 'UNINVOICED').length,
  invoicedCount: invoiceRows.filter(row => row.tab === 'INVOICED').length,
  dueSoonCount: invoiceRows.filter(row => row.isDueSoon && row.tab !== 'HIDDEN').length,
  hiddenCount: invoiceRows.filter(row => row.tab === 'HIDDEN').length,
  totalPackageCount: invoiceRows.length,
}

function invoiceWorkspacePage({ url }) {
  const tab = url.searchParams.get('tab') ?? 'UNINVOICED'
  const pageSize = Number(url.searchParams.get('pageSize') ?? 20)
  const platforms = url.searchParams.get('platformCodes')?.split(',') ?? null
  const search = url.searchParams.get('search')?.toLocaleLowerCase('tr-TR') ?? ''
  const items = invoiceRows
    .filter(row => tab === 'ALL' ? row.tab !== 'HIDDEN' : tab === 'DUE_SOON' ? row.isDueSoon && row.tab !== 'HIDDEN' : row.tab === tab)
    .filter(row => !platforms || platforms.includes(row.platformCode))
    .filter(row => !search || `${row.orderNumber} ${row.customerName} ${row.invoiceNumber ?? ''} ${row.cargoTrackingNumber ?? ''}`.toLocaleLowerCase('tr-TR').includes(search))
    .map(({ tab: _tab, ...row }) => row)
  return {
    items: items.slice(0, pageSize),
    totalCount: items.length,
    pageNumber: 1,
    pageSize,
    totalPages: Math.max(1, Math.ceil(items.length / pageSize)),
    ...tabCounts,
    hasPendingMarketplaceInvoices: true,
    shipmentStatuses: ['CREATED', 'READY_TO_SHIP', 'SHIPPED', 'DELIVERED', 'CANCELLED', 'RETURNED'],
    cargoProviders: ['Kargoist', 'MNG Kargo', 'Sendeo', 'UPS Kargo'],
    invoiceStatuses: ['FATURA_BEKLIYOR', 'FATURA_ISLENIYOR', 'FATURA_BELIRSIZ', 'FATURA_PLATFORMA_AKTARILMADI', 'FATURA_REDDEDILDI', 'FATURA_KESILDI', 'FATURA_YUKLENDI', 'FATURA_IPTAL'],
  }
}

const invoiceStatusByWorkspace = { FATURA_BEKLIYOR: 'DRAFT', FATURA_ISLENIYOR: 'SUBMITTED', FATURA_BELIRSIZ: 'UNKNOWN_RESULT', FATURA_PLATFORMA_AKTARILMADI: 'MARKETPLACE_FAILED', FATURA_REDDEDILDI: 'REJECTED', FATURA_KESILDI: 'COMPLETED', FATURA_KONTROLDE: 'COMPLETED', FATURA_YUKLENDI: 'COMPLETED' }

// The invoice record behind a workspace row (GET /invoices/:id): the Shopify status window and the detail page read it.
function invoiceDetail({ match }) {
  const id = match[1]
  const row = invoiceRows.find(item => item.invoiceId === id) ?? invoiceRows[3]
  const lines = (row.lines ?? []).map((item, index) => {
    const lineTotal = Math.round(item.quantity * item.unitPrice * 100) / 100
    const taxExclusive = Math.round((lineTotal / 1.2) * 100) / 100
    return { id: `${id}-line-${index + 1}`, lineSequence: index + 1, description: item.description, sku: item.sku, unit: 'ADET', quantity: item.quantity, unitPrice: item.unitPrice, discountAmount: 0, vatRate: item.vatRate, vatAmount: Math.round((lineTotal - taxExclusive) * 100) / 100, lineTotal }
  })
  const taxExclusiveTotal = Math.round((row.amount / 1.2) * 100) / 100
  const status = invoiceStatusByWorkspace[row.invoiceStatus] ?? 'DRAFT'
  const issued = status !== 'DRAFT'
  return {
    id,
    orderNumber: row.orderNumber,
    invoiceType: 'E_ARCHIVE',
    status,
    currency: 'TRY',
    payableTotal: row.amount,
    invoiceNumber: row.invoiceNumber,
    dueAt: row.invoiceDueAt,
    createdAt: '2026-10-18T10:05:00Z',
    version: 3,
    orderId: row.orderId,
    packageId: row.packageId,
    providerConnectionId: row.platformCode === 'SHOPIFY' ? 'c-sh' : 'c-ef',
    sequencePurpose: 'E_ARSIV_SATIS',
    ettnUuid: issued ? '3f6c2a1e-8b4d-4c9a-9e21-5d7b0a4c1f08' : null,
    taxExclusiveTotal,
    discountTotal: 0,
    taxTotal: Math.round((row.amount - taxExclusiveTotal) * 100) / 100,
    note: '',
    issuedAt: issued ? '2026-10-18T10:07:00Z' : null,
    lastErrorCode: row.invoiceErrorCode,
    lines,
    documents: row.invoiceDocumentAvailable ? [{ id: `${id}-doc-1`, documentType: 'PDF', sha256: '9b2f61d8c0a4e7f35a1c88d2e4b07f6a3d915c2e8f4b7a06c1d3e5f7a9b2c4d6', createdAt: '2026-10-18T10:08:00Z' }] : [],
    attempts: issued ? [{ attemptNumber: 1, outcome: 'SUCCESS', errorCode: null, startedAt: '2026-10-18T10:06:00Z', completedAt: '2026-10-18T10:07:00Z' }] : [],
    deliveries: row.deliveryStatus ? [{ id: `${id}-delivery-1`, deliveryType: 'MARKETPLACE_LINK', status: row.deliveryStatus, externalReference: row.invoiceDeliveryReference, errorCode: row.invoiceErrorCode, createdAt: '2026-10-18T10:09:00Z' }] : [],
    allowedActions: status === 'DRAFT' ? ['VALIDATE'] : ['RECONCILE'],
    requiresSensitiveConfirmation: false,
  }
}

// ── Ürün Soruları ───────────────────────────────────────────────────────────────────────────────────────────────────

const stores = {
  TRENDYOL: { connectionId: 'c-ty', storeName: 'Ravencia Moda Trendyol' },
  HEPSIBURADA: { connectionId: 'c-hb', storeName: 'Ravencia Moda Hepsiburada' },
  N11: { connectionId: 'c-n11', storeName: 'Ravencia Moda n11' },
  PAZARAMA: { connectionId: 'c-pz', storeName: 'Ravencia Moda Pazarama' },
}

function question(item) {
  return {
    id: `q-${item.key}`,
    ...stores[item.platformCode],
    platformCode: item.platformCode,
    externalQuestionId: item.externalQuestionId,
    kind: item.kind ?? 'PRODUCT',
    status: item.status,
    questionText: item.questionText,
    productName: item.productName ?? null,
    productImageUrl: item.productImageUrl ?? productImage,
    productSku: item.productSku ?? null,
    productBarcode: item.productBarcode ?? null,
    productModelCode: item.productModelCode ?? null,
    productUrl: null,
    customerName: item.customerName ?? null,
    externalOrderNumber: item.externalOrderNumber ?? null,
    conversations: item.conversations ?? [],
    createdAt: item.createdAt,
    expiresAt: item.expiresAt ?? null,
    lastRemoteModifiedAt: item.lastRemoteModifiedAt ?? item.createdAt,
    lastSyncedAt: '2026-10-20T06:25:00Z',
    version: item.version ?? 1,
  }
}

const seller = (text, createdAt, rejectionReason = null) => ({ author: 'SELLER', text, createdAt, rejectionReason })
const customer = (author, text, createdAt) => ({ author, text, createdAt })

const questions = [
  question({ key: 'ty-88120451', platformCode: 'TRENDYOL', externalQuestionId: '88120451', status: 'WAITING_FOR_ANSWER', customerName: 'Gamze T.', productName: 'Yün Karışımlı Kazak · Lacivert', productSku: 'RV-KZK-001', productBarcode: '8690000000017', productModelCode: 'KZK-001', questionText: 'Merhaba, 1.68 boyunda ve 60 kilogramım. S beden bana bol gelir mi, kalıbı dar mı?', createdAt: '2026-10-20T05:48:00Z', expiresAt: '2026-10-22T05:48:00Z' }),
  question({ key: 'hb-5502917', platformCode: 'HEPSIBURADA', externalQuestionId: '5502917', status: 'WAITING_FOR_ANSWER', customerName: 'Murat K.', productName: 'Oxford Gömlek · Mavi', productSku: 'RV-GML-402', productBarcode: '8690000000086', questionText: 'Gömleğin kumaşı %100 pamuk mu? Ütü gerektiriyor mu?', createdAt: '2026-10-20T04:10:00Z', expiresAt: '2026-10-20T15:10:00Z' }),
  question({ key: 'n11-77310482', platformCode: 'N11', externalQuestionId: '77310482', status: 'WAITING_FOR_ANSWER', customerName: 'Sena A.', productName: 'Kaşe Mont · Camel', productSku: 'RV-MNT-330', productBarcode: '8690000000048', questionText: 'Bu montun astarı var mı? Kış için yeterince sıcak tutar mı?', createdAt: '2026-10-19T17:32:00Z', expiresAt: '2026-10-21T17:32:00Z' }),
  question({ key: 'pzr-PQ90314', platformCode: 'PAZARAMA', externalQuestionId: 'PQ90314', status: 'WAITING_FOR_ANSWER', customerName: 'Kerem B.', productName: 'Chino Pantolon · Bej', productSku: 'RV-PNT-115', productBarcode: '8690000000079', questionText: 'Paça boyu kaç santim? Kısaltma hizmetiniz var mı?', createdAt: '2026-10-19T12:05:00Z', expiresAt: '2026-10-22T12:05:00Z' }),
  question({ key: 'ty-88117302', platformCode: 'TRENDYOL', externalQuestionId: '88117302', status: 'WAITING_FOR_ANSWER', customerName: 'Büşra Y.', productName: 'Keten Elbise · Haki', productSku: 'RV-ELB-077', productBarcode: '8690000000055', productModelCode: 'ELB-077', questionText: 'Elbisenin boyu diz altında mı kalıyor? 38 beden için boy ölçüsünü paylaşabilir misiniz?', createdAt: '2026-10-19T08:20:00Z', expiresAt: '2026-10-21T08:20:00Z' }),
  question({ key: 'hb-5502844', platformCode: 'HEPSIBURADA', externalQuestionId: '5502844', status: 'WAITING_FOR_ANSWER', customerName: 'Yusuf D.', productName: 'Deri Ceket · Siyah', productSku: 'RV-CKT-220', productBarcode: '8690000000130', questionText: 'Gerçek deri mi yoksa suni deri mi? Sertifikası var mı?', conversations: [customer('Yusuf D.', 'Ayrıca 42 beden tekrar stoğa girecek mi?', '2026-10-18T16:02:00Z')], createdAt: '2026-10-18T15:55:00Z', expiresAt: '2026-10-20T03:55:00Z' }),
  question({ key: 'n11-77309915', platformCode: 'N11', externalQuestionId: '77309915', status: 'ANSWER_SUBMITTED', customerName: 'Ceren Ö.', productName: 'Şişme Yelek · Antrasit', productSku: 'RV-YLK-020', productBarcode: '8690000000109', questionText: 'Yıkamada çekme yapar mı? Makinede yıkanabilir mi?', conversations: [seller('Merhaba, 30 derecede hassas programda yıkanabilir; çekme yapmaz.', '2026-10-20T06:15:00Z')], createdAt: '2026-10-18T09:40:00Z', lastRemoteModifiedAt: '2026-10-20T06:15:00Z' }),
  question({ key: 'ty-88101177', platformCode: 'TRENDYOL', externalQuestionId: '88101177', status: 'ANSWERED', customerName: 'Pınar S.', productName: 'Basic Tişört · Beyaz', productSku: 'RV-TSH-210', productBarcode: '8690000000031', questionText: 'Tişört içini gösterir mi?', conversations: [seller('Merhaba, 180 gr/m² penye kumaştır; içini göstermez. İyi alışverişler dileriz.', '2026-10-17T10:14:00Z')], createdAt: '2026-10-17T09:02:00Z', lastRemoteModifiedAt: '2026-10-17T10:14:00Z' }),
  question({ key: 'pzr-PQ90288', platformCode: 'PAZARAMA', externalQuestionId: 'PQ90288', status: 'WAITING_FOR_APPROVE', customerName: 'İlker N.', productName: 'Kapüşonlu Sweatshirt · Bordo', productSku: 'RV-SWT-090', productBarcode: '8690000000123', questionText: 'Kapüşonun içi şardonlu mu?', conversations: [seller('Merhaba, kapüşon dahil iç yüzey şardonludur.', '2026-10-18T13:30:00Z')], createdAt: '2026-10-18T11:15:00Z', lastRemoteModifiedAt: '2026-10-18T13:30:00Z' }),
  question({ key: 'n11-77301230', platformCode: 'N11', externalQuestionId: '77301230', status: 'EXPIRED', customerName: 'Tolga E.', productName: 'Triko Bere · Siyah', productSku: 'RV-BRT-019', productBarcode: '8690000000093', questionText: 'Bere çift katlı mı?', createdAt: '2026-10-12T20:45:00Z', lastRemoteModifiedAt: '2026-10-15T20:45:00Z' }),
  question({ key: 'ty-88095510', platformCode: 'TRENDYOL', externalQuestionId: '88095510', status: 'REJECTED', customerName: 'Nazlı G.', productName: 'Pileli Etek · Siyah', productSku: 'RV-ETK-061', productBarcode: '8690000000154', questionText: 'Bu etek astarlı mı?', conversations: [seller('Astarlıdır, detaylar için bizi Instagram’dan takip edin.', '2026-10-16T08:10:00Z', 'Cevapta platform dışı yönlendirme bulunuyor.')], createdAt: '2026-10-16T07:30:00Z', lastRemoteModifiedAt: '2026-10-16T09:00:00Z' }),
  question({ key: 'hb-o-5503102', kind: 'ORDER', platformCode: 'HEPSIBURADA', externalQuestionId: '5503102', status: 'WAITING_FOR_ANSWER', customerName: 'Mehmet Demir', externalOrderNumber: '4574136713', questionText: 'Siparişim kargoya verildi görünüyor ama takip numarası çalışmıyor. Ne zaman teslim edilir?', createdAt: '2026-10-19T20:12:00Z', expiresAt: '2026-10-20T20:12:00Z' }),
  question({ key: 'pzr-o-PQ90320', kind: 'ORDER', platformCode: 'PAZARAMA', externalQuestionId: 'PQ90320', status: 'ANSWERED', customerName: 'Burak Yıldız', externalOrderNumber: 'PZ202610140931', productName: 'Chino Pantolon · Bej', productSku: 'RV-PNT-115', questionText: 'Faturamı e-posta ile gönderebilir misiniz?', conversations: [seller('Merhaba, faturanız sipariş detayınıza yüklendi; e-posta adresinize de gönderildi.', '2026-10-19T11:02:00Z')], createdAt: '2026-10-19T09:44:00Z', lastRemoteModifiedAt: '2026-10-19T11:02:00Z' }),
  question({ key: 'hb-o-5502770', kind: 'ORDER', platformCode: 'HEPSIBURADA', externalQuestionId: '5502770', status: 'ANSWERED', customerName: 'Gizem Er', externalOrderNumber: '4569904412', questionText: 'Ürünü değiştirmek istiyorum, nasıl iade kodu alabilirim?', conversations: [seller('Merhaba, Hepsiburada uygulamasında Siparişlerim > İade Talebi adımından kod oluşturabilirsiniz.', '2026-10-15T12:20:00Z')], createdAt: '2026-10-15T10:05:00Z', lastRemoteModifiedAt: '2026-10-15T12:20:00Z' }),
]

const questionStatusGroups = {
  WAITING: ['WAITING_FOR_ANSWER'],
  ANSWERED: ['ANSWERED'],
  EXPIRED: ['EXPIRED', 'UNANSWERED', 'AUTOCLOSED'],
  OTHER: ['ANSWER_SUBMITTED', 'WAITING_FOR_APPROVE', 'REJECTED', 'REPORTED'],
}

function questionPage({ url }) {
  const params = url.searchParams
  const kind = params.get('kind') ?? 'PRODUCT'
  const status = params.get('status') ?? 'WAITING'
  const platform = params.get('platform')
  const connectionId = params.get('connectionId')
  const search = params.get('search')?.toLocaleLowerCase('tr-TR') ?? ''
  const limit = Number(params.get('limit') ?? 50)
  const page = Number(params.get('page') ?? 1)
  const sort = params.get('sort') ?? 'NEWEST'
  const matching = questions
    .filter(item => kind === 'ALL' || item.kind === kind)
    .filter(item => status === 'ALL' || questionStatusGroups[status]?.includes(item.status))
    .filter(item => !platform || item.platformCode === platform)
    .filter(item => !connectionId || item.connectionId === connectionId)
    .filter(item => !search || `${item.questionText} ${item.productName ?? ''} ${item.productSku ?? ''} ${item.externalOrderNumber ?? ''}`.toLocaleLowerCase('tr-TR').includes(search))
    .sort((left, right) => sort === 'OLDEST' ? left.createdAt.localeCompare(right.createdAt) : sort === 'UPDATED' ? right.lastRemoteModifiedAt.localeCompare(left.lastRemoteModifiedAt) : right.createdAt.localeCompare(left.createdAt))
  return { items: matching.slice((page - 1) * limit, page * limit), page, limit, totalCount: matching.length }
}

const questionSyncStates = [
  { connectionId: 'c-ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Moda Trendyol', historyImported: true, historyStartedAt: '2026-09-01T07:00:00Z', progressStatus: 'COMPLETED', importedCount: 1284, productQuestionCount: 1284, orderQuestionCount: 0, lastRunStartedAt: '2026-10-20T06:20:00Z', lastSuccessAt: '2026-10-20T06:21:00Z', lastError: null },
  { connectionId: 'c-hb', platformCode: 'HEPSIBURADA', storeName: 'Ravencia Moda Hepsiburada', historyImported: true, historyStartedAt: '2026-09-01T07:00:00Z', progressStatus: 'COMPLETED', importedCount: 642, productQuestionCount: 518, orderQuestionCount: 124, lastRunStartedAt: '2026-10-20T06:18:00Z', lastSuccessAt: '2026-10-20T06:19:00Z', lastError: null },
  { connectionId: 'c-n11', platformCode: 'N11', storeName: 'Ravencia Moda n11', historyImported: false, historyStartedAt: '2026-10-18T08:00:00Z', progressStatus: 'INITIAL', importedCount: 213, productQuestionCount: 213, orderQuestionCount: 0, lastRunStartedAt: '2026-10-20T06:10:00Z', lastSuccessAt: '2026-10-20T06:12:00Z', lastError: null },
  { connectionId: 'c-pz', platformCode: 'PAZARAMA', storeName: 'Ravencia Moda Pazarama', historyImported: false, historyStartedAt: '2026-10-15T09:00:00Z', progressStatus: 'FAILED', importedCount: 57, productQuestionCount: 49, orderQuestionCount: 8, lastRunStartedAt: '2026-10-20T06:00:00Z', lastSuccessAt: null, lastError: 'Pazarama yetkilendirmesi reddedildi (401).' },
]

const questionTemplates = [
  { id: 'tpl-beden', title: 'Beden tablosu', text: 'Merhaba, ürün sayfasındaki beden tablosunu inceleyebilirsiniz. Kalıplarımız standart ölçülerdedir; arada kalırsanız bir büyük bedeni öneririz.', version: 2, updatedAt: '2026-10-02T09:15:00Z' },
  { id: 'tpl-kargo', title: 'Kargo süresi', text: 'Merhaba, siparişleriniz 1–2 iş günü içinde kargoya teslim edilir. Takip numarası kargolandığında iletilir.', version: 1, updatedAt: '2026-09-21T13:40:00Z' },
  { id: 'tpl-iade', title: 'İade koşulları', text: 'Merhaba, ürünü teslim aldıktan sonra 14 gün içinde etiketi sökülmemiş şekilde iade edebilirsiniz.', version: 3, updatedAt: '2026-10-11T07:05:00Z' },
  { id: 'tpl-yikama', title: 'Yıkama talimatı', text: 'Merhaba, 30 derecede tersten yıkamanızı ve kurutma makinesi kullanmamanızı öneririz.', version: 1, updatedAt: '2026-08-30T10:00:00Z' },
]

// ── Sistem Ayarları ─────────────────────────────────────────────────────────────────────────────────────────────────

const darkPalette = { bg: '#101014', sidebar: '#17171c', surface: '#17171c', surfaceRaised: '#1d1d23', surfaceSoft: '#22222a', border: '#2a2a32', borderStrong: '#383842', ink: '#ececf0', muted: '#a3a3b0', subtle: '#8a8a99', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#272139', accent: '#46b98f', accentSoft: '#1e2e2c', warning: '#e2aa4b', warningSoft: '#332c23', danger: '#e4636f', dangerSoft: '#342228', info: '#83b4ff' }
const lightPalette = { bg: '#e1e4eb', sidebar: '#eaedf2', surface: '#eaedf2', surfaceRaised: '#f0f2f6', surfaceSoft: '#dfe3ea', border: '#c9d0dc', borderStrong: '#a4aec0', ink: '#2f3a4f', muted: '#525e77', subtle: '#59657d', primary: '#5b44d2', primaryHover: '#4b36bb', primarySoft: '#e0dcf6', accent: '#0f7864', accentSoft: '#d3e8df', warning: '#8a560d', warningSoft: '#efe1c0', danger: '#a2344f', dangerSoft: '#efd5dc', info: '#2f5aa8' }
const grafitPalette = { bg: '#101014', sidebar: '#18181f', surface: '#18181f', surfaceRaised: '#202028', surfaceSoft: '#272730', border: '#303039', borderStrong: '#41414d', ink: '#f4f4f7', muted: '#aaaab9', subtle: '#8d8d9d', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#302443', accent: '#4adea0', accentSoft: '#1c332c', warning: '#f6c16b', warningSoft: '#382f22', danger: '#fb858b', dangerSoft: '#3c252b', info: '#83b4ff' }
const midnightPalette = { bg: '#0d1117', sidebar: '#131a24', surface: '#131a24', surfaceRaised: '#1a2330', surfaceSoft: '#202b3a', border: '#283447', borderStrong: '#36445a', ink: '#e6edf6', muted: '#9aa8bc', subtle: '#8592a6', primary: '#4f8cff', primaryHover: '#3b78eb', primarySoft: '#1b2a45', accent: '#3fbf9a', accentSoft: '#173029', warning: '#e0a845', warningSoft: '#33291a', danger: '#e5636e', dangerSoft: '#352027', info: '#7cb6ff' }

// Same colours as the panel's defaults (so every other screen looks as it would unsaved) plus one saved theme.
// Exported (with the label settings below) so an earlier fixture that answers the same path can reuse it.
export const appearanceSettings = {
  settings: {
    fontFamily: 'inter',
    fontSize: 'md',
    themeMode: 'dark',
    colors: { dark: darkPalette, light: lightPalette },
    colorThemes: [
      { id: 'default-dark', name: 'Ravencia — Sade', palette: darkPalette, builtIn: true },
      { id: 'ravencia-grafit', name: 'Ravencia — Grafit', palette: grafitPalette, builtIn: true },
      { id: 'custom-gece-mavisi', name: 'Gece Mavisi', palette: midnightPalette, builtIn: false },
    ],
  },
  version: 4,
}

const block = (id, kind, title, fields, align, position, extra = {}) => ({ id, kind, title, fields, align, text: '', fontSize: 14, rotation: 0, position, ...extra })

// Saved label layout: six blocks on A4 (one a custom store note), a rotated side note on the sticker.
export const shippingLabelSettings = {
  settings: {
    senderName: 'Ravencia Tekstil Ltd. Şti.',
    senderAddress: 'Atatürk Mah. 403. Cad. No:24/2 Ataşehir / İstanbul',
    defaultFormat: 'a4',
    showA4Button: true,
    showStickerButton: true,
    a4LabelsPerPage: 2,
    stickerWidthMm: 100,
    stickerHeightMm: 150,
    showCustomerPhone: true,
    sectionGapMm: 3,
    layout: {
      a4: [
        block('trackingBarcode', 'trackingBarcode', 'Takip barkodu', ['trackingNumber'], 'center', { x: 8, y: 4, width: 84, height: 17 }),
        block('address', 'address', 'Teslimat adresi', ['customerName', 'address'], 'left', { x: 8, y: 24, width: 54, height: 24 }, { fontSize: 16 }),
        block('orderInfo', 'orderInfo', 'Paket / sipariş bilgileri', ['packageNumber', 'orderNumber', 'customerEmail'], 'left', { x: 64, y: 24, width: 28, height: 24 }, { fontSize: 12 }),
        block('packageBarcode', 'packageBarcode', 'Paket barkodu', ['packageNumber'], 'center', { x: 8, y: 52, width: 84, height: 14 }),
        block('sender', 'sender', 'Gönderici ve kargo bilgileri', ['cargoProvider', 'senderName', 'senderAddress'], 'left', { x: 8, y: 70, width: 84, height: 13 }, { fontSize: 12 }),
        block('custom-store-note', 'custom', 'Mağaza notu', [], 'center', { x: 8, y: 86, width: 84, height: 9 }, { text: 'Bizi tercih ettiğiniz için teşekkürler! İade için: ravencia.com/iade', fontSize: 11 }),
      ],
      sticker: [
        block('trackingBarcode', 'trackingBarcode', 'Takip barkodu', ['trackingNumber'], 'center', { x: 6, y: 4, width: 88, height: 20 }),
        block('address', 'address', 'Teslimat adresi', ['customerName', 'address'], 'left', { x: 6, y: 27, width: 70, height: 30 }, { fontSize: 15 }),
        block('custom-fragile', 'custom', 'Dikkat', [], 'center', { x: 72, y: 34, width: 30, height: 12 }, { text: 'KIRILACAK EŞYA', fontSize: 12, rotation: 90 }),
        block('orderInfo', 'orderInfo', 'Paket / sipariş bilgileri', ['packageNumber', 'orderNumber'], 'left', { x: 6, y: 60, width: 88, height: 14 }, { fontSize: 12 }),
        block('sender', 'sender', 'Gönderici ve kargo bilgileri', ['cargoProvider', 'senderName'], 'left', { x: 6, y: 78, width: 88, height: 16 }, { fontSize: 11 }),
      ],
    },
  },
  version: 7,
}

const securitySessions = [
  { id: 'ses-01', state: 'ACTIVE', current: true, issuedAt: '2026-10-20T05:58:00Z', lastSeenAt: '2026-10-20T06:29:00Z', expiresAt: '2026-10-20T14:29:00Z' },
  { id: 'ses-02', state: 'ACTIVE', current: false, issuedAt: '2026-10-19T14:12:00Z', lastSeenAt: '2026-10-20T05:41:00Z', expiresAt: '2026-10-20T13:41:00Z' },
  { id: 'ses-03', state: 'ACTIVE', current: false, issuedAt: '2026-10-18T07:30:00Z', lastSeenAt: '2026-10-19T18:05:00Z', expiresAt: '2026-10-20T10:05:00Z' },
  { id: 'ses-04', state: 'REVOKED', current: false, issuedAt: '2026-10-16T08:02:00Z', lastSeenAt: '2026-10-16T17:44:00Z', expiresAt: '2026-10-17T01:44:00Z' },
  { id: 'ses-05', state: 'REVOKED', current: false, issuedAt: '2026-10-15T09:20:00Z', lastSeenAt: '2026-10-15T12:03:00Z', expiresAt: '2026-10-15T20:03:00Z' },
]

export const backOffice = [
  // Faturalar
  ['^/invoice-workspace/page$', invoiceWorkspacePage],
  ['^/invoices/([^/]+)$', invoiceDetail],
  ['PUT /invoice-workspace/manual-status', ({ body }) => ({ updatedCount: JSON.parse(body ?? '{}').packageIds?.length ?? 1 })],
  ['PUT /invoice-workspace/hidden', ({ body }) => ({ updatedCount: JSON.parse(body ?? '{}').packageIds?.length ?? 1 })],
  // Ürün Soruları
  ['GET /questions', questionPage],
  ['GET /questions/sync-state', questionSyncStates],
  ['POST /questions/sync', { jobs: ['job-question-sync-01', 'job-question-sync-02'], queuedConnections: 4 }],
  ['GET /question-templates', questionTemplates],
  ['^/questions/(q-[^/]+)$', ({ match }) => questions.find(item => item.id === match[1]) ?? questions[0]],
  // Sistem Ayarları
  ['GET /settings/appearance', appearanceSettings],
  ['GET /settings/shipping-label', shippingLabelSettings],
  ['GET /auth/security-status', { totpState: 'ENABLED', recoveryCodesRemaining: 7 }],
  ['GET /auth/sessions', securitySessions],
]
