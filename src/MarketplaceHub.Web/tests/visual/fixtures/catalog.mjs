// Catalog, integrations and job-queue data: the product list (/products), the product editor (/products/p1) with its
// categories, brands and category requirements, the per-connection cards of /integrations and the job list (/jobs).
// Everything is fixed: no clock or random input, so two captures receive byte-identical answers. Dates sit in the days
// before the frozen browser clock (2026-10-20 09:30 Istanbul). Pictures are static files of the built panel (public/).
// The connection list itself (/connections) belongs to the shell fixtures; answers here that depend on a connection
// read its platform from the id when they can and fall back to a neutral answer otherwise.

const img = {
  symbol: '/pack/brand/ravencia-symbol-256.webp',
  wordmark: '/pack/brand/ravencia-wordmark.webp',
  trendyol: '/platforms/trendyol.webp',
  hepsiburada: '/platforms/hepsiburada.webp',
  n11: '/platforms/n11.webp',
  pazarama: '/platforms/pazarama.webp',
  shopify: '/platforms/shopify.webp',
  pttavm: '/platforms/pttavm.webp',
  efaturam: '/platforms/trendyol-efaturam.webp'
}

// ---------------------------------------------------------------------------------------------------------------
// Catalog structure: categories, brands, attributes and the requirements of the trench-coat category.

const categories = [
  { id: 'cat-giyim', version: 1, name: 'Giyim', path: 'Giyim', depth: 0, isLeaf: false, isActive: true },
  { id: 'cat-kadin', version: 1, name: 'Kadın', path: 'Giyim > Kadın', depth: 1, isLeaf: false, isActive: true },
  { id: 'cat-trenckot', version: 3, name: 'Trençkot', path: 'Giyim > Kadın > Dış Giyim > Trençkot', depth: 3, isLeaf: true, isActive: true },
  { id: 'cat-jean', version: 2, name: 'Jean', path: 'Giyim > Kadın > Alt Giyim > Jean', depth: 3, isLeaf: true, isActive: true },
  { id: 'cat-erkek-gomlek', version: 2, name: 'Gömlek', path: 'Giyim > Erkek > Üst Giyim > Gömlek', depth: 3, isLeaf: true, isActive: true },
  { id: 'cat-erkek-kazak', version: 1, name: 'Kazak', path: 'Giyim > Erkek > Üst Giyim > Kazak', depth: 3, isLeaf: true, isActive: true },
  { id: 'cat-tisort', version: 1, name: 'Tişört', path: 'Giyim > Unisex > Tişört', depth: 2, isLeaf: true, isActive: true },
  { id: 'cat-cocuk-sweat', version: 1, name: 'Sweatshirt', path: 'Giyim > Çocuk > Sweatshirt', depth: 2, isLeaf: true, isActive: true },
  { id: 'cat-aksesuar', version: 1, name: 'Aksesuar', path: 'Aksesuar', depth: 0, isLeaf: false, isActive: true },
  { id: 'cat-kartvizit', version: 1, name: 'Kartvizitlik', path: 'Aksesuar > Cüzdan ve Kartlık > Kartvizitlik', depth: 2, isLeaf: true, isActive: true },
  { id: 'cat-canta', version: 1, name: 'Seyahat Çantası', path: 'Aksesuar > Çanta > Seyahat Çantası', depth: 2, isLeaf: true, isActive: true },
  { id: 'cat-eski', version: 4, name: 'Eski Sezon', path: 'Giyim > Eski Sezon', depth: 1, isLeaf: true, isActive: false }
]
const categoryPath = id => categories.find(item => item.id === id)?.path ?? null

const brands = [
  { id: 'br-ravencia', version: 2, name: 'Ravencia', isActive: true },
  { id: 'br-ravencia-kids', version: 1, name: 'Ravencia Kids', isActive: true },
  { id: 'br-atolye', version: 1, name: 'Atölye 34', isActive: true },
  { id: 'br-deriart', version: 1, name: 'DeriArt', isActive: true },
  { id: 'br-yolcu', version: 1, name: 'Yolcu', isActive: true },
  { id: 'br-eski', version: 3, name: 'Moda Evi (pasif)', isActive: false }
]

const values = (prefix, names) => names.map(value => ({ id: `${prefix}-${value.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9ğüşıöç]+/g, '-')}`, value }))
const attrRenk = { id: 'attr-renk', version: 2, code: 'option-renk', name: 'Renk', dataType: 'ENUM', roles: ['OPTION'], values: values('renk', ['Siyah', 'Bej', 'Haki', 'Lacivert', 'Kahverengi']) }
const attrBeden = { id: 'attr-beden', version: 2, code: 'option-beden', name: 'Beden', dataType: 'ENUM', roles: ['OPTION'], values: values('beden', ['XS', 'S', 'M', 'L', 'XL']) }
const attrWebColor = { id: 'attr-web-color', version: 1, code: 'web-color', name: 'Web Color', dataType: 'ENUM', roles: ['ATTRIBUTE'], values: values('web', ['Siyah', 'Bej', 'Yeşil', 'Lacivert', 'Kahverengi']) }
const attrMateryal = { id: 'attr-materyal', version: 1, code: 'materyal', name: 'Materyal', dataType: 'ENUM', roles: ['ATTRIBUTE'], values: values('materyal', ['Gabardin', 'Pamuk', 'Polyester', 'Keten']) }
const attrKalip = { id: 'attr-kalip', version: 1, code: 'kalip', name: 'Kalıp', dataType: 'ENUM', roles: ['ATTRIBUTE'], values: values('kalip', ['Oversize', 'Regular', 'Slim Fit']) }
const attrBoy = { id: 'attr-boy', version: 1, code: 'boy', name: 'Boy', dataType: 'ENUM', roles: ['ATTRIBUTE'], values: values('boy', ['Uzun', 'Midi', 'Kısa']) }
const attrYaka = { id: 'attr-yaka', version: 1, code: 'yaka-tipi', name: 'Yaka Tipi', dataType: 'ENUM', roles: ['ATTRIBUTE'], values: values('yaka', ['Klasik Yaka', 'Kruvaze Yaka', 'Kapüşonlu']) }
const attrMensei = { id: 'attr-mensei', version: 1, code: 'mensei', name: 'Menşei', dataType: 'TEXT', roles: ['ATTRIBUTE'], values: [] }
const valueId = (attribute, value) => attribute.values.find(item => item.value === value)?.id ?? null

const trenchRequirements = [
  { attributeId: attrRenk.id, isRequired: true, allowsCustomValue: false, displayOrder: 1, role: 'OPTION', attribute: attrRenk },
  { attributeId: attrBeden.id, isRequired: true, allowsCustomValue: false, displayOrder: 2, role: 'OPTION', attribute: attrBeden },
  { attributeId: attrWebColor.id, isRequired: true, allowsCustomValue: false, displayOrder: 3, role: 'ATTRIBUTE', isWebColor: true, attribute: attrWebColor },
  { attributeId: attrMateryal.id, isRequired: true, allowsCustomValue: false, displayOrder: 4, role: 'ATTRIBUTE', attribute: attrMateryal },
  { attributeId: attrKalip.id, isRequired: false, allowsCustomValue: false, displayOrder: 5, role: 'ATTRIBUTE', attribute: attrKalip },
  { attributeId: attrBoy.id, isRequired: false, allowsCustomValue: false, displayOrder: 6, role: 'ATTRIBUTE', attribute: attrBoy },
  { attributeId: attrYaka.id, isRequired: false, allowsCustomValue: false, displayOrder: 7, role: 'ATTRIBUTE', attribute: attrYaka },
  { attributeId: attrMensei.id, isRequired: true, allowsCustomValue: true, displayOrder: 8, role: 'ATTRIBUTE', attribute: attrMensei }
]
// Other categories share the option axes and the material, so the editor of any product finds its options.
const genericRequirements = trenchRequirements.filter(item => [attrRenk.id, attrBeden.id, attrMateryal.id, attrMensei.id].includes(item.attributeId))

// ---------------------------------------------------------------------------------------------------------------
// Products. Products that share a model code form one row (one colour each) in the list.

const platformNames = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify', N11: 'N11', PAZARAMA: 'Pazarama' }
// The connection ids the variants' price rows point at (the shell fixture's /connections answers the list itself).
const connectionIds = { TRENDYOL: 'c-ty', HEPSIBURADA: 'c-hb', SHOPIFY: 'c-sh', N11: 'c-n11', PAZARAMA: 'c-pz' }

let offerSequence = 0
function variant(productId, index, { sku, barcode, modelCode, options = {}, onHand, listPrice, salePrice, costPrice, mediaUrls = [], platforms = [], weight = 0.8, desi = 2, status = 'ACTIVE' }) {
  offerSequence += 1
  const optionSignature = Object.entries(options).map(([name, value]) => `${name}:${value}`).join('_') || 'Tek Ürün'
  const attributes = Object.entries(options).map(([name, value], sortOrder) => {
    const attribute = name === 'Renk' ? attrRenk : name === 'Beden' ? attrBeden : null
    return attribute ? { attributeId: attribute.id, valueId: valueId(attribute, value), textValue: attribute && valueId(attribute, value) ? null : value, numberValue: null, booleanValue: null, sortOrder } : null
  }).filter(Boolean)
  return {
    id: `${productId}-v${index + 1}`, version: 3, sku, barcode, modelCode, optionSignature, status,
    weight, width: 35, height: 8, length: 45, desi, costPrice,
    defaultListPrice: listPrice, defaultSalePrice: salePrice,
    onHand, available: Math.max(0, onHand - (index === 0 && onHand > 2 ? 1 : 0)), inventoryVersion: 5 + index,
    offerId: platforms.length ? `offer-${String(offerSequence).padStart(3, '0')}` : null, listPrice, salePrice, currency: 'TRY', offerStatus: platforms.length ? 'ACTIVE' : null,
    priceVersion: 2, offerVersion: platforms.length ? 4 : null, vatRate: 10, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 1,
    mediaUrls, options, attributes,
    platformStatuses: platforms.map(([code, status]) => ({
      platform: platformNames[code], platformCode: code, isLinked: status !== 'UNLINKED', status,
      connectionId: connectionIds[code], offerId: `offer-${String(offerSequence).padStart(3, '0')}-${code.toLowerCase()}`,
      listPrice, salePrice: code === 'SHOPIFY' ? salePrice - 50 : salePrice, currency: 'TRY', vatRate: 10, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 1, offerVersion: 2
    }))
  }
}

function product({ id, version = 7, title, description, brandId, categoryId, status = 'ACTIVE', updatedAt, modelCode, primaryImageUrl, mediaUrls = [], variants, attributes = [], familyMediaItems, platformStatuses, listPrice, salePrice }) {
  const prices = variants.map(item => item.salePrice).filter(value => value != null)
  return {
    id, version, title, description, brandId, categoryId, status, updatedAt,
    defaultListPrice: listPrice, defaultSalePrice: salePrice,
    categoryPath: categoryPath(categoryId), variants, primaryImageUrl, mediaUrls,
    totalStock: variants.reduce((total, item) => total + item.onHand, 0),
    startingPrice: prices.length ? Math.min(...prices) : null,
    currency: 'TRY', modelCode, activePlatforms: [...new Set(platformStatuses.map(item => item.platformCode))],
    familyMediaUrls: familyMediaItems?.map(item => item.url) ?? [], familyMediaItems: familyMediaItems ?? [], familyOrderedMediaUrls: [], hasCustomMediaOrder: false,
    platformStatuses, attributes,
    options: variants.some(item => Object.keys(item.options).length)
      ? [...new Set(variants.flatMap(item => Object.keys(item.options)))].map(name => ({ id: `opt-${id}-${name.toLocaleLowerCase('tr-TR')}`, label: name, values: [...new Set(variants.map(item => item.options[name]).filter(Boolean))].map(value => ({ id: `opt-${id}-${value.toLocaleLowerCase('tr-TR')}`, label: value })) }))
      : []
  }
}
const platformRow = (code, status, matched, total) => ({ platform: platformNames[code], platformCode: code, status, matchedVariantCount: matched, variantCount: total })
const attribute = (attr, value, sortOrder) => ({ attributeId: attr.id, valueId: valueId(attr, value), textValue: null, numberValue: null, booleanValue: null, sortOrder })
const textAttribute = (attr, text, sortOrder) => ({ attributeId: attr.id, valueId: null, textValue: text, numberValue: null, booleanValue: null, sortOrder })

const trenchMedia = [img.symbol, img.wordmark, img.trendyol, img.hepsiburada, img.shopify]
const trenchPlatforms = [['TRENDYOL', 'LIVE'], ['HEPSIBURADA', 'LIVE'], ['SHOPIFY', 'LINKED']]
const p1Variants = [['S', 4], ['M', 9], ['L', 6], ['XL', 2]].map(([size, onHand], index) => variant('p1', index, {
  sku: `RVN-TRC-2401-SYH-${size}`, barcode: `86900124010${index + 1}`, modelCode: 'RVN-TRC-2401',
  options: { Renk: 'Siyah', Beden: size }, onHand, listPrice: 2899.9, salePrice: 2499.9, costPrice: 1180,
  mediaUrls: trenchMedia, platforms: trenchPlatforms, weight: 1.1, desi: 3
}))
const p1bVariants = [['S', 0], ['M', 3], ['L', 0]].map(([size, onHand], index) => variant('p1b', index, {
  sku: `RVN-TRC-2401-BEJ-${size}`, barcode: `86900124011${index + 1}`, modelCode: 'RVN-TRC-2401',
  options: { Renk: 'Bej', Beden: size }, onHand, listPrice: 2899.9, salePrice: 2599.9, costPrice: 1180,
  mediaUrls: [img.n11], platforms: [['TRENDYOL', 'LIVE'], ['HEPSIBURADA', 'APPROVAL_PENDING']], weight: 1.1, desi: 3
}))

const products = [
  product({
    id: 'p1', version: 12, title: 'Kadın Oversize Gabardin Trençkot - Siyah', modelCode: 'RVN-TRC-2401', brandId: 'br-ravencia', categoryId: 'cat-trenckot', updatedAt: '2026-10-19T16:42:00+03:00',
    description: '<p>Su itici <strong>gabardin</strong> kumaştan, kemerli ve kruvaze yakalı oversize trençkot.</p><ul><li>Astarlı, iki yan cepli</li><li>Model 1,76 m boyunda ve S beden giyiyor</li></ul>',
    primaryImageUrl: img.symbol, mediaUrls: trenchMedia, variants: p1Variants, listPrice: 2899.9, salePrice: 2499.9,
    attributes: [attribute(attrWebColor, 'Siyah', 0), attribute(attrMateryal, 'Gabardin', 1), attribute(attrKalip, 'Oversize', 2), attribute(attrBoy, 'Uzun', 3), attribute(attrYaka, 'Kruvaze Yaka', 4), textAttribute(attrMensei, 'Türkiye', 5)],
    familyMediaItems: [{ url: img.n11, mediaIds: ['m-p1b-1'], sourceProductTitles: ['Kadın Oversize Gabardin Trençkot - Bej'] }],
    platformStatuses: [platformRow('TRENDYOL', 'LIVE', 4, 4), platformRow('HEPSIBURADA', 'LIVE', 4, 4), platformRow('SHOPIFY', 'LINKED', 4, 4)]
  }),
  product({
    id: 'p1b', version: 8, title: 'Kadın Oversize Gabardin Trençkot - Bej', modelCode: 'RVN-TRC-2401', brandId: 'br-ravencia', categoryId: 'cat-trenckot', updatedAt: '2026-10-19T16:40:00+03:00',
    description: '<p>Bej renk seçeneği; aynı kalıp ve kumaş.</p>', primaryImageUrl: img.n11, mediaUrls: [img.n11], variants: p1bVariants, listPrice: 2899.9, salePrice: 2599.9,
    attributes: [attribute(attrWebColor, 'Bej', 0), attribute(attrMateryal, 'Gabardin', 1)],
    familyMediaItems: trenchMedia.map((url, index) => ({ url, mediaIds: [`m-p1-${index + 1}`], sourceProductTitles: ['Kadın Oversize Gabardin Trençkot - Siyah'] })),
    platformStatuses: [platformRow('TRENDYOL', 'LIVE', 3, 3), platformRow('HEPSIBURADA', 'APPROVAL_PENDING', 0, 3)]
  }),
  product({
    id: 'p2', version: 5, title: 'Erkek Slim Fit Keten Gömlek', modelCode: 'RVN-GML-1180', brandId: 'br-atolye', categoryId: 'cat-erkek-gomlek', updatedAt: '2026-10-18T11:05:00+03:00',
    description: '<p>%100 keten, nefes alan dokuma.</p>', primaryImageUrl: img.hepsiburada, mediaUrls: [img.hepsiburada, img.pttavm], listPrice: 1199, salePrice: 899,
    variants: [['S', 12], ['M', 18], ['L', 14], ['XL', 7]].map(([size, onHand], index) => variant('p2', index, { sku: `RVN-GML-1180-BYZ-${size}`, barcode: `86900111800${index + 1}`, modelCode: 'RVN-GML-1180', options: { Renk: 'Beyaz', Beden: size }, onHand, listPrice: 1199, salePrice: 899, costPrice: 410, mediaUrls: [img.hepsiburada], platforms: [['TRENDYOL', 'LIVE'], ['HEPSIBURADA', 'LIVE'], ['N11', 'LIVE'], ['PAZARAMA', 'LIVE']] })),
    attributes: [attribute(attrMateryal, 'Keten', 0)],
    platformStatuses: [platformRow('TRENDYOL', 'LIVE', 4, 4), platformRow('HEPSIBURADA', 'LIVE', 4, 4), platformRow('N11', 'LIVE', 4, 4), platformRow('PAZARAMA', 'LIVE', 4, 4)]
  }),
  product({
    id: 'p3', version: 9, title: 'Unisex Pamuklu Basic Tişört', modelCode: 'RVN-TSH-0042', brandId: 'br-ravencia', categoryId: 'cat-tisort', updatedAt: '2026-10-20T08:55:00+03:00',
    description: '<p>Yumuşak dokulu süprem pamuk.</p>', primaryImageUrl: img.trendyol, mediaUrls: [img.trendyol], listPrice: 449, salePrice: 349,
    variants: [['XS', 0], ['S', 1], ['M', 0], ['L', 3], ['XL', 0]].map(([size, onHand], index) => variant('p3', index, { sku: `RVN-TSH-0042-LCV-${size}`, barcode: `86900100420${index + 1}`, modelCode: 'RVN-TSH-0042', options: { Renk: 'Lacivert', Beden: size }, onHand, listPrice: 449, salePrice: 349, costPrice: 120, mediaUrls: [img.trendyol], platforms: [['TRENDYOL', 'LIVE'], ['N11', 'PARTIAL_LIVE']], weight: 0.25, desi: 1 })),
    attributes: [attribute(attrMateryal, 'Pamuk', 0)],
    platformStatuses: [platformRow('TRENDYOL', 'LIVE', 5, 5), platformRow('N11', 'PARTIAL_LIVE', 3, 5)]
  }),
  product({
    id: 'p4', version: 2, title: 'Hakiki Deri Kartvizitlik', modelCode: 'DRA-KRT-0007', brandId: 'br-deriart', categoryId: 'cat-kartvizit', status: 'DRAFT', updatedAt: '2026-10-17T15:20:00+03:00',
    description: '<p>Bitkisel tabaklanmış dana derisi.</p>', primaryImageUrl: null, mediaUrls: [], listPrice: 690, salePrice: 590,
    variants: [variant('p4', 0, { sku: 'DRA-KRT-0007', barcode: '8690010000071', modelCode: 'DRA-KRT-0007', onHand: 25, listPrice: 690, salePrice: 590, costPrice: 210, weight: 0.1, desi: 1 })],
    platformStatuses: []
  }),
  product({
    id: 'p5', version: 6, title: 'Kadın Yüksek Bel Mom Jean', modelCode: 'RVN-JNS-0310', brandId: 'br-ravencia', categoryId: 'cat-jean', updatedAt: '2026-10-16T10:12:00+03:00',
    description: '<p>Taşlanmış denim, yüksek bel.</p>', primaryImageUrl: img.pazarama, mediaUrls: [img.pazarama], listPrice: 1299, salePrice: 1299,
    variants: [['36', 8], ['38', 11], ['40', 6], ['42', 2]].map(([size, onHand], index) => variant('p5', index, { sku: `RVN-JNS-0310-MAV-${size}`, barcode: `86900103100${index + 1}`, modelCode: 'RVN-JNS-0310', options: { Renk: 'Mavi', Beden: size }, onHand, listPrice: 1299, salePrice: 1299, costPrice: 530, mediaUrls: [img.pazarama], platforms: [['TRENDYOL', 'LIVE'], ['HEPSIBURADA', 'UPDATE_REJECTED'], ['PAZARAMA', 'LIVE']] })),
    attributes: [attribute(attrMateryal, 'Pamuk', 0)],
    platformStatuses: [platformRow('TRENDYOL', 'LIVE', 4, 4), platformRow('HEPSIBURADA', 'UPDATE_REJECTED', 4, 4), platformRow('PAZARAMA', 'LIVE', 4, 4)]
  }),
  product({
    id: 'p6', version: 4, title: 'Çocuk Kapüşonlu Sweatshirt', modelCode: 'RVK-SWT-0520', brandId: 'br-ravencia-kids', categoryId: 'cat-cocuk-sweat', status: 'ARCHIVED', updatedAt: '2026-10-15T09:00:00+03:00',
    description: '<p>İçi şardonlu, kapüşonlu.</p>', primaryImageUrl: img.pttavm, mediaUrls: [img.pttavm], listPrice: 599, salePrice: 479,
    variants: [['5-6 Yaş', 0], ['7-8 Yaş', 0], ['9-10 Yaş', 0]].map(([size, onHand], index) => variant('p6', index, { sku: `RVK-SWT-0520-GRI-${index + 1}`, barcode: `86900105200${index + 1}`, modelCode: 'RVK-SWT-0520', options: { Renk: 'Gri', Beden: size }, onHand, listPrice: 599, salePrice: 479, costPrice: 190, mediaUrls: [img.pttavm], platforms: [['TRENDYOL', 'ARCHIVED']] })),
    platformStatuses: [platformRow('TRENDYOL', 'ARCHIVED', 0, 3)]
  }),
  product({
    id: 'p7', version: 3, title: 'Su Geçirmez Seyahat Çantası 45 L', modelCode: 'YLC-CNT-0045', brandId: 'br-yolcu', categoryId: 'cat-canta', updatedAt: '2026-10-18T19:30:00+03:00',
    description: '<p>Kabin boyutuna uygun, su geçirmez.</p>', primaryImageUrl: img.shopify, mediaUrls: [img.shopify], listPrice: 1850, salePrice: 1649,
    variants: [variant('p7', 0, { sku: 'YLC-CNT-0045-HAK', barcode: '8690010000453', modelCode: 'YLC-CNT-0045', options: { Renk: 'Haki' }, onHand: 14, listPrice: 1850, salePrice: 1649, costPrice: 720, mediaUrls: [img.shopify], platforms: [['SHOPIFY', 'LIVE']], weight: 1.4, desi: 6 })],
    platformStatuses: [platformRow('SHOPIFY', 'LIVE', 1, 1)]
  }),
  product({
    id: 'p8', version: 7, title: 'Erkek Balıkçı Yaka Triko Kazak', modelCode: 'RVN-KZK-0777', brandId: 'br-atolye', categoryId: 'cat-erkek-kazak', updatedAt: '2026-10-19T21:15:00+03:00',
    description: '<p>Yün karışımlı, balıkçı yaka.</p>', primaryImageUrl: img.efaturam, mediaUrls: [img.efaturam], listPrice: 1499, salePrice: 1149,
    variants: [['M', 5], ['L', 0], ['XL', 4]].map(([size, onHand], index) => variant('p8', index, { sku: `RVN-KZK-0777-KHV-${size}`, barcode: `86900107770${index + 1}`, modelCode: 'RVN-KZK-0777', options: { Renk: 'Kahverengi', Beden: size }, onHand, listPrice: 1499, salePrice: 1149, costPrice: 520, mediaUrls: [img.efaturam], platforms: [['TRENDYOL', 'CREATE_REJECTED'], ['N11', 'QUEUED']] })),
    attributes: [attribute(attrMateryal, 'Polyester', 0)],
    platformStatuses: [platformRow('TRENDYOL', 'CREATE_REJECTED', 0, 3), { ...platformRow('N11', 'QUEUED', 0, 3), isChecking: true }]
  })
]

// Low stock: at least half of a colour's variants are sold out (as the panel describes it).
const isLowStock = item => item.variants.filter(entry => entry.onHand <= 0).length * 2 >= item.variants.length && item.status !== 'ARCHIVED'

function productPage({ url }) {
  const params = url.searchParams
  const search = (params.get('search') ?? '').toLocaleLowerCase('tr-TR')
  const status = params.get('status') ?? ''
  const stock = params.get('stock') ?? ''
  const platform = (params.get('platform') ?? '').split(':')[0]
  const limit = Number(params.get('limit') ?? 20)
  const offset = Number(params.get('after') ?? 0)
  const matching = products.filter(item =>
    (!search || [item.title, item.modelCode, ...item.variants.map(entry => entry.sku)].join(' ').toLocaleLowerCase('tr-TR').includes(search))
    && (!status || item.status === status)
    && (!platform || item.platformStatuses.some(row => row.platformCode === platform))
    && (stock !== 'LOW' || isLowStock(item))
    && (stock !== 'OUT' || item.totalStock === 0))
  const items = matching.slice(offset, offset + limit)
  const hasMore = offset + limit < matching.length
  return { items, totalCount: matching.length, hasMore, nextCursor: hasMore ? String(offset + limit) : null }
}

const productSummary = {
  totalCount: products.length,
  activeCount: products.filter(item => item.status === 'ACTIVE').length,
  outOfStockCount: products.filter(item => item.totalStock === 0).length,
  lowStockCount: products.filter(isLowStock).length,
  platforms: ['TRENDYOL', 'HEPSIBURADA', 'SHOPIFY', 'N11', 'PAZARAMA']
}

// ---------------------------------------------------------------------------------------------------------------
// Per-connection answers. The platform comes from the shell fixture's connection ids (c-ty, c-hb, c-sh, c-n11, c-pz,
// c-ef); an unknown id gets the neutral answer.

const shellConnectionPlatforms = { 'c-ty': 'TRENDYOL', 'c-hb': 'HEPSIBURADA', 'c-sh': 'SHOPIFY', 'c-n11': 'N11', 'c-pz': 'PAZARAMA', 'c-ef': 'TRENDYOL_EFATURAM' }
function platformOf(connectionId) {
  return shellConnectionPlatforms[decodeURIComponent(connectionId)] ?? 'UNKNOWN'
}

function publicationStatus({ match }) {
  const [, productId, connectionId] = match
  const item = products.find(entry => entry.id === productId) ?? products[0]
  const platform = platformOf(connectionId)
  // Hepsiburada still waits for approval of the Bej colour; every other channel has the product live.
  const actualStatus = platform === 'HEPSIBURADA' && productId === 'p1b' ? 'APPROVAL_PENDING' : 'LIVE'
  return {
    productId: item.id, connectionId: decodeURIComponent(connectionId), profileId: `lp-${item.id}-${platform.toLowerCase()}`,
    desiredStatus: 'LIVE', actualStatus, lastRejectionCode: null,
    lastJobId: platform === 'TRENDYOL' ? 'job-pub-p1-trendyol' : null, lastJobStatus: platform === 'TRENDYOL' ? 'SUCCEEDED' : null,
    lines: item.variants.map(entry => ({ variantId: entry.id, sku: entry.sku, barcode: entry.barcode, desiredStatus: 'LIVE', actualStatus, rejectionCode: null }))
  }
}

const shopifyScopes = { ORDER_READ: 'read_orders', PRODUCT_READ: 'read_products', PRODUCT_WRITE: 'write_products', INVENTORY_WRITE: 'write_inventory', PUBLICATION_READ: 'read_publications', PUBLICATION_WRITE: 'write_publications', RETURN_READ: 'read_returns' }
const capabilityCodes = ['ORDER_READ', 'PRODUCT_READ', 'PRODUCT_WRITE', 'INVENTORY_WRITE', 'PUBLICATION_READ', 'PUBLICATION_WRITE', 'RETURN_READ', 'QUESTION_READ']
function capabilities({ match }) {
  const platform = platformOf(match[1])
  return capabilityCodes.map(code => ({
    code, supportLevel: platform === 'PAZARAMA' && code.startsWith('PUBLICATION') ? 'UNSUPPORTED' : 'SUPPORTED',
    environment: 'PRODUCTION', storeScope: 'STORE', apiVersion: platform === 'SHOPIFY' ? '2026-10' : 'V2',
    sourceUrl: null, requiredScope: platform === 'SHOPIFY' ? shopifyScopes[code] ?? null : null,
    evidenceNote: 'Bağlantı testinde doğrulandı.', verifiedAt: '2026-10-19T08:00:00+03:00', verifiedForConnection: !(platform === 'PAZARAMA' && code.startsWith('PUBLICATION'))
  }))
}

// The first import still runs for n11 (orders and returns), every other connection is done with it.
function initialImport({ match }) {
  const connectionId = decodeURIComponent(match[1])
  const running = platformOf(connectionId) === 'N11'
  return {
    connectionId, running,
    parts: running
      ? [{ kind: 'ORDERS', state: 'RUNNING', percent: 60 }, { kind: 'RETURNS', state: 'DONE', percent: 100 }, { kind: 'QUESTIONS', state: 'QUEUED', percent: null }]
      : [{ kind: 'ORDERS', state: 'DONE', percent: 100 }, { kind: 'RETURNS', state: 'DONE', percent: 100 }, { kind: 'QUESTIONS', state: 'DONE', percent: 100 }]
  }
}

function instantNotifications({ match }) {
  const platform = platformOf(match[1])
  if (platform === 'TRENDYOL') return { state: 'ACTIVE', endpoint: 'https://panel.ravencia.com/webhooks/trendyol/c-ty', registeredAt: '2026-10-02T10:00:00+03:00', lastReceivedAt: '2026-10-20T09:12:00+03:00', receivedLast24Hours: 148, message: null, setup: null }
  if (platform === 'SHOPIFY') return { state: 'QUIET', endpoint: 'https://panel.ravencia.com/webhooks/shopify/c-sh', registeredAt: '2026-09-21T14:00:00+03:00', lastReceivedAt: '2026-10-19T22:40:00+03:00', receivedLast24Hours: 0, message: 'Son 24 saatte bildirim gelmedi.', setup: null }
  return { state: 'UNSUPPORTED', endpoint: null, registeredAt: null, lastReceivedAt: null, receivedLast24Hours: 0, message: 'Bu platform anlık bildirim sunmuyor.', setup: null }
}

const syncPolicyTypes = [['ORDERS', 300, 'HEALTHY'], ['RETURNS', 900, 'HEALTHY'], ['QUESTIONS', 900, 'DEGRADED'], ['PRODUCTS', 3600, 'HEALTHY'], ['PRICE_INVENTORY', 600, 'HEALTHY']]
function syncPolicies({ match }) {
  const connectionId = decodeURIComponent(match[1])
  return syncPolicyTypes.map(([resourceType, intervalSeconds, healthStatus], index) => ({
    id: `sp-${connectionId}-${resourceType.toLowerCase()}`, resourceType, intervalSeconds, overlapSeconds: 120, jitterSeconds: 15, enabled: resourceType !== 'PRICE_INVENTORY', version: 3,
    lastSuccessAt: `2026-10-20T09:${String(10 + index * 3).padStart(2, '0')}:00+03:00`, lastModifiedWatermark: '2026-10-20T09:00:00+03:00',
    lastCursorAdvancedAt: '2026-10-20T09:05:00+03:00', cursorStagnantSince: null, cursorProgressStatus: 'ADVANCING', healthStatus, recoveryGapStatus: 'NONE', recoveryGapDays: null,
    lastAttemptAt: `2026-10-20T09:${String(10 + index * 3).padStart(2, '0')}:00+03:00`, lastDurationMs: 1800 + index * 420, consecutiveFailureCount: healthStatus === 'DEGRADED' ? 2 : 0,
    lastRequestCount: 4 + index, lastReceivedCount: 30 - index * 4, lastChangedCount: 6 - index, lastInsertedCount: 3, lastUpdatedCount: 3, lastSkippedCount: 0, lastFailedCount: healthStatus === 'DEGRADED' ? 1 : 0,
    lastRetryCount: 0, lastRateLimitCount: 0, connectionBacklogCount: 2, connectionOldestBacklogAt: '2026-10-20T09:20:00+03:00', connectionManualReviewCount: 1, connectionDeadJobCount24h: 0,
    requiresExternalWrites: resourceType === 'PRICE_INVENTORY'
  }))
}

// Publication readiness: only asked for a channel where the product is not live. The category is mapped and verified,
// its attributes are known and need no further values, so the card shows no blocking issue.
const referenceSnapshot = 'snap-2026-10-19'
const categoryMapping = { externalId: '1180', snapshotId: referenceSnapshot, status: 'VERIFIED' }
const referenceCategories = { snapshotId: referenceSnapshot, items: [{ externalId: '1180', name: 'Trençkot', isActive: true, isLeaf: true }, { externalId: '601', name: 'Gömlek', isActive: true, isLeaf: true }, { externalId: '383', name: 'Kazak', isActive: true, isLeaf: true }] }
const referenceAttributes = { snapshotId: referenceSnapshot, items: [{ externalId: '47', name: 'Renk', isActive: true, isRequired: true, allowsCustomValue: false }, { externalId: '338', name: 'Beden', isActive: true, isRequired: true, allowsCustomValue: false }, { externalId: '14', name: 'Materyal', isActive: true, isRequired: false, allowsCustomValue: false }, { externalId: '1192', name: 'Menşei', isActive: true, isRequired: true, allowsCustomValue: true }] }
const attributeDefaults = [{ externalAttributeId: '1192' }]

// ---------------------------------------------------------------------------------------------------------------
// Jobs. The 24-hour list holds the newest jobs; three older ones show up under "Son 7 Gün" and "Tüm Zamanlar".

function job({ id, connectionId = null, jobType, status, attemptCount = 1, maxAttempts = 5, createdAt, startedAt = createdAt, completedAt = null, marketplace, externalId = null, errorCode = null, errorSummary = null, batchCount = 1, progress = null, correlationId }) {
  const failed = ['BLOCKED', 'MANUAL_REVIEW', 'DEAD', 'RETRY_SCHEDULED'].includes(status)
  return {
    id, connectionId, jobType, status, attemptCount, maxAttempts, availableAt: createdAt, lastErrorCode: errorCode, lastErrorSummary: errorSummary,
    correlationId, createdAt, startedAt: status === 'PENDING' ? null : startedAt, completedAt, marketplace, externalId,
    firstFailedAt: failed ? startedAt : null, lastFailedAt: failed ? completedAt ?? startedAt : null, nextRetryAt: status === 'RETRY_SCHEDULED' ? '2026-10-20T09:45:00+03:00' : null,
    batchCount, progressCurrent: progress?.current ?? 0, progressTotal: progress?.total ?? null, progressPercent: progress ? Math.round(progress.current / progress.total * 100) : null,
    progressLabel: progress?.label ?? null, progressReceived: progress?.current ?? 0, progressProcessed: progress ? progress.current - (progress.failed ?? 0) : 0, progressSkipped: 0, progressFailed: progress?.failed ?? 0,
    lastAttemptStartedAt: status === 'PENDING' ? null : startedAt, heartbeatAt: status === 'LEASED' ? '2026-10-20T09:29:40+03:00' : null
  }
}

const jobs = [
  job({ id: 'job-01', connectionId: 'c-ty', jobType: 'TRENDYOL_ORDER_SYNC', status: 'LEASED', createdAt: '2026-10-20T09:28:00+03:00', startedAt: '2026-10-20T09:28:05+03:00', marketplace: 'Trendyol', correlationId: 'corr-7f3a91c2', progress: { current: 140, total: 220, label: 'Sipariş paketleri okunuyor' } }),
  job({ id: 'job-02', connectionId: 'c-hb', jobType: 'HEPSIBURADA_ORDER_SYNC', status: 'SUCCEEDED', createdAt: '2026-10-20T09:15:00+03:00', startedAt: '2026-10-20T09:15:02+03:00', completedAt: '2026-10-20T09:15:41+03:00', marketplace: 'Hepsiburada', correlationId: 'corr-1b88e0d4' }),
  job({ id: 'job-03', connectionId: 'c-ty', jobType: 'PRICE_INVENTORY_SYNC', status: 'RETRY_SCHEDULED', attemptCount: 2, createdAt: '2026-10-20T08:52:00+03:00', startedAt: '2026-10-20T09:22:00+03:00', completedAt: null, marketplace: 'Trendyol', externalId: 'RVN-TSH-0042-LCV-S', errorCode: 'RATE_LIMITED', errorSummary: 'Trendyol istek sınırı aşıldı; 15 dakika sonra yeniden denenecek.', correlationId: 'corr-c04d55aa' }),
  job({ id: 'job-04', connectionId: 'c-ty', jobType: 'PRODUCT_PUBLICATION', status: 'MANUAL_REVIEW', attemptCount: 1, createdAt: '2026-10-20T07:40:00+03:00', startedAt: '2026-10-20T07:40:04+03:00', completedAt: '2026-10-20T07:41:10+03:00', marketplace: 'Trendyol', externalId: 'RVN-KZK-0777', errorCode: 'CREATE_REJECTED', errorSummary: 'Kategori özelliği "Kumaş Tipi" zorunlu; ürün reddedildi.', correlationId: 'corr-90ab3e17', progress: { current: 3, total: 3, label: 'Varyantlar gönderildi', failed: 3 } }),
  job({ id: 'job-05', connectionId: 'c-ef', jobType: 'EFATURAM_INVOICE_CREATE', status: 'SUCCEEDED', createdAt: '2026-10-20T06:05:00+03:00', startedAt: '2026-10-20T06:05:03+03:00', completedAt: '2026-10-20T06:05:19+03:00', marketplace: 'Trendyol', externalId: 'RVN2026000418', correlationId: 'corr-44e2b6f0' }),
  job({ id: 'job-06', connectionId: 'c-n11', jobType: 'N11_ORDER_RECOVERY_SYNC', status: 'PENDING', attemptCount: 0, createdAt: '2026-10-20T09:29:00+03:00', marketplace: 'N11', correlationId: 'corr-5d17f9b8' }),
  job({ id: 'job-07', connectionId: 'c-pz', jobType: 'PAZARAMA_RETURN_SYNC', status: 'SUCCEEDED', createdAt: '2026-10-20T03:30:00+03:00', startedAt: '2026-10-20T03:30:01+03:00', completedAt: '2026-10-20T03:31:12+03:00', marketplace: 'Pazarama', correlationId: 'corr-e8c1023f' }),
  job({ id: 'job-08', connectionId: 'c-sh', jobType: 'SHOPIFY_PRODUCT_SYNC', status: 'SUCCEEDED', createdAt: '2026-10-19T23:10:00+03:00', startedAt: '2026-10-19T23:10:02+03:00', completedAt: '2026-10-19T23:14:47+03:00', marketplace: 'Shopify', correlationId: 'corr-2a6f7d90', progress: { current: 86, total: 86, label: 'Ürünler aktarıldı' } }),
  job({ id: 'job-09', connectionId: 'c-hb', jobType: 'HEPSIBURADA_SHIPMENT_ACTION', status: 'DEAD', attemptCount: 5, createdAt: '2026-10-19T18:20:00+03:00', startedAt: '2026-10-19T21:20:00+03:00', completedAt: '2026-10-19T21:20:09+03:00', marketplace: 'Hepsiburada', externalId: 'HB-4471029', errorCode: 'PACKAGE_ALREADY_SHIPPED', errorSummary: 'Paket Hepsiburada tarafında zaten kargoya verilmiş.', correlationId: 'corr-0fd3a6e5' }),
  job({ id: 'job-10', connectionId: 'c-ty', jobType: 'CATALOG_STRUCTURE_IMPORT', status: 'CANCELLED', attemptCount: 1, createdAt: '2026-10-19T14:45:00+03:00', startedAt: '2026-10-19T14:45:03+03:00', completedAt: '2026-10-19T14:47:30+03:00', marketplace: 'Trendyol', correlationId: 'corr-b7722c41' }),
  job({ id: 'job-11', connectionId: 'c-ty', jobType: 'TRENDYOL_REFERENCE_SYNC', status: 'SUCCEEDED', createdAt: '2026-10-18T04:00:00+03:00', startedAt: '2026-10-18T04:00:02+03:00', completedAt: '2026-10-18T04:12:40+03:00', marketplace: 'Trendyol', correlationId: 'corr-6c90e3a1' }),
  job({ id: 'job-12', connectionId: 'c-n11', jobType: 'N11_ORDER_SYNC', status: 'BLOCKED', attemptCount: 3, createdAt: '2026-10-17T12:00:00+03:00', startedAt: '2026-10-17T12:30:00+03:00', completedAt: '2026-10-17T12:30:04+03:00', marketplace: 'N11', errorCode: 'CREDENTIAL_REJECTED', errorSummary: 'N11 API anahtarı reddedildi; bağlantı bilgilerini yenileyin.', correlationId: 'corr-d1e55b02' }),
  job({ id: 'job-13', connectionId: 'c-sh', jobType: 'CONNECTION_TEST', status: 'SUCCEEDED', createdAt: '2026-10-15T10:00:00+03:00', startedAt: '2026-10-15T10:00:01+03:00', completedAt: '2026-10-15T10:00:03+03:00', marketplace: 'Shopify', correlationId: 'corr-83f0aa6d' }),
  job({ id: 'job-pub-p1-trendyol', connectionId: 'c-ty', jobType: 'PRODUCT_PUBLICATION', status: 'SUCCEEDED', createdAt: '2026-10-12T11:00:00+03:00', startedAt: '2026-10-12T11:00:02+03:00', completedAt: '2026-10-12T11:03:20+03:00', marketplace: 'Trendyol', externalId: 'RVN-TRC-2401', correlationId: 'corr-pub-2401', progress: { current: 4, total: 4, label: 'Varyantlar yayında' } })
]

function jobsMatching(url) {
  const since = url.searchParams.get('since')
  const statuses = (url.searchParams.get('status') ?? '').split(',').filter(Boolean)
  const types = (url.searchParams.get('jobTypes') ?? '').split(',').filter(Boolean)
  return jobs.filter(item => (!since || new Date(item.createdAt).getTime() >= new Date(since).getTime())
    && (!statuses.length || statuses.includes(item.status))
    && (!types.length || types.includes(item.jobType)))
}

function jobCounts({ url }) {
  const counts = new Map()
  for (const item of jobsMatching(url)) {
    const key = `${item.status}|${item.jobType}`
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return [...counts.entries()].map(([key, count]) => { const [status, jobType] = key.split('|'); return { status, jobType, count } })
}

const jobOrder = { orderId: 'o-1042', orderNumber: '10428871234', externalOrderId: '10428871234', status: 'SHIPPED', currency: 'TRY', netAmount: 2499.9, orderedAt: '2026-10-19T13:12:00+03:00', externalPackageId: 'PKG-3381920', cargoProvider: 'Trendyol Express', cargoTrackingNumber: '7280012345671', customerName: 'Elif Demir', lineCount: 2 }
function jobDetail({ match }) {
  const item = jobs.find(entry => entry.id === decodeURIComponent(match[1]))
  if (!item) return undefined
  const attempts = Array.from({ length: item.attemptCount }, (_, index) => {
    const last = index === item.attemptCount - 1
    const succeeded = last && item.status === 'SUCCEEDED'
    return { attemptNumber: index + 1, startedAt: item.startedAt ?? item.createdAt, completedAt: last ? item.completedAt : item.startedAt, succeeded, errorCode: succeeded || (last && item.status === 'LEASED') ? null : item.lastErrorCode, errorSummary: succeeded || (last && item.status === 'LEASED') ? null : item.lastErrorSummary }
  })
  const orderJob = /ORDER|SHIPMENT/.test(item.jobType)
  return {
    job: item, attempts,
    order: item.jobType.includes('SHIPMENT') ? jobOrder : null,
    change: null,
    relatedOrders: orderJob && !item.jobType.includes('SHIPMENT') ? [jobOrder, { ...jobOrder, orderId: 'o-1043', orderNumber: '10428871299', externalOrderId: '10428871299', status: 'CREATED', netAmount: 899, customerName: 'Mert Kaya', lineCount: 1, cargoTrackingNumber: null }] : [],
    scan: orderJob ? { mode: 'INCREMENTAL', label: 'Artımlı tarama', detail: 'Son başarılı taramadan bu yana değişen siparişler okunur.', window: '20.10.2026 09:13 – 09:28', plannedIntervalSeconds: 300, plannedIntervalLabel: '5 dakika', previousScheduledAt: '2026-10-20T09:23:00+03:00', actualIntervalLabel: '5 dakika' } : null,
    failureReasons: item.status === 'MANUAL_REVIEW' ? [{ key: 'MISSING_ATTRIBUTE', title: 'Zorunlu özellik eksik', description: 'Trendyol bu kategoride "Kumaş Tipi" özelliğini zorunlu tutuyor.', technicalDetail: 'CREATE_REJECTED: attributeId=1192 missing', affectedRecords: 3, sampleProductIds: ['p8'] }] : null,
    invoice: item.jobType.includes('INVOICE') ? { invoiceId: 'inv-418', orderNumber: '10428871234', status: 'ISSUED', invoiceType: 'E_ARCHIVE', currency: 'TRY', payableTotal: 2499.9, invoiceNumber: 'RVN2026000418', externalPackageId: 'PKG-3381920' } : null
  }
}

const jobMetrics = [
  { jobType: 'TRENDYOL_ORDER_SYNC', attempts: 288, failed: 3, averageWaitSeconds: 4.2, p95WaitSeconds: 18, averageDurationSeconds: 21, p95DurationSeconds: 64, queued: 0, running: 1 },
  { jobType: 'HEPSIBURADA_ORDER_SYNC', attempts: 96, failed: 0, averageWaitSeconds: 2.1, p95WaitSeconds: 7, averageDurationSeconds: 38, p95DurationSeconds: 71, queued: 0, running: 0 },
  { jobType: 'PRICE_INVENTORY_SYNC', attempts: 412, failed: 57, averageWaitSeconds: 35, p95WaitSeconds: 240, averageDurationSeconds: 3.4, p95DurationSeconds: 9, queued: 6, running: 0 },
  { jobType: 'PRODUCT_PUBLICATION', attempts: 12, failed: 2, averageWaitSeconds: 0.6, p95WaitSeconds: 2, averageDurationSeconds: 66, p95DurationSeconds: 190, queued: 0, running: 0 },
  { jobType: 'N11_ORDER_RECOVERY_SYNC', attempts: 4, failed: 0, averageWaitSeconds: null, p95WaitSeconds: null, averageDurationSeconds: 5400, p95DurationSeconds: 7800, queued: 1, running: 0 }
]
const laneMetrics = [
  { lane: 'USER', attempts: 64, averageWaitSeconds: 0.8, p95WaitSeconds: 3, queued: 0, running: 0 },
  { lane: 'INITIAL', attempts: 4, averageWaitSeconds: 12, p95WaitSeconds: 40, queued: 1, running: 0 },
  { lane: 'HOT', attempts: 610, averageWaitSeconds: 3.1, p95WaitSeconds: 14, queued: 0, running: 1 },
  { lane: 'BACKGROUND', attempts: 230, averageWaitSeconds: 48, p95WaitSeconds: 310, queued: 6, running: 0 }
]
const quotaUsage = [
  { platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol', lane: 'FOREGROUND', limitPerMinute: 50, lastHourRequests: 1260, peakMinuteRequests: 44, averagePerMinute: 21 },
  { platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol', lane: 'BACKGROUND', limitPerMinute: 30, lastHourRequests: 1500, peakMinuteRequests: 30, averagePerMinute: 25 }
]

export const catalog = [
  // Products
  ['GET /products', productPage],
  ['GET /products/summary', productSummary],
  ['^/products/([^/]+)/publication-status/([^/]+)$', publicationStatus],
  ['^/products/([^/]+)/listing-profiles/([^/]+)$', ({ match }) => ({ id: `lp-${match[1]}-${match[2]}`, version: 2 })],
  ['^/products/([^/]+)$', ({ match }) => products.find(item => item.id === decodeURIComponent(match[1])) ?? products[0]],
  ['GET /catalog/categories', { items: categories, totalCount: categories.length, hasMore: false, nextCursor: null }],
  ['GET /catalog/brands', { items: brands, totalCount: brands.length, hasMore: false, nextCursor: null }],
  ['^/catalog/categories/([^/]+)/attribute-requirements$', ({ match }) => match[1] === 'cat-trenckot' ? trenchRequirements : genericRequirements],

  // Publication readiness (only reached for a channel where the product is not live)
  ['^/mappings/categories/([^/]+)$', categoryMapping],
  ['GET /reference-data/categories', referenceCategories],
  ['^/reference-data/categories/([^/]+)/attributes$', referenceAttributes],
  ['^/reference-data/categories/([^/]+)/attributes/([^/]+)/values$', { snapshotId: referenceSnapshot, items: [] }],
  ['GET /mappings/attributes', []],
  ['GET /mappings/attribute-values', []],

  // Per-connection details used by the product editor and the integration cards
  ['^/connections/([^/]+)/capabilities$', capabilities],
  ['^/connections/([^/]+)/initial-import$', initialImport],
  ['^/connections/([^/]+)/attribute-defaults$', attributeDefaults],
  ['^/connections/([^/]+)/instant-notifications$', instantNotifications],
  ['^/connections/([^/]+)/sync-policies$', syncPolicies],
  ['^/connections/([^/]+)/n11-shipment-templates$', ['Standart Kargo', 'Ücretsiz Kargo 500 TL Üzeri']],

  // Jobs
  ['GET /jobs', ({ url }) => jobsMatching(url)],
  ['GET /jobs/counts', jobCounts],
  ['GET /jobs/metrics', jobMetrics],
  ['GET /jobs/metrics/lanes', laneMetrics],
  ['GET /jobs/metrics/quota', quotaUsage],
  ['^/jobs/([^/]+)$', jobDetail],
]
