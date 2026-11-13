# Frontend Denetimi, Bölüm 1: dashboard, reports, operations, invoicing, shipping, app

Kapsam: `src/MarketplaceHub.Web/src/{app,features/dashboard,features/reports,features/operations,features/invoicing,features/shipping}`.
Kaynaklar repo içi dosyalardır (yol:satır). Kaynak kod hiç değiştirilmedi; yalnız `npm ci` ile node_modules kuruldu.
Satır numaraları 5 Kasım 2026 çalışma ağacına aittir.

## 1. Statik kontroller ve testler

### Takeaway
`npm ci`, `npm run typecheck`, `npm run lint` ve kapsamdaki vitest paketleri temiz geçti. Aşağıdaki hataların hiçbirini mevcut testler yakalamıyor.

### Cited Findings
- `npm ci`: EXIT 0 (node_modules yoktu, kuruldu). — [package.json](src/MarketplaceHub.Web/package.json)
- `npm run typecheck` (`tsc -b --pretty false`): EXIT 0, hata yok.
- `npm run lint` (`eslint . --max-warnings=0`): EXIT 0, uyarı yok.
- `npx vitest run src/features/dashboard src/features/reports src/features/operations src/features/invoicing src/features/shipping`: "Test Files 37 passed (37), Tests 267 passed (267)", EXIT 0.

### Inferences
- Sorunlar tip veya lint düzeyinde değil; durum yönetimi, tarih ve UX mantığında. Testi olmayan yollarda kalıyorlar (Hakediş arama sayfalaması, sayfa düzeyi platform filtresi, useReveal'ın geç gelen veriyle çalışması).

### Gaps
- `npm run build` ve `npm run check:bundle` istenmediği için çalıştırılmadı.

## 2. Genel Bakış (dashboard)

### Takeaway
Dashboard dikkatli yazılmış: İstanbul günleri kullanılıyor, yükleme sırasında KPI'lar gizleniyor, bilinmeyen sayılar "—" gösteriliyor. Bulunanlar az ve düşük/orta önemde: iç içe etkileşimli öğeler, ARIA tab yapısının eksik olması ve sözleşmedeki bir eski yorum.

### Cited Findings
- **[Kesin][Orta, a11y] İç içe etkileşimli öğe.** Operasyon kartının "?" işareti `<span role="button" tabIndex=0>` olarak `<Link>` (yani `<a>`) içinde çiziliyor (Bekleyen Siparişler, Geciken, İadeler, İptal kartları). Ekran okuyucu ve klavye için geçersiz bir yapı: Tab ile önce link, sonra içindeki düğme odaklanıyor. Enter/Space'te `preventDefault` var, ama yardımcı teknolojiler link içindeki düğmeyi ayrı duyurmaz ya da yanlış duyurur. — [DashboardPage.tsx:342-349](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)
- **[Kesin][Düşük, a11y] Eksik tablist yapısı.** `role="tablist"` içinde 3 KpiCell `role="tab"` alıyor, 4. hücre (Aktif Ürün) rolsüz kalıyor. Hiçbir tab'ın `aria-controls`'u ve bağlı bir `tabpanel`'i yok. Ok tuşuyla gezinme de yok (her hücre tabIndex=0). — [DashboardPage.tsx:164, 357-361](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)
- **[Kesin][Düşük, a11y] Ok tuşu desteği olmayan radiogroup.** Dönem düğmeleri `role="radiogroup"` / `role="radio"`, ama ok tuşu desteği ve roving tabindex yok. Her düğme ayrı Tab durağı. — [DashboardPage.tsx:354](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)
- **[Kesin][Düşük] trend() önceki dönem 0 iken.** Önceki dönem 0 ya da negatif olunca, mevcut değer >0 ise "%100 ▲" gösteriliyor. Matematiksel olarak tanımsız bir artış sabit %100 diye sunuluyor. Senaryo: dün hiç sipariş yok, bugün 1 sipariş var, ekranda "%100 önceki döneme göre" çıkıyor. — [DashboardPage.tsx:52-56](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)
- **[Kesin][Düşük, sözleşme yorumu] Backend yorumu ile uygulama çelişiyor.** `DashboardPerformanceView` yorumu "dönem bugün bitiyorsa önceki dönem aynı saatte durur" diyor. Uygulama (`DashboardPerformancePeriod`) ve frontend ise yalnız "Bugün" için aynı saat kesimi yapıyor; 7/30/90 gün bugünü tam gün sayıp önceki tam günlerle karşılaştırıyor (sahip kararı, 21 Eki 2026). Kod tutarlı, `DashboardContracts.cs` yorumu eskimiş. Bunun sonucu: "Son 7 Gün" trendi gün içinde sistematik olarak aşağı yönlü görünür (bugün kısmi, önceki 7 gün tam). — [DashboardContracts.cs](src/MarketplaceHub.Application/DashboardContracts.cs); [DashboardReadService.cs:514-517](src/MarketplaceHub.Infrastructure/Persistence/DashboardReadService.cs); [dashboard-performance.ts:44-49](src/MarketplaceHub.Web/src/features/dashboard/dashboard-performance.ts)
- **[Kesin][Bilgi] Bu noktalar kontrol edildi, hata değil.** Yükleme sırasında trend/ortalama/aralık CSS ile gizleniyor (`.rr-kc-wait :is(.rr-trend,.rr-avg,.rr-krange){visibility:hidden}`). Saatlik anahtar "yyyy-MM-ddTHH:00" backend `DashboardPerformanceHourView.Hour` ile uyumlu. `PendingUnitsByConnection` decimal→number uyumlu. CSV, `reportReady` (placeholder ve fetching yokken) kontrolüyle korunuyor. — [dashboard-sade.css:193](src/MarketplaceHub.Web/src/features/dashboard/dashboard-sade.css); [dashboard-performance.ts:134-148](src/MarketplaceHub.Web/src/features/dashboard/dashboard-performance.ts)
- **[Kesin][Düşük] Aynı query key, farklı seçenekler.** `['dashboard-bootstrap']` ve `['invoice-workspace-summary']` hem Shell'de (App.tsx, `hubApi` başlıksız, refetchInterval yok) hem Dashboard'da (`poll` başlığıyla, 60 sn interval) farklı queryFn ile kayıtlı. Hangi gözlemcinin tetiklediğine göre istek "otomatik" (backgroundPoll) ya da "kullanıcı" olarak gidiyor. Shell'in focus refetch'i başlıksız olduğu için boşta oturum sayacını sıfırlayabilir. Etkisi küçük. — [App.tsx:78-79](src/MarketplaceHub.Web/src/app/App.tsx); [DashboardPage.tsx:205-206](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)

### Inferences
- Dashboard'daki gerçek risk görsel/a11y tarafında. Rakam doğruluğu (ciro = amount + returnedAmount; sepet = ciro ÷ sipariş) backend sözleşme yorumuyla tutarlı.

### Gaps
- Canlı veriyle Dashboard ve Raporlar rakam karşılaştırması (read-only SQL) bu görevde yapılmadı (canlıya dokunmama kısıtı).

## 3. Raporlar (reports)

### Takeaway
En somut hatalar burada: (a) Hakediş sekmesi sayfa üstündeki Platform filtresini ilk açılıştan sonra yok sayıyor, (b) Hakediş aramasında sayfa numarası sıfırlanmadığı için yanlış "kayıt bulunamadı" çıkıyor, (c) tarih presetleri İstanbul yerine tarayıcı saatini kullanıyor, (d) tutar filtresi noktalı ondalığı 100 kat büyütüyor, (e) boş veriyle açılan bar listesi sonradan veri gelince hiç dolmuyor.

### Cited Findings
- **[Kesin][Yüksek] Hakediş sayfa düzeyi platform filtresini dinlemiyor.** `PayoutsReport` `platforms` state'ini prop'tan yalnız ilk render'da alıyor (`useState(() => ... pagePlatforms ?? [])`). Sonradan prop değişince güncellemiyor, bileşenin `key`'i de yok. Senaryo: Hakediş sekmesindeyken üstteki "Platform" çoklu seçiminden yalnız Hepsiburada seçilir; filtre çipi değişir ama Hakediş kartları ve listeler Tümü/eski seçimle kalır. Sayfa filtresi görünür olduğu için kullanıcı yanlış kapsamdaki rakamı seçtiği platformun rakamı sanır. — [ReportsPage.tsx:158, 1805-1807](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx); [ReportsPage.tsx:138](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx)
- **[Kesin][Orta] Hakediş aramasında sayfa sıfırlanmıyor.** `PayoutLinesTable` `page` state'ini `search`/`group`/`platforms` değişince sıfırlamıyor. Arama durumunda bileşen aynı konumda render edildiği için instance korunuyor. Senaryo: "123" araması 4 sayfa döndürür, kullanıcı 3. sayfaya gider, sonra 1 sayfalık "456" aramasını uygular. İstek `page=2` ile gider, `items=[]` gelir ve ekranda "Bu numarayla kayıt bulunamadı." görünür, oysa kayıt vardır. OrderProfitability bu durumu `pageScope` ile çözmüş, burada eşdeğeri yok. — [ReportsPage.tsx:1991, 2043-2050](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx); karşılaştırma: [OrderProfitability.tsx:57-61](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)
- **[Kesin][Orta] Rapor tarihleri tarayıcı yerel saatinden hesaplanıyor.** `dayKey`, `lastDays`, `periodPresets` ve `lastWholeMonths` `new Date()` + `getFullYear/getMonth/getDate` kullanıyor, yani tarayıcının saat dilimini. Dashboard ise bilerek `istanbulDateKey()` kullanıyor. Senaryo: Saat dilimi UTC olan bir cihazda İstanbul saatiyle 00:00–03:00 arasında "Bugün" önceki günü, "Bu Ay" ayın ilk saatlerinde önceki ayı getirir. UTC+4 (Bakü) cihazda 23:00 sonrası "Bugün" yarını ister. Aynı anda Genel Bakış ile Raporlar farklı "bugün" gösterir. — [ReportsPage.tsx:47-67, 1624-1631](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx); karşılaştırma: [DashboardPage.tsx:185](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)
- **[Kesin][Orta] Tutar filtresi noktalı ondalığı yanlış okuyor.** `amount()` tüm noktaları binlik ayırıcı sayıp siliyor. Senaryo: iOS İngilizce klavyede `inputMode="decimal"` ondalık tuşu "." üretir, "1500.50" girilir, 150050 olarak okunur ve en az/en çok filtresi 100 kat kayar. — [payouts.ts:100](src/MarketplaceHub.Web/src/features/reports/payouts.ts); [ReportsPage.tsx:1985-1986](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx)
- **[Kesin][Orta, görsel] Geç gelen veride barlar dolmuyor.** `useReveal` effect'i yalnız `[shown]`'a bağlı. `Bars` listesi boşken erken `return` ettiği için `ref` bağlanmıyor ve observer kurulmuyor. Aynı instance'a sonradan veri gelirse effect yeniden çalışmadığı için `shown` hep false kalır ve tüm barlar `width:0%`. Senaryo: satışsız bir dönem açılır ("Gösterilecek değer yok."), sonra satışlı döneme geçilir; şehir/neden barları boş görünür, yalnız metin değerler görünür. (Ayrıca reduced-motion/IO yokken sorun çıkmaz.) — [report-visuals.tsx:10-24](src/MarketplaceHub.Web/src/features/reports/report-visuals.tsx); [ReportsPage.tsx:269-277](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx)
- **[Kesin][Orta] Filtre değişince eski dönemin rakamları kalıyor.** `reports-sales`, `report-order-profit`, `reports-stock`, `reports-payouts`, `reports-payout-lines` sorgularının hepsi `placeholderData: previous => previous` kullanıyor. Dönem/platform değişince yeni dönem etiketi altında eski dönemin KPI'ları ve tabloları kalıyor. Tek işaret küçük bir "Güncelleniyor" yazısı (`reports-refreshing`); KPI'lar gizlenmiyor ve CSV düğmesi aktif kalıyor. Bu durumda indirilen CSV eski döneme ait olur. Dashboard aynı sorunu `isPlaceholderData`'yı bekleyip "—" göstererek çözmüş. CLAUDE.md'deki "a wrong number on screen is worse than a missing one" kuralına ters. — [ReportsPage.tsx:113, 142-143, 243-244, 1676, 1831, 2046](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx); [OrderProfitability.tsx:62, 81-88](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)
- **[Kesin][Düşük] Komisyon bilinmezken 0,00 ₺ gösteriliyor.** Sipariş Analizi modalında `commissionRate` biliniyor ama `commission` null ise `money(line.commission ?? 0)` "Komisyon %X (0,00 ₺)" gösteriyor. Bilinmeyen bir değer 0 olarak sunuluyor. — [OrderProfitability.tsx:138](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx); tip: [OrderProfitability.tsx:16](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)
- **[Kesin][Düşük] Maliyet 0 girilmişse "Maliyet girilmemiş" görünüyor.** `line.costPrice ?` falsy kontrolü yüzünden 0 değeri girilmemiş sayılıyor. — [OrderProfitability.tsx:139](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)
- **[Kesin][Düşük] Hata ile eski tablo aynı anda görünüyor.** `result.isError` olduğunda placeholder/eski veri varken hem ErrorBox hem eski tablo ve özet görünüyor. Kullanıcı hangi rakamın güncel olduğunu ayırt edemiyor. — [OrderProfitability.tsx:81-92](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)
- **[Muhtemel][Düşük] Gece yarısını geçen açık sayfada preset eşleşmesi kayboluyor.** `range` state'i mount anında `lastDays(30)` ile sabitleniyor. Sayfa gece yarısını geçince presetler yeniden hesaplanıyor ama `range` eski günde kalıyor. "Son 30 Gün" vurgusu kaybolur ve dönem "Özel" görünür; veri dünkü pencereyle kalır. — [ReportsPage.tsx:100-110](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx)
- **[Muhtemel][Düşük] Platform sırası cache'i bölüyor.** Query key platform listesinin seçilme sırasına bağlı (`platforms.join(',')`, sıralanmıyor). [Trendyol, HB] ile [HB, Trendyol] aynı veri için ayrı cache girdisi ve ayrı istek üretir. — [report-format.ts:152-156](src/MarketplaceHub.Web/src/features/reports/report-format.ts)
- **[Kesin][Bilgi] Kontrol edildi, hata değil.** PayoutLinesTable tfoot colSpan'leri sütun sayısıyla uyumlu (7+1 / 8+2). OrderProfitability sayfa taşmasını `page > pageCount-1` ile sınırlıyor. `useMonthlyProfitRate` ana raporla aynı `['reports-sales', query]` key şeklini paylaşıyor (cache uyumlu). — [ReportsPage.tsx:2057-2072](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx); [OrderProfitability.tsx:66](src/MarketplaceHub.Web/src/features/reports/OrderProfitability.tsx)

### Inferences
- Raporlar sekmeleri tek bir sayfa filtresine bağlı görünse de Hakediş kendi platform state'ini tutuyor. Bu, "her rakam filtreyi izler" vaadini bozan tasarım tutarsızlığı.
- Tarayıcı saat dilimi hatası yalnız Türkiye dışı ya da yanlış ayarlı cihazlarda tetiklenir, ama Dashboard ile çelişki yaratır.

### Gaps
- `/reports/*` uç noktalarının gelecek tarihli `from` için ne döndürdüğü (Dashboard'daki "Rapor dönemi gelecekte başlayamaz" 400'ünün benzeri var mı) kontrol edilmedi.
- ReportsPage.tsx 2080 satır. 260–1600 arası (Overview, Profit, Ops, Customers görünümleri) satır satır okunmadı, yalnız kalıp taramasıyla geçildi. Rakam türetme (moneyBreakdown, profitSteps) ile backend karşılaştırması bu bölümde yapılmadı.
- `ReportsContracts.cs` ile TS `ReportSales` tipi alan alan karşılaştırılmadı.

## 4. Faturalar, İşlem Takibi, Kargo, App kabuğu

### Takeaway
Mutasyon sonrası invalidation genel olarak tam. Sorunlar verimlilik (filtre değişiminde fazladan istek, çok sayfalı listede tam yeniden çekim) ve render sırasında yan etki düzeyinde.

### Cited Findings
- **[Kesin][Düşük] Filtre değişiminde fazladan istek.** Faturalar sayfası filtre değişince sayfa numarasını `useEffect(() => setPageNumber(1), [...])` ile sıfırlıyor. İlk render yeni filtre ama eski `pageNumber` ile bir istek atıyor, effect'ten sonra page=1 ile ikinci istek gidiyor. Yani her filtre veya arama değişimi 2 istek. 3. sayfadayken platform değişince önce "yeni filtre / sayfa 3" isteği gider. — [invoicing/pages.tsx:375-384, 562](src/MarketplaceHub.Web/src/features/invoicing/pages.tsx)
- **[Kesin][Düşük] Çok sayfalı iş listesinde tam yeniden çekim.** İşlem Takibi'nde "Yenile" ve her mutasyon `invalidateQueries({ queryKey: ['jobs'] })` çağırıyor. Bu, `['jobs','page',…]` infinite query'nin yüklü TÜM sayfalarını sırayla yeniden çekiyor; `['jobs','page-head']` ve `['jobs','counts']` da buna ek. Kullanıcı "Daha fazla" ile 10 sayfa yüklediyse tek tık 12 istek üretir. Sayfa ayrıca yalnız ilk sayfayı yoklayacak şekilde tasarlanmış, bu invalidation o tasarımı bypass ediyor. — [operations/pages.tsx:424-443, 459, 476, 539-543](src/MarketplaceHub.Web/src/features/operations/pages.tsx)
- **[Kesin][Düşük] Render sırasında yan etki.** App render gövdesinde `setStorageScope(...)` ve (401'de) `clearQuerySnapshot()` çağrılıyor. StrictMode'da ikişer kez çalışırlar; idempotent oldukları sürece zararsız ama React kuralı ihlali. — [App.tsx:255, 259](src/MarketplaceHub.Web/src/app/App.tsx)
- **[Kesin][Bilgi] Kontrol edildi, hata değil.** Fatura mutasyonları `invoice-workspace`, `-page`, `-summary` (gerekli yerde `orders`, `jobs`, `invoice`) key'lerini invalidate ediyor. Önizleme modalında `active` bayrağıyla unmount sonrası setState engelleniyor. Fatura sorgusu sağlayıcı kimlik bilgisi bilinene kadar (`enabled: !connections.isPending`) bekletiliyor. Sayfa sıkıştırma yalnız gerçek cevapla (`!isPlaceholderData`) yapılıyor. — [invoicing/pages.tsx:385-487](src/MarketplaceHub.Web/src/features/invoicing/pages.tsx); [InvoiceWorkspacePreviewModal.tsx:71-80](src/MarketplaceHub.Web/src/features/invoicing/InvoiceWorkspacePreviewModal.tsx)
- **[Kesin][Bilgi] Kargo etiketi index key'leri sorun değil.** `ShippingLabelSurface` `key={index}` kullanıyor, ama liste statik layout parçaları ve yeniden sıralanmıyor, bu yüzden pratik bir hata yok. — [ShippingLabelSurface.tsx:41-50](src/MarketplaceHub.Web/src/features/shipping/ShippingLabelSurface.tsx)
- **[Kesin][Bilgi] Query client ayarları.** Varsayılanlar: retry 1 (yalnız 5xx/ağ), staleTime 60 sn, refetchOnWindowFocus false. Reload'da `restoreQuerySnapshot` ile cache geri yükleniyor; `['me']` snapshot'tan geri yüklenmeyip tenant doğrulanıyor. — [main.tsx](src/MarketplaceHub.Web/src/main.tsx); [App.tsx:218-236](src/MarketplaceHub.Web/src/app/App.tsx)

### Inferences
- Faturalar ve İşlem Takibi'nde veri bozan bir hata bulunmadı; bulunanlar sunucu yükü ve verimlilikle ilgili.

### Gaps
- `operations/pages.tsx` (615 satır) ve `invoicing/pages.tsx` (849 satır) içindeki JSX ve satır işlemleri (toplu fatura, seçim modu) satır satır okunmadı.
- `shipping/shipping-label.ts` desi ve tarih biçimlendirmesi ayrıntılı incelenmedi; grep'te Date/Intl kullanımı çıkmadı.

## 5. UI kuralları (CLAUDE.md) ve sözleşme uyumu

### Takeaway
36 px kontrol ve 7/12/14 radius kurallarından birkaç sapma var. Bunların bir kısmı reports-design skill'inin kendi kuralıyla (filtre kontrolleri 40 px) çelişiyor; kural sahibinin karar vermesi gerekiyor.

### Cited Findings
- **[Kesin][Düşük, kural çelişkisi] Rapor filtreleri 40 px.** Rapor filtre kontrolleri (Platform, Özel Dönem tarih seçici, select) 40 px. CLAUDE.md "Controls 36 px" diyor, `.claude/skills/reports-design/SKILL.md:24` ise "Filter controls share one label style and one height (40px)" diyor. İki kural birbirini tutmuyor. — [reports.css:431, 470](src/MarketplaceHub.Web/src/features/reports/reports.css); [reports-design SKILL.md:24](.claude/skills/reports-design/SKILL.md); [CLAUDE.md](CLAUDE.md)
- **[Kesin][Düşük] 36 px dışındaki kontrol yükseklikleri.** reports.css:74 arama input 34 px; :255 kart select 32 px; :414/:429 segmented düğmeler 30/32 px; :698 tarife platform düğmeleri 34 px (min-height reset yok, global 44 px kuralı geçerli olabilir); jobs-sade.css:226 durum çipleri 30 px; invoices-sade.css:28 sekme düğmeleri 30 px; Dashboard `.rr-seg > button` 28 px. Mobilde 40 px (invoices-responsive.css:103, 257, 331; reports.css:229) dokunma hedefi gerekçeli olabilir. — [reports.css](src/MarketplaceHub.Web/src/features/reports/reports.css); [jobs-sade.css:226](src/MarketplaceHub.Web/src/features/operations/jobs-sade.css); [invoices-sade.css:28](src/MarketplaceHub.Web/src/features/invoicing/invoices-sade.css); [dashboard-sade.css:220](src/MarketplaceHub.Web/src/features/dashboard/dashboard-sade.css)
- **[Muhtemel][Düşük] min-height reset eksik.** `.reports-tariff-platforms button { height: 34px }` `min-height: 0` vermiyor. Global `button { min-height: 44px }` kuralı geçerliyse gerçek yükseklik 44 px olur (CLAUDE.md: compact kontroller `min-height: 0` ile sıfırlanmalı). — [reports.css:698](src/MarketplaceHub.Web/src/features/reports/reports.css)
- **[Kesin][Düşük] Kural dışı radius değerleri.** Kapsamdaki CSS'lerde 3 px (12 kez), 4 px (6), 5 px (3), 2 px (3), 6 px (2) bulundu. Çoğu ilerleme çubuğu/bar izi (kabul edilebilir). Kontrol niteliğinde olanlar: invoices-sade.css:180 checkbox 4 px, invoices-sade.css:215 logo 6 px, reports.css:172 4 px. — [invoices-sade.css:180, 215](src/MarketplaceHub.Web/src/features/invoicing/invoices-sade.css); [reports.css:132-172](src/MarketplaceHub.Web/src/features/reports/reports.css)
- **[Kesin][Bilgi] Dashboard sözleşmesi uyumlu.** TS `DashboardMetrics`, `Performance`, `PerformanceRow`, `HourRow`, `DashboardChannel` alanları C# `DashboardMetricsView`, `DashboardPerformanceView`, `DashboardPerformanceRowView`, `DashboardPerformanceHourView`, `DashboardChannelView` ile ad ve null'luk bakımından uyumlu. TS'de `GeneratedAt` ve `Version` eksik ama kullanılmıyor. — [DashboardContracts.cs](src/MarketplaceHub.Application/DashboardContracts.cs); [dashboard-performance.ts:8-19](src/MarketplaceHub.Web/src/features/dashboard/dashboard-performance.ts); [DashboardPage.tsx:25-32](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)

### Inferences
- Görsel kural sapmaları tek tek düşük önemde. Asıl iş, 36 ile 40 px çelişkisini kural sahibinin tek kurala bağlaması.

### Gaps
- Title Case etiket taraması sistematik yapılmadı (örnek bakışta "Raporu İndir", "Kanalları Yönet", "Gelir Verisi Yok" uygun). Cümle biçimli metinler bilgi notlarında (ör. "Okunan mağazalar: …") ve muhtemelen etiket sayılmaz.
- Sözleşme karşılaştırması yalnız Dashboard için yapıldı. `ReportsContracts.cs`, `PayoutContracts.cs` ve `InvoicingContracts.cs` ile TS tipleri karşılaştırılmadı.
