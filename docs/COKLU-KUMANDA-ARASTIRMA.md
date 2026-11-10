# Çoklu kumanda araştırması — DS5 Untethered

Tarih: 2026-11-10 · Depo: `Kopru` (master) · Yalnızca okuma; kod değiştirilmedi.

Etiketler: **[KOD]** kodumuzda doğrulandı · **[KAYNAK]** herkese açık bir kaynak söylüyor (bağlantı verildi) · **[TAHMİN]** benim hesabım / yorumum, ölçülmedi.

---

## Özet (yarım sayfa)

**Bugün ne çalışıyor (kodda; donanımda hiç denenmedi):** `BridgeManager` 4 slota kadar kumandayı alıyor. Her slot kendi
`BridgeHost`'unu çalıştırıyor. Her birinin kendi sanal USB DualSense'i, kendi loopback USB/IP sunucusu, bus id'si (`1-1`…`1-4`)
ve sanal BT adresi var. Bu yüzden 2–4. slotlarda da **haptik, tetik, ışık, hoparlör ve mikrofon aktarımı zaten var**; her host tam bir köprü.
Her kumanda HidHide ile ayrı ayrı gizleniyor ve kendi profilini kullanabiliyor. Kumanda seçici Genel bakış, Işık ve Kumanda testi
sayfalarında, kenar çubuğunda da bir liste var. **Yalnızca 1. slotta** olanlar şunlar: kablo geçişi, köprü kapalıyken pil yoklaması,
uyku koruması, adaptör testi, düşük pil bildirimi ve Düşük Güç'ün otomatik açılması (seçili ya da 1. kumandaya bakıyor). Slot hafızası
yalnızca bellekte tutuluyor, uygulama kapanınca siliniyor.

**Önce yapılmaya değer 5 şey:**
1. **Ses uç noktası ↔ kumanda eşleşmesini doğrulamak ve göstermek (S–M).** Oyunlar haptiği genelde HID cihazıyla aynı *ContainerID*'ye
   sahip ses çıkışına gönderiyor. Sanal pedlerimizde bu eşleşme bozuksa ikinci oyuncunun haptiği birinci kumandaya gider, hatta tek
   kumandada bile hiç çalışmaz. Önce ölçülmeli. Bu, tek kumandada bile en büyük bilinmeyen.
2. **Çoklu kumanda adaptör testi ve canlı bant genişliği uyarısı (M).** Hesabıma göre bir kumanda haptikle birlikte havanın %75–100'ünü
   kullanıyor. 3–4 kumandada haptik artık sığmayabilir. Kullanıcıya "kaç kumanda haptikle rahat çalışır" bilgisi gösterilmeli.
3. **Slot kararlılığı: kısa kopmalarda sanal pedi takılı tutmak (M).** Bugün kumanda düşünce sanal ped hemen çıkarılıyor. Oyun 2. oyuncuyu
   kaybediyor ve geri gelince sıralama karışabiliyor.
4. **Kalıcı "hatırlanan kumandalar" ile slot, profil ve isim (S, paralel iş hazır olunca).** Ayrıca slotları elle yeniden sıralamak ve
   "Tanı" düğmesi (titreşim + oyuncu LED'i yakıp söndürme).
5. **Bant genişliğine göre otomatik düşürme (M).** N>1 olduğunda hoparlör/mikrofonu kapatmak, rapor bütçesine göre uyarmak, isteğe bağlı
   olarak 2+ kumandada haptiği "hafif" moda almak.

**En büyük risk:** Tek bir Classic Bluetooth adaptörünün hava süresi. Tek kumanda zaten adaptörün yoklama kapasitesinin neredeyse tamamını
kullanıyor (ölçtüğümüz ~750 Hz, kuramsal üst sınır olan 800 alışverişe çok yakın). 4 kumandada haptik kesilmeleri ve girdi hızının
~100 Hz'e düşmesi büyük olasılık **[TAHMİN]**. CSR klonu bir dongle'da durum daha da kötü olabilir. Hiçbiri ölçülmedi.
İkinci büyük risk ses tarafı: ContainerID ve varsayılan cihaz karmaşası.

---

## 1. Bugünkü durum (kodda doğrulandı)

### 1.1 Mimari [KOD]
| Konu | Durum | Nerede |
|---|---|---|
| En fazla kumanda | 4 (`BridgeManager.MaxControllers`) | `src/Kopru.Core/Bridge/BridgeManager.cs` |
| Slot başına sanal ped | Her slotta ayrı `BridgeHost` → ayrı `VirtualDualSense` (ya da Xbox 360 / DS4), ayrı `UsbIpServer` (port 0 = rastgele loopback portu), bus id `1-1`…`1-4`, sanal BT adresi `02:4B:50:52:55:0N` (feature 0x09) | `BridgeManager.cs`, `VirtualDualSense.BusIdFor`, `DeviceIdentity.ForSlot`, `UsbIpServer.cs` |
| Kumanda ↔ slot bağı | Aygıt örnek kimliği (device instance id) ile. `BoundControllerSource` her hostun yalnızca kendi kumandasını açmasını sağlıyor | `BridgeManager.cs` |
| Slot hafızası | `remembered` sözlüğü: ayrılan kumanda geri gelince eski slotunu alıyor, **meğer** o arada yeni bir kumanda o slota girmiş olsun. Yalnızca bellekte tutuluyor, kalıcı değil | `BridgeManager.Assign/FreeSlot` |
| Tarama | 2 sn'de bir `RefreshAsync` (2–4. slotlardaki hostları başlatır/durdurur) | `RealControllerService.Multi.cs` |
| Bayat ped temizliği | Her slotun kendi `bridge-state-{slot}.json` dosyası | `BridgeManager.CreateDefault`, `CleanupStaleAsync` |
| USB seri numarası | Yakalanan cihaz tanımlayıcısında `iSerialNumber = 0`, yani seri numarası yok. Windows örnek kimliğini porta göre üretir; 4 aynı ped çakışmaz | `captures/*.json` (Device: `…0102 00 01`) |

### 1.2 2–4. slotlarda ne aktarılıyor [KOD]
Her slot tam bir `BridgeHost` olduğu için:
- **Girdi, oyun çıktısı (tetik/ışık/titreşim), oyun haptiği (ses kanal 3–4 → 0x32), hoparlör (kanal 1–2 → 0x36), mikrofon:** evet.
  Her host kendi `Connection`'ında bunları çalıştırıyor. `ApplyTo` hoparlör, mikrofon ve ışık ayarlarını her hosta ayrı ayrı gönderiyor.
- **Ayarlar:** Kumandanın kendi profili varsa o (`SetControllerProfile`), yoksa genel ayarlar uygulanıyor. Oyun modu profili, kendi profili
  olmayan bütün slotlara gidiyor. **Sanal ped türü** (DualSense/Xbox 360/DS4) oyun başına seçiliyor ve **bütün slotlara aynı** gidiyor (`UsePad`).
- **Oyuncu LED'i:** `LightForSlot`: kullanıcı varsayılan "1" desenindeyse 2–4. slotlar kendi numarasını gösteriyor. Kullanıcı başka bir
  desen seçtiyse herkes aynı deseni gösteriyor. Oyun kendi LED'ini gönderirse ne olacağı oyunun çıktısına bağlı.
- **Gizleme:** `HideOtherAsync` her yeni kumandayı aynı `HidingSession`'a ekliyor (`AddDevicesAsync`).
- **Kısayollar:** Herhangi bir kumandadaki PS+D-pad **genel** profili değiştiriyor. Yani 2. oyuncu 1. oyuncunun profilini değiştirebilir.

### 1.3 Yalnızca 1. slotta olanlar [KOD + PLAN]
`docs/PLAN.md`, C bölümü: "Kablo geçişi, pil yoklaması, sağlık ve uyku koruması şimdilik yalnızca 1. slotta".
Kodda gördüklerim:
- `BridgeSleepGuard(powerEvents, host)` yalnızca 1. slotun hostunu yönetiyor (`App.axaml.cs`). Uykudan sonra 2–4. slotlar, yalnızca
  `BridgeManager` taraması onları yeniden başlatırsa döner.
- `CableSwitch` ve köprü kapalıyken pil yoklaması (`ProbeBatteryAsync`) yalnızca `host` için çalışıyor (`RealControllerServiceDevice.cs`).
- **Adaptör testi** `host.RunAdapterCheckAsync` ile **yalnızca 1. kumandayı** ölçüyor (`RealControllerService.cs:343`).
- Düşük pil bildirimi ve rozeti `_service.BatteryPercent` değerine, yani **seçili** kumandaya bakıyor. 2. kumandanın pili biterken ancak o
  seçiliyse uyarı çıkıyor. Bağlanma ve kopma bildirimleri ise slot adıyla veriliyor ("Kumanda 2 bağlandı", `Notifications.Multi.cs`).
- **Düşük Güç** genel bir ayar (`SettingsState.Effective`): ya bütün kumandalarda açık ya hiçbirinde. Otomatik açılma tek bir pil
  değerine bakıyor.
- Sağlık raporu (`HealthReport`) ve Steam Input uyarısı uygulama genelinde. Slot başına sağlık yok. Bekçi (`BridgeWatchdog`) ise her
  hostun kendi bağlantısında var.

### 1.4 Arayüz [KOD + docs/screens/overview-multi-*.png]
- `ControllerPickerViewModel`: birden fazla kumanda varsa görünüyor. Kenar çubuğunda slot numarası, ad ("DualSense"/"DualSense Edge")
  ve pil yüzdesi var. Genel bakış, Işık ve Kumanda testi sayfalarının başında "Kumanda 1 / Kumanda 2" çipleri, Genel bakış'ta da
  "Bu Kumandanın Profili" seçicisi var.
- Hızlı Ayarlar (haptik/tetik gücü, ışık, Düşük Güç, hoparlör, mikrofon) **genel ayarları** değiştiriyor. Seçili kumandanın kendi profili
  varsa ne olduğu belirsiz: seçici yalnızca profil atıyor, hızlı ayarlar yine `Settings` üzerinde.
  **[TAHMİN]** Kullanıcı açısından kafa karıştırıcı.
- Kumandaya kullanıcının verdiği isim, "Tanı" (identify) düğmesi ya da slotları elle sıralama yok.

### 1.5 Testler [KOD]
- `tests/Kopru.Core.Tests/BridgeManagerTests.cs` (10 test): 3 kumandanın ayrı port/bus id/slot almaları, her hostun yalnızca kendi
  kumandasını açması, ayrılıp dönen kumandanın slotunu koruması, 5. kumandanın beklemesi, köprü dururken listelenip köprülenmemesi,
  profil dağıtımı, slot LED'i, slot başına adres, slot başına bayat ped temizliği ve durum dosyası.
- `tests/Kopru.App.Tests/MultiControllerTests.cs` (6 test): iki kumandanın köprülenip gizlenmesi, seçimin tekli üyeleri yönlendirmesi,
  kumandaya özel profilin kaydedilmesi, slot adlı bildirimler, tek kumandada görünümün değişmemesi, seçici, iki kumandalı Genel bakış görüntüsü.
- **Yok:** bant genişliği/eşzamanlı haptik yükü, ses uç noktası eşleşmesi, kopma sırasında slotun korunması, uyku sonrası 2–4. slotlar.

### 1.6 Paralel iş
Kullanıcının isim verdiği kalıcı "hatırlanan kumandalar" kaydı yapılıyor. Bu rapor onun var olacağını varsayıyor:
`remembered` sözlüğünün yerini alıp slot, profil ve isim bilgisini kalıcı hale getirmeli.

---

## 2. Bluetooth kapasitesi

### 2.1 Kaynakların söyledikleri [KAYNAK]
- Classic BT piconet'inde en fazla **7 etkin bağımlı cihaz** var (3 bitlik etkin adres). Ana cihaz (PC) zamanlamayı belirliyor ve
  bağımlıları sırayla yokluyor (poll). [York Univ. notları](https://wiki1.eecs.yorku.ca/course_archive/2008-09/W/4215/_media/notes_may_7.pdf),
  [Freiburg WSN slaytları](https://forum.cone.informatik.uni-freiburg.de/teaching/lecture/wsn-w06/slides/WSN-11.pdf)
- Paket yükleri: DH1 27 B, DH3 183 B, DH5 339 B; 2-DH1 54 B, 2-DH3 367 B, 2-DH5 679 B; **3-DH1 83 B**, 3-DH3 552 B, 3-DH5 1021 B.
  1/3/5 yuvalık paketler, her yuva 625 µs (Bluetooth Core Spec, Cilt 2 Kısım B).
  [R&S CMW belgesi](https://rscmwbluetoothmeas.readthedocs.io/en/latest/Configure_InputSignal_Plength.html)
- Kullanıcı raporları: iki DualSense ile iyi bir adaptörde "3 ms iyi gecikme". Ucuz bir BT 5.3 (Ugreen) dongle'da tek kumandada ~30 ms
  gecikme ve kopmalar. Intel AX200'de birden fazla kumandayla kopma konuları.
  [Intel topluluk forumu](https://community.intel.com/t5/Wireless/AX200-bluetooth-disconnection-with-multiple-game-controllers/m-p/1469130).
  Beş DualSense'li bir kullanıcı kumanda sayısı arttıkça gecikmenin arttığını yazıyor. DS4Windows belgesi, çok kumandada BT yoklama
  aralığını büyütmeyi öneriyor. [DS4Windows: BT input delay](https://kanuan.github.io/DS4WSite/troubleshooting/input-delay-bt)
- CSR 4.0 "klonları" CSR kimliğini taklit ediyor. Güç döngüsünden sonra yeniden bağlanamama raporları var.
  [Ultimarc](https://www.arcaderenovations.com/ultimarc-bluehid-csr-dongle.html).
- Wi-Fi/BT birlikte çalışma: Intel, Nisan 2026'da Windows 11 için "Wi-Fi ile Bluetooth daha iyi bir arada çalışsın" düzeltmeli sürücü
  yayımladı (BT 24.40.0.3). Eski bir Intel yanıtı 2,4 GHz paraziti için 5 GHz kullanmayı öneriyor.
  [Windows Latest](https://www.windowslatest.com/2026/04/30/intel-admits-wi-fi-and-bluetooth-conflicts-on-windows-11-rolls-out-fixes-with-april-2026-driver-update/),
  [HotHardware](https://hothardware.com/news/intel-issues-urgent-windows-11-bluetooth-wifi-fix),
  [Intel topluluk](https://communities.intel.com/thread/104995)
- **Bulamadığım:** Sony'nin ya da bir yonga üreticisinin "bir adaptörde en fazla N DualSense" diyen resmi bir belgesi. DualSense'in BT
  rapor hızının neye bağlı olduğunu (sabit iç hız mı, yoklama mı) anlatan bir belge.

### 2.2 Hava süresi hesabı [TAHMİN — ölçülmedi]
Varsayımlar: girdi 0x31 = 78 B + 1 B HIDP başlığı + 4 B L2CAP = **83 B**. Bu, 3-DH1'in en büyük yüküne tam eşit; yani tek yuvaya sığıyor.
Haptik 0x32 = 142 + 5 = 147 B, 3 yuvalık paket gerektiriyor. Ana cihazın her yoklaması bir ana→bağımlı ve bir bağımlı→ana yuvası
harcıyor, yani en az 2 yuva = 1,25 ms. Saniyede 1600 yuva var.

- Tek yuvalı alışverişle **en fazla 800 girdi/sn** mümkün. Ölçtüğümüz 734–750 Hz (boşta) bu sınıra çok yakın. Buradan, kumandanın kendi
  hızıyla değil **ana cihazın yoklama hızıyla** sınırlandığı sonucu çıkıyor. Bu sonuç da tahmin, ama ölçümle tutarlı.
- Haptikte her 0x32 raporu 3 yuvalık ana paket + 1 yuvalık yanıt = 4 yuva. 93,75/sn × 4 = **375 yuva/sn (~%23)**.
  Yanıt yuvası bir girdi raporu taşıyabilir. Tahmin: tek kumanda haptikle ≈ (1600−375)/2 + 94 ≈ **706 Hz**. Ölçtüğümüz yük altında 661 Hz.
- Hoparlör (0x36) daha büyük paketler ve daha fazla yuva demek. Mikrofon ters yönde ek trafik. Tam boyutları `docs/research/bt-audio.md`'de;
  burada sayısal tahmin yapmadım.

Kumanda sayısına göre kaba tahmin (yeniden gönderim yok, Wi-Fi yok, adil yoklama varsayıldı):

| Kumanda | Haptiksiz, kumanda başına girdi | Hepsi haptikli, kumanda başına girdi | Haptiğin havadan aldığı pay | Beklenti |
|---|---|---|---|---|
| 1 | ~800 Hz | ~700 Hz | %23 | Rahat (ölçüldü: 748/661) |
| 2 | ~400 Hz | ~300 Hz | %47 | Muhtemelen iyi; girdi hızı düşer ama 250 Hz üstünde kalır |
| 3 | ~265 Hz | ~170 Hz | %70 | Sınırda; yeniden gönderimler haptik kesilmesine yol açabilir |
| 4 | ~200 Hz | ~105 Hz | %94 | Haptik büyük olasılıkla sığmaz; kesilme ve gecikme beklenir |

**Önce ne bozulur [TAHMİN]:** (1) Önce *girdi hızı* düşer. Bu sessizce olur ve oyuncu fark etmez; 250 Hz üstü yeterli.
(2) Sonra *gecikme kuyruğu* (p99) uzar. (3) Sonra *haptik* aksar: 0x32 tamponu yalnızca ~50 ms, kaçırılan 10,67 ms'lik raporlar kesik
ya da cızırtılı his olarak hissedilir. (4) Hoparlör sesi en hassas olanı (Opus çerçeveleri, kesinti hemen duyulur).
Wi-Fi 2,4 GHz'te yoğunsa ya da kart Wi-Fi+BT kombo ise (zaman paylaşımı) bütün bu eşikler aşağı kayar.
**Bilinmeyen:** Windows BT yığını ile adaptör yazılımının birden fazla HID bağlantısını gerçekten adil yoklayıp yoklamadığı; DualSense'in
sniff moduna geçip geçmediği (geçerse hız sabit ve düşük bir aralığa iner). Bunlar donanımda ölçülmeli.

### 2.3 Uygulama neyi ölçmeli ve göstermeli (öneri)
`AdapterCheck` bugün tek kumandayı not veriyor: hız <125 / <250 Hz, kayıp >%2 / >%0,5, durma p99 >30 / >15 ms, haptik <%90 / <%97,
yük altında %60'ın altına düşme. Öneriler:
1. **N kumandalı test:** Bağlı bütün kumandalar aynı anda: 5 sn boşta → 5 sn **hepsinde** haptik (sessiz 80 Hz ton) → 5 sn hoparlör açık
   (deneysel özellik açıksa). Her kumanda için ayrı `LinkResult`, ayrıca toplam satır: Σ girdi Hz, Σ haptik raporu/sn, en kötü p99.
   Not her kumandaya ayrı verilir; genel not en kötüsü olur.
2. **Bütçe göstergesi (canlı):** Genel bakış'ta, köprü çalışırken her kumandanın girdi Hz'i, haptik raporu/sn'si ve son 10 sn'deki
   "haptik açığı" (beklenen − gönderilen). Bunlar `BridgeStatus.InputRateHz`, `HapticsReportsPerSecond` ve bekçi ile zaten toplanıyor;
   yalnızca slot başına gösterilmeleri gerekiyor.
3. **Uyarılar:** Haptik hızı <%97 (10 sn) → "Bluetooth adaptörü N kumandayı haptikle zor taşıyor". Toplam girdi <250 Hz/kumanda →
   bilgi notu. Kombo kartta (adaptör adı Intel/Realtek/MediaTek Wi-Fi+BT ise) → "Wi-Fi'yi 5 GHz'e alın" ipucu. CSR klonu tespit edilirse →
   "2'den fazla kumanda için önerilmez". Klon tespiti: HCI sürüm/alt sürüm bilgisi; Windows'tan okunabilirliği **[TAHMİN]**, denenmeli.
4. **Sonucu sakla:** Tanılama zip'ine N kumandalı sonucu ve adaptör kimliğini (VID/PID, sürücü sürümü) ekle.

---

## 3. Windows ve oyun tarafı

### 3.1 Oyuncu numarası ve oyuncu LED'leri
- Oyuncu numarasını işletim sistemi değil **oyun ya da üst katman (Steam Input, SDL kullanan oyun)** belirliyor **[TAHMİN; bir arama
  özetinde Linux tarafında oyuncu kimliğinin "kullanıcı alanının işi" sayıldığı geçiyordu, ama temiz oda kuralı gereği o yama sayfasını açmadım]**.
- Steam'in DualSense oyuncu LED'i için "açık / kapalı / yalnızca birden fazla DualSense bağlıyken" seçeneği var.
  [PC Gamer](https://pcgamer.com/steam-client-beta-adds-extended-support-for-ps5-and-xbox-controllers),
  [Stealth Optional](https://stealthoptional.com/pc-gaming/ps5-controller-customise-led-dualsense-steam-connect/) [KAYNAK]
- Bizde: oyun ya da Steam sanal pede LED gönderirse bu `OutputReceived` → 0x31 ile gerçek kumandaya gider. Bizim `LightForSlot`
  varsayılanı ise ilk değeri verir [KOD].
  **[TAHMİN]** Çoğu PC oyunu oyuncu LED'i göndermiyor. Steam Input açıksa Steam kendi sırasını yakıyor; bu bizim slot sıramızla
  çakışabilir.
- **Windows'taki sıra [TAHMİN]:** XInput/SDL/Raw Input sıralaması genelde cihazın takılma sırasını izliyor. Bizim sanal pedlerimiz
  kumandaların bağlanma sırasıyla takılıyor; bir ped çıkarılıp yeniden takılırsa oyun onu sona koyabilir.

### 3.2 Her pedin kendi ses cihazı — en büyük bilinmeyen
- Her sanal DualSense'in kendi 4 kanallı USB ses çıkışı ve mikrofonu var [KOD: `VirtualDualSense` uç noktaları 1 ve 2].
  Windows bunları "Wireless Controller", "2- Wireless Controller" gibi adlarla listeler **[TAHMİN, yaygın Windows davranışı]**.
- **Oyunlar haptik cihazını nasıl seçiyor [KAYNAK]:** Wine geliştirici listesine göre Ghostwire: Tokyo HID cihazının ContainerID'sini
  SetupAPI ile okuyup MMDevAPI'deki ses çıkışlarını aynı ContainerID'ye göre süzüyor ve **ilk eşleşeni** alıyor. Deathloop benzer,
  ama kullanılamayan uç noktaları da sayıyor ve ilk eşleşmede duruyor, yani bayat bir uç nokta seçilebiliyor.
  [Wine MR359/MR360 tartışması](https://list.winehq.org/archives/list/wine-gitlab@list.winehq.org/thread/SQ2SB5P53IYI4RNIJODYX5M4S2RGNWVY),
  [wine-devel 2022-07](https://www.winehq.org/pipermail/wine-devel/2022-July/222153.html)
- **ContainerID nasıl atanıyor [KAYNAK]:** Microsoft'a göre bir USB cihazının alt düğümleri (HID arayüzü, ses arayüzü) ebeveynin
  ContainerID'sini miras alıyor. Ebeveynin kendisi, port "çıkarılabilir" ise yeni bir ContainerID alıyor (seri numarası yoksa rastgele);
  port "çıkarılamaz" ise **bilgisayarın ContainerID'sini** miras alıyor.
  [Microsoft Learn: How USB devices are assigned container IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-usb-devices-are-assigned-container-ids)
  - **Sonuç [TAHMİN]:** usbip-win2'nin sanal hub'ı portları çıkarılabilir bildiriyorsa her sanal pedin HID ve sesi kendi kapsayıcısını paylaşır.
    ContainerID kullanan oyunlar doğru pede haptik gönderir; kendiliğinden iyi. Çıkarılamaz bildiriyorsa bütün pedler ve PC aynı
    kapsayıcıda olur. O zaman ContainerID ile arayan oyun "ilk eşleşeni", büyük olasılıkla 1. pedi (hatta başka bir PC ses aygıtını)
    seçer. **Bu, tek kumandada bile haptik çalışmasını etkiler ve donanımda ilk ölçülmesi gereken şey.**
  - Seri numarası olmadığı için ContainerID her takılışta rastgele olabilir. Bu yüzden Windows'ta her takılışta yeni bir ses uç noktası
    oluşup bayat uç noktalar birikebilir. Deathloop tipi oyunlar bayat uç noktaya takılabilir **[TAHMİN]**. Çözüm adayları: MS OS
    ContainerID tanımlayıcısıyla **slot başına sabit** bir ContainerID vermek ya da sabit bir sahte seri numarası. Her ikisi de Windows'un
    önbelleğiyle etkileşir; ölçülmeli.
- **Varsayılan cihaz karmaşası [KAYNAK]:** Windows, DualSense takılınca varsayılan oynatma cihazını sık sık "Wireless Controller"a
  çeviriyor. Kullanıcılar bunun için uç noktayı devre dışı bırakmayı öneriyor, ama bazı oyunlar kumandanın uç noktası etkin olmadıkça
  titreşim/haptik vermiyor.
  [CyberPowerPC blog](https://www.cyberpowerpc.com/blog/ps5-dualsense-controller-recognized-as-audio-device-how-to-fix/),
  [EA F1 23 forumu](https://forums.ea.com/discussions/f1-23-en/pc-no-vibrationrumble-with-dualsense-controller/8390341),
  [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/3876833/dualsense-controller-is-connecting-to-windows-howe).
  4 pedde bu sorun 4 kat. Kodumuzda varsayılan cihazı koruyan bir şey yok [KOD: `PolicyConfig`/`IMMDevice` araması boş, yalnızca `MicMute`].
- **[TAHMİN]** Bazı oyunlar haptiği yalnızca *ilk* DualSense'e veriyor (çok oyunculu haptik desteği zaten nadir). Bu bizim düzeltebileceğimiz
  bir şey değil; yalnızca beklentiyi doğru kurmak gerekiyor.

### 3.3 DualSense özelliklerini birden fazla oyuncu için destekleyen yerel co-op oyunlar
Herkese açık kaynaklarda PC'de iki DualSense ile **iki oyuncunun da** haptik ve adaptif tetik aldığını doğrulayan güvenilir bir oyun
listesi **bulamadım** [KAYNAK eksik]. Split Fiction'ın PS5 sayfası yerel co-op için iki DualSense istiyor ve haptik/tetik desteğinden
söz ediyor, ama bu PS5 için. [PlayStation: Split Fiction](https://www.playstation.com/games/split-fiction/)
**Öneri:** Uygulamada "çoklu oyuncu haptik doğrulandı" diye bir liste tutulmasın. Kullanıcıya oyun başına "2. oyuncu haptiği çalıştı mı?"
diye sorulup yerelde saklanabilir.

### 3.4 Steam Input
- Kodda Steam Input denetimi var (`SteamInputCheck`, `SteamController_PSSupport`, oyun başına `UseSteamControllerConfig`) [KOD].
- **[TAHMİN]** Steam Input açıkken Steam her sanal DualSense'i kendisi açar ve oyuna kendi sanal Xbox pedlerini verir. Haptik ve tetikler
  kaybolur; oyuncu sırasını ve LED'leri Steam belirler. Çoklu kumandada bu uyarı her kumanda için aynı. Ek bir işe gerek yok, ama Genel
  bakış'taki uyarı "bütün kumandaları etkiler" demeli.

### 3.5 usbip-win2 ile birden fazla sanal cihaz
- usbip-win2 README'si eşzamanlı cihaz/port sayısı ya da izokron desteği hakkında sayı vermiyor [KAYNAK:
  [usbip-win2](https://github.com/vadimgrn/usbip-win2)]. Önceki usbip-win projesinde vhci port sayısının 16'ya çıkarıldığı bir etiket notu var
  [KAYNAK: [usbip-win v0.3.3-dev](https://git.ista.ac.at/alois.schloegl/usbip-win/-/tags/v0.3.3-dev)]. Linux'ta aynı anda yapılan
  takmalarda "aynı boş port seçilip birinin başarısız olması" yarışı rapor edilmiş; usbip-win2 için doğrulanmadı.
- **[TAHMİN]** 4 ped × (1 kHz HID + 1 ms'lik izokron ses + mikrofon) loopback'te CPU açısından küçük bir yük. Asıl risk, sürücünün
  izokron zamanlamasını birden fazla cihazda kararlı tutup tutamayacağı. Ölçülmeli.
- Bizde takmalar `RefreshAsync` içinde **sırayla** yapılıyor (her host `StartAsync`'i bekleniyor) [KOD]. Bu, yarış riskini azaltıyor.

### 3.6 HidHide ile birden fazla cihaz
- `HidingSession` istenen sayıda cihaz alıyor ve yalnızca kendi eklediklerini geri alıyor [KOD].
- **[KAYNAK]** HidHide engel listesi aygıt örnek yolu ile çalışıyor; BT kumandası silinip yeniden eşlenince yeni bir yol oluşabiliyor ve
  yeniden işaretlemek gerekiyor. USB ve BT için ayrı ayrı işaretleme gerekiyor.
  [DS4Windows HidHide sorun giderme](https://kanuan.github.io/DS4WSite/troubleshooting/hidhide-troubleshoot)
- Bizde her bağlanmada yeniden gizleniyor (`ControllerOpened`), bu yüzden yeniden eşleme sorun değil [KOD].

### 3.7 Karışık kurulumlar
| Kurulum | Bugün [KOD] | Not / öneri |
|---|---|---|
| Bir kumanda kabloda, diğerleri BT | `CableSwitch` yalnızca 1. slotta. Kablolu kumanda `Enumerate`'te BT değilse slot almıyor **[TAHMİN: `WindowsControllerSource` yalnızca BT DualSense'leri listeliyor; yorum "Every Bluetooth DualSense present now"]** | Kablolu gerçek ped de oyuna görünür ve HidHide onu gizlemez (`HidingSession` USB pedi atlıyor). Oyuncu sırası: gerçek kablolu ped Windows'ta ayrı bir cihaz; sıra takılma zamanına bağlı. 2–4. slotta kablo takılırsa o slotun BT bağlantısı kopar ve sanal ped çıkar → oyunda kopma. Öneri: kablo geçişini slot başına yapmak |
| DualSense + DualSense Edge | Her biri kendi kimliğiyle ("Edge Olarak Göster" seçeneği genel) | Sorun beklenmiyor. Edge arka tuşları kumanda profilinde olmalı |
| Biri Xbox 360/DS4 taklidi, diğeri DualSense | **Desteklenmiyor**: `UsePad` bütün slotlara aynı türü veriyor | Aday: slot başına ped türü. Gerçek kullanım alanı: XInput-only bir oyunda bir oyuncunun Xbox gibi görünmesi. Ancak oyuncu sırası XInput (0–3) ve DualSense (ayrı API) arasında karışır; değeri sınırlı |

---

## 4. Özellik adayları (öncelik sırasıyla)

Ölçek: Çaba S (≤1 gün) / M (2–4 gün) / L (1 hafta+). Risk = yanlış çıkma ya da donanımda bozulma olasılığı.

| # | Özellik | Kullanıcıya değeri | Çaba | Risk | Bağımlılık |
|---|---|---|---|---|---|
| 1 | Ses uç noktası ↔ ped eşleşmesi: ölç, göster, gerekirse slot başına sabit ContainerID | Haptiğin doğru oyuncuya gitmesi (tek kumandada bile) | S (ölçüm) + M (düzeltme) | Yüksek (Windows önbelleği) | Donanım oturumu |
| 2 | N kumandalı adaptör testi + canlı bütçe göstergesi + uyarılar | "Kaç kumanda olur?" sorusuna cevap | M | Düşük | Yok |
| 3 | Kısa kopmada sanal pedi takılı tutmak (ör. 30 sn nötr girdiyle) | 2. oyuncu 1. olmaz; oyun "kumanda kesildi" demez | M | Orta (oyunun uzun süre sessiz pedi görmesi) | Yok |
| 4 | Kalıcı hatırlanan kumandalar: slot/profil/isim otomatik; elle sıralama; "Tanı" | Her oturumda aynı oyuncu numarası; hangi kumanda kimin | S–M | Düşük | Paralel kayıt işi |
| 5 | Bant genişliğine göre düşürme: N>1'de hoparlör/mik varsayılan kapalı + uyarı; isteğe bağlı "2+ kumandada haptik hafif" | Kesilmeyen haptik | S–M | Orta (düşük kalite modu tanımsız) | #2 ölçümleri |
| 6 | Slot başına pil, düşük pil ve Düşük Güç | 2. oyuncunun pili habersiz bitmez | S–M | Düşük | Yok |
| 7 | Slot başına uyku koruması, kablo geçişi, pil yoklaması (PLAN'daki "yalnızca 1. slot" borcu) | Uykudan dönüşte herkes geri gelir | M | Orta | Yok |
| 8 | Kumanda başına hızlı ayarlar (haptik/tetik gücü, ışık, Düşük Güç) | Çocuk hafif, yetişkin güçlü haptik | M | Orta (arayüz karmaşası) | #4 |
| 9 | Kısayolları slota bağlamak (2. oyuncu PS+D-pad ile yalnızca kendi profilini değiştirir) | Oyuncular birbirini bozmaz | S | Düşük | #8 |
| 10 | Varsayılan ses cihazını koruma (ped takılınca Windows'un varsayılanı değiştirmesini geri almak) | Oyun sesi hoparlörden kaymaz | M | Orta (gizli Windows API) | #1 |
| 11 | Slot başına ped türü (bir oyuncu Xbox 360) | Nadir senaryolar | M | Orta | Yok |

### Ayrıntılar

**1. Ses uç noktası eşleşmesi.**
- Ne: Bir tanılama satırı, "Kumanda 2 → Hoparlör (2- Wireless Controller), aynı kapsayıcı ✓". Sorun varsa düzeltme: sanal pede MS OS 1.0
  ContainerID tanımlayıcısı (her slot için sabit GUID) ya da slot başına sabit seri numarası.
- Donanımsız test: tanımlayıcı üretiminin birim testi (her slotta farklı ve sabit GUID); `FakeUsbIpDevice` ile OS tanımlayıcı isteğine
  yanıt testi.
- Donanımla test: 2 ped takılıyken PowerShell `Get-PnpDevice -PresentOnly | ? FriendlyName -match 'Wireless Controller|DualSense'` ve
  `Get-PnpDeviceProperty DEVPKEY_Device_ContainerId`. HID ile ses düğümünün aynı, iki pedin farklı ContainerID'si olmalı. Ardından
  ContainerID kullanan bir oyunla 2. pedin haptiği.

**2. N kumandalı adaptör testi.**
- `AdapterCheck.RunAsync` birden fazla `BluetoothSession` alacak şekilde genişletilir. `Grade` slot başına yapılır, toplam satır eklenir.
  Haptik fazında hepsine aynı anda ton gönderilir. Eşikler aynı kalır, ek olarak "kumanda başına <250 Hz" ve "toplam haptik açığı" var.
- Donanımsız test: `FakeLink` ile N oturum; sentetik `LinkSample` dizileriyle not verme testleri (ör. 3 kumandada 2.'si sınırda).
- Donanımla test: bkz. bölüm 5.

**3. Slot kararlılığı.**
- Bugün kumanda düşünce `Connection.CloseAsync` önce sanal pedi çıkarıyor [KOD]. Öneri: "beklemede" durumu. Sanal ped takılı kalır,
  nötr girdi ve sessiz ses akar, kumanda X sn içinde dönerse yeni `BluetoothSession` aynı pede bağlanır. Süre dolunca ped çıkarılır.
  Kullanıcı kumandayı kendisi kapattıysa ("Kumandayı kapat", boşta kapanma) ped hemen çıkarılır.
- Risk: Ped/USB/IP sunucusu ile BT oturumunun yaşam döngüsü bugün tek bir `Connection` içinde. Ayrılmaları gerekiyor (orta büyüklükte
  bir yeniden düzenleme).
- Donanımsız test: `BridgeHost` + `FakeLink` + `FakeUsbIpDevice` ile "kopma → 5 sn → dönüş: attach çağrısı tekrar yapılmadı, bus id aynı".

**4. Hatırlanan kumandalar.**
- Kalıcı kayıt slot, profil ve isim tutar. `BridgeManager.Assign` önce kayıttaki slotu dener. Kenar çubuğu ve çiplerde kullanıcının verdiği
  isim ("Ali'nin kumandası") gösterilir. "Slotu değiştir": iki kumandanın slotunu takas eder; sanal pedler yeniden takılır, bunun için
  oyunun kapalı olması önerilir.
- "Tanı": seçili kumandada 0,5 sn haptik + oyuncu LED'leri yanıp söner (`ShowPlayerLeds` ve `TestHapticsAsync` zaten var [KOD]).
- Donanımsız test: kayıtla `Assign` testleri (dönüşte eski slot, çakışmada boş slot); isimlerin `ControllerEntry`'ye taşınması.

**5. Bant genişliğine göre düşürme.**
- Basit kural: N≥2'de hoparlör ve mikrofon (deneysel) yalnızca seçili kumandada açılabilsin; N≥3'te uyarı. "Haptik hafif modu" (ör. her iki
  0x32'den birini atlamak) tamponu (~50 ms) ve hissi bozar, bu yüzden **[TAHMİN]** önerilmez. Daha güvenli seçenek: haptik sessizken
  0x32 göndermemek ve böylece girdiye yer açmak (hoparlör yolunda 200 ms sessizlik kuralı var [PLAN]; haptik yolunda sessizlikte akışın
  durup durmadığını bu araştırmada doğrulamadım).
- Donanımsız test: kural tablosu birim testi. Donanımla: #2 ile önce/sonra karşılaştırması.

**6. Slot başına pil ve Düşük Güç.**
- `ControllerEntry` zaten pil taşıyor [KOD]. Düşük pil bildirimi her kumanda için slot adıyla verilmeli. Düşük Güç kumanda profiline
  taşınmalı ya da "yalnızca pili düşük olan kumanda" seçeneği olmalı.
- Donanımsız test: `FakeControllerService` ile iki kumanda, 2.'si %15 → "Kumanda 2 pili zayıf".

**7–9.** PLAN'daki 1. slot borcu: `BridgeSleepGuard` bütün hostlar için ya da `BridgeManager` için; `CableSwitch` her slot için. Hızlı
ayarlar seçili kumandanın profilini düzenler, profili yoksa "Bu kumandaya özel yap" düğmesi gösterilir. Kısayollar o kumandanın profiline
etki eder.

### 4.1 Arayüz önerisi (dağınıklık olmadan)
- **Tek kumanda:** Bugünkü gibi (zaten öyle tasarlanmış [KOD: `IsMulti`]).
- **2–4 kumanda:**
  - Kenar çubuğu: slot rozeti + kullanıcının verdiği isim + pil simgesi + küçük durum noktası (çalışıyor / beklemede / haptik aksıyor).
    Uzun adlar kısaltılır (ekranda "DualSense E…" zaten kesiliyor).
  - Genel bakış: başlık çipleri kalır. Seçili kumandanın kartında "Tanı" ve "Bu kumandanın profili" yer alır. Hızlı Ayarlar başlığında
    hangi kumandaya uygulandığı yazar ("Kumanda 2 · Ali").
  - Tek satırlık "Bluetooth yükü" göstergesi (Normal / Yoğun / Aşırı), tıklanınca kumanda başına Hz ve haptik tablosu açılır.
  - Kumanda testi: seçili kumanda (bugünkü gibi) + üstte "Tanı".
  - Ayarlar → Kumandalar: hatırlanan kumandalar listesi (isim, slot, profil, unut, sırala).

### 4.2 Bilerek desteklenmemeli
- 4'ten fazla kumanda. 7'lik piconet sınırı ve hava süresi yüzünden zaten anlamsız.
- Bir adaptörde 3–4 kumandada tam kalite haptik + hoparlör + mikrofon **sözü vermek**. Ölçüme göre "en iyi çaba" ve uyarı.
- Birden fazla BT adaptörü ile yük dağıtma. Windows tek bir etkin BT radyosu destekliyor **[TAHMİN, yaygın bilgi; doğrulanmadı]**.
- Aynı oyunda karışık ped türleri (#11) yalnızca talep olursa yapılmalı.
- Oyunun çok oyunculu haptiği hiç desteklemediği durumları "düzeltmek" (haptiği başka pede kopyalamak gibi).

---

## 5. Donanım oturumu için açık sorular (çalıştırma sırasıyla)

Elde en az bir DualSense ve bir CSR klonu dongle var. İkinci bir DualSense (ya da Edge) varsa 3–6. adımlar mümkün.

1. **Tek ped, ContainerID:** Köprüyü çalıştır. Sanal pedin HID düğümü, ses çıkışı ve mikrofonunun `DEVPKEY_Device_ContainerId`'si aynı mı,
   bilgisayarınkinden farklı mı? Çıkar-tak yapınca değişiyor mu? `mmsys.cpl`'de bayat "Wireless Controller" uç noktaları birikiyor mu?
   Pedi takınca varsayılan oynatma cihazı değişiyor mu? *Bu tek kumandada da kritik.*
2. **Tek ped, yük profili:** Adaptör testi boşta / haptikle / hoparlörle (deneysel). Girdi Hz, p99, haptik raporu/sn. Aynı ölçüm Wi-Fi
   2,4 GHz'te yoğun indirme sırasında ve Wi-Fi kapalıyken. (Kombo kart varsa onunla da.)
3. **İki ped, haptiksiz:** Girdi hızı kumanda başına ~400 Hz'e iniyor mu (yoklama paylaşımı hipotezi)? Yoksa her biri ~750'de mi kalıyor?
   Hipotez yanlışsa bölüm 2.2'deki tablo geçersiz olur.
4. **İki ped, ikisi de haptikli:** Haptik raporu/sn her biri 93,75'e yakın mı? 10 dk boyunca bekçi tetikleniyor mu? Haptik açığı ve
   hissedilen kesilme var mı?
5. **İki ped, ContainerID ve oyun:** İki pedin kapsayıcıları farklı mı? ContainerID ile cihaz arayan bir oyunda 2. oyuncunun haptiği
   kendi kumandasına mı gidiyor? (İki kişilik DualSense destekli bir oyun gerekiyor.)
6. **Kopma ve dönüş:** 2. kumandayı kapat-aç. Slot aynı mı? Oyun onu yine 2. oyuncu olarak mı görüyor? Sanal pedin Windows'taki örnek
   kimliği ve sırası değişiyor mu?
7. **usbip-win2 sınırları:** Hızlı arka arkaya 4 takma (sahte ped de olur: `Kopru.Probe virtual`) yapılınca port yarışı ya da hata var mı?
   `usbip port` çıktısı.
8. **Uyku:** İki pedle uyku → uyanma. 2. slot geri geliyor mu, ne kadar sürede?
9. (3–4 kumanda bulunabilirse) bölüm 2.2 tablosunun 3. ve 4. satırlarını doğrula.

---

## 6. Doğrulanamayanlar (özet)
- DualSense'in BT girdi hızının yoklamaya mı bağlı olduğu, yoksa sabit iç hızı mı olduğu. Sniff modu.
- usbip-win2'nin port sayısı ve çok cihazlı izokron kararlılığı.
- usbip-win2 sanal hub portunun "çıkarılabilir" bildirilip bildirilmediği, yani ContainerID'nin ne olacağı.
- PC'de iki oyuncuya da haptik/tetik veren yerel co-op oyun listesi.
- Steam Input'un birden fazla sanal DualSense ile oyuncu sırasını ve LED'leri nasıl verdiği.
- CSR klonu dongle'ın birden fazla HID bağlantısında nasıl davrandığı (yalnızca anekdot var).

## Kaynaklar
- Microsoft: [USB container ID ataması](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-usb-devices-are-assigned-container-ids),
  [USB aygıt kayıt girdileri (IgnoreHWSerNum)](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-device-specific-registry-settings)
- Wine: [mmdevapi ContainerID (MR359)](https://list.winehq.org/archives/list/wine-gitlab@list.winehq.org/thread/SQ2SB5P53IYI4RNIJODYX5M4S2RGNWVY),
  [wine-devel 2022-07](https://www.winehq.org/pipermail/wine-devel/2022-July/222153.html)
- BT temel: [York Univ.](https://wiki1.eecs.yorku.ca/course_archive/2008-09/W/4215/_media/notes_may_7.pdf),
  [Freiburg](https://forum.cone.informatik.uni-freiburg.de/teaching/lecture/wsn-w06/slides/WSN-11.pdf),
  [R&S paket uzunlukları](https://rscmwbluetoothmeas.readthedocs.io/en/latest/Configure_InputSignal_Plength.html),
  [Silex: ACL temelleri](https://www.silextechnology.com/unwired/basics-of-asynchronous-connection-less-acl-bluetooth-communication-protocol)
- Adaptör/kullanıcı raporları: [Intel AX200 + çoklu kumanda](https://community.intel.com/t5/Wireless/AX200-bluetooth-disconnection-with-multiple-game-controllers/m-p/1469130),
  [DS4Windows BT gecikme](https://kanuan.github.io/DS4WSite/troubleshooting/input-delay-bt),
  [DS4Windows BT bağlantı](https://kanuan.github.io/DS4WSite/troubleshooting/bt-connection-issues/),
  [Ultimarc CSR klonları](https://www.arcaderenovations.com/ultimarc-bluehid-csr-dongle.html),
  [Windows Latest: Intel Wi-Fi/BT düzeltmesi](https://www.windowslatest.com/2026/04/30/intel-admits-wi-fi-and-bluetooth-conflicts-on-windows-11-rolls-out-fixes-with-april-2026-driver-update/),
  [HotHardware](https://hothardware.com/news/intel-issues-urgent-windows-11-bluetooth-wifi-fix),
  [Intel topluluk N-7260](https://communities.intel.com/thread/104995)
- Ses cihazı: [CyberPowerPC](https://www.cyberpowerpc.com/blog/ps5-dualsense-controller-recognized-as-audio-device-how-to-fix/),
  [EA F1 23 forumu](https://forums.ea.com/discussions/f1-23-en/pc-no-vibrationrumble-with-dualsense-controller/8390341),
  [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/3876833/dualsense-controller-is-connecting-to-windows-howe)
- LED / Steam: [PC Gamer](https://pcgamer.com/steam-client-beta-adds-extended-support-for-ps5-and-xbox-controllers),
  [Stealth Optional](https://stealthoptional.com/pc-gaming/ps5-controller-customise-led-dualsense-steam-connect/)
- USB/IP: [usbip-win2 README](https://github.com/vadimgrn/usbip-win2), [usbip-win v0.3.3-dev etiketi](https://git.ista.ac.at/alois.schloegl/usbip-win/-/tags/v0.3.3-dev)
- HidHide: [DS4Windows HidHide sorun giderme](https://kanuan.github.io/DS4WSite/troubleshooting/hidhide-troubleshoot)
- PS5 co-op: [PlayStation — Split Fiction](https://www.playstation.com/games/split-fiction/)
