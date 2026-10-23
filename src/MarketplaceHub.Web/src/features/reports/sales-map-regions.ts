// The sales map's regional breakdown: Turkey's seven geographical regions by their provinces, and the export countries
// by the part of the world they are in. Provinces are written as the report writes them (TurkishProvinces.Names).
export type MapZone = { key: string; label: string; members: string[] }

export const turkeyZones: MapZone[] = [
  { key: 'marmara', label: 'Marmara', members: ['Edirne', 'Kırklareli', 'Tekirdağ', 'İstanbul', 'Kocaeli', 'Yalova', 'Sakarya', 'Bilecik', 'Bursa', 'Balıkesir', 'Çanakkale'] },
  { key: 'ege', label: 'Ege', members: ['İzmir', 'Manisa', 'Aydın', 'Denizli', 'Kütahya', 'Afyonkarahisar', 'Uşak', 'Muğla'] },
  { key: 'akdeniz', label: 'Akdeniz', members: ['Antalya', 'Isparta', 'Burdur', 'Mersin', 'Adana', 'Hatay', 'Osmaniye', 'Kahramanmaraş'] },
  { key: 'ic-anadolu', label: 'İç Anadolu', members: ['Ankara', 'Konya', 'Kayseri', 'Eskişehir', 'Sivas', 'Kırıkkale', 'Aksaray', 'Karaman', 'Kırşehir', 'Niğde', 'Nevşehir', 'Yozgat', 'Çankırı'] },
  { key: 'karadeniz', label: 'Karadeniz', members: ['Rize', 'Trabzon', 'Artvin', 'Giresun', 'Ordu', 'Samsun', 'Sinop', 'Kastamonu', 'Bartın', 'Zonguldak', 'Karabük', 'Düzce', 'Bolu', 'Amasya', 'Tokat', 'Çorum', 'Gümüşhane', 'Bayburt'] },
  { key: 'dogu-anadolu', label: 'Doğu Anadolu', members: ['Erzurum', 'Erzincan', 'Kars', 'Ağrı', 'Iğdır', 'Ardahan', 'Malatya', 'Elazığ', 'Tunceli', 'Bingöl', 'Muş', 'Bitlis', 'Van', 'Hakkari'] },
  { key: 'guneydogu', label: 'Güneydoğu Anadolu', members: ['Gaziantep', 'Şanlıurfa', 'Diyarbakır', 'Mardin', 'Batman', 'Siirt', 'Şırnak', 'Kilis', 'Adıyaman'] }
]

// ISO 3166-1 alpha-2 codes; a country in none of them falls under "Diğer".
export const worldZones: MapZone[] = [
  { key: 'europe', label: 'Avrupa', members: ['BG', 'RO', 'GR', 'DE', 'NL', 'BE', 'FR', 'AT', 'CH', 'GB', 'HU', 'CZ', 'SK', 'PL', 'HR', 'SI', 'CY', 'IT', 'ES', 'SE', 'DK', 'NO', 'RS', 'BA', 'MK', 'AL', 'XK', 'UA', 'MD', 'IE', 'PT', 'FI', 'LT', 'LV', 'EE', 'LU', 'ME'] },
  { key: 'gulf', label: 'Orta Doğu ve Körfez', members: ['SA', 'AE', 'QA', 'KW', 'BH', 'OM', 'IQ', 'JO', 'LB', 'IL', 'SY', 'YE', 'IR'] },
  { key: 'caucasus', label: 'Kafkasya ve Orta Asya', members: ['AZ', 'GE', 'AM', 'KZ', 'UZ', 'TM', 'KG', 'TJ'] },
  { key: 'africa', label: 'Afrika', members: ['LY', 'EG', 'TN', 'DZ', 'MA'] },
  { key: 'america', label: 'Amerika', members: ['US', 'CA', 'MX', 'BR'] }
]

export const otherZone: MapZone = { key: 'other', label: 'Diğer', members: [] }
