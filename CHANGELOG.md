# Changelog

<p><a href="#english">English</a> · <a href="#türkçe">Türkçe</a></p>

Downloads and full notes for each version are on the [Releases](https://github.com/Midzer00/ds5-untethered/releases) page.

## English

### 0.2.0 — 2026-11-18

- New Xbox Series controller type: games see an Xbox Series controller, and Create works as View/Back. On Windows older than 10 21H2, or if Windows doesn't start it, the app uses Xbox 360 instead.
- Xbox Trigger Rumble (experimental, off by default; Settings > Controllers > Triggers, or the Games page): games that use Xbox trigger rumble, racing games especially, get an Xbox Series controller, and their trigger rumble plays on the DualSense's own trigger motors, made to feel like the Xbox trigger motors (stronger rumble is faster and finer, with a soft start and stop). The Trigger Rumble feel is set per profile: Xbox-Like, Soft, Sharp or Off. The app knows 87 such games and learns new ones.
- Games page: a new Xbox Trigger Rumble section. Automatic picks per game in this order: DualSense for native DualSense support, Xbox Series for Xbox trigger rumble games (while Xbox Trigger Rumble is on), DualShock 4, your Default Controller Type, else Xbox 360.
- Profiles: create one with a name from a game's settings, rename it or duplicate it. Switching profiles while the game runs applies at once.
- Refreshed look: the same rows, sizes and spacing on every page, a redesigned game settings pop-up, and a Light page that fits without scrolling. A Back To Game button in the game pop-up and the Games list for the game that is running.
- Sidebar: a Star button (GitHub) next to the Support button.
- Fixed: the controller could keep vibrating after a game closed, especially with Steam Input; rumble now stops when the game exits, lets go of the controller or stops sending. The microphone cuts out less (adaptive buffer, lost-packet concealment). Empty profile pickers in the Games list. The Turn Sound Into Haptics session log is clearer.

### 0.1.10 — 2026-11-17

- Automatic Controller Type now picks per game: DualSense for games with native DualSense support (the app's own list, Steam store data shipped with the app for about 21,000 games, or learned when the game in front sends real trigger effects or haptics for a few seconds), DualShock 4 for games that only support DualShock 4, otherwise the Default Controller Type, else Xbox 360. It reads Steam's local app info to tell games from tools; nothing about your games is sent anywhere.
- Games page: Automatic shows what it picks for the game, e.g. "Automatic (Xbox 360)". The Default Controller Type is now for games we know nothing about.
- A pink Support button in the sidebar and a GitHub Sponsors link in Settings > General > Updates open GitHub Sponsors. The app stays free.
- The classic rumble card is now Xbox And DS4 Rumble and says it is the general profile for Xbox and DualShock 4 rumble.
- The Mouse Mode tip bar and its Show Tips switch are gone; a short PS press turns Mouse Mode on.
- Windows no longer slows the app down while it is hidden during a game (power throttling is turned off for the app).
- Turn Sound Into Haptics now really works: earlier versions never captured the game's sound. Its switch, Strength and Bass/Full Sound now apply at once while the game runs.

### 0.1.9 — 2026-11-17

- A short PS press that opens the app now turns Mouse Mode on, with a short notification; going back to the game turns it off.
- Mouse Mode: every touchpad press is exactly one click, and the pointer no longer drifts while you press.
- New Right Click setting (Settings > Mouse Mode): Off (every press is a left click, the default), Right Side or Two Fingers.
- The DualShock 4 type now looks to Windows like a real wired DualShock 4, so games with native DualShock 4 support accept it: a game with native DualShock 4 support that sent it nothing before now sends its output to it. The DualShock 4's speaker is never left as the default Windows output.
- In-app updates download a separate `-Update.exe` copy of the installer, so new installs and updates can be counted apart.
- Fixed: the light glow on the controller picture spilling outside the light strip; menu icons not lined up with their labels; pickers losing their choice when you switch the language.

### 0.1.8 — 2026-11-16

- A game's settings open in a large pop-up over the Games page, in two columns on wide windows.
- Esc, Close or a click outside closes the pop-up; Enter opens a game's row.

### 0.1.7 — 2026-11-16

- Controller Type per game replaces the DualSense Mode switch; there is one game profile. Games with DualSense support get their own haptics and triggers straight through; the classic rumble settings only apply where the game sends nothing. Apply Now switches the type while the game is running.
- Games page with Supported and Unsupported games, found from Steam and Epic, with a Play button, a Start File you can choose and the settings for each game.
- Turn Sound Into Haptics for games without DualSense support: the game's own sound becomes haptics (Bass or Full Sound, with a Strength slider).
- DSX Mod Support: game mods made for DSX can send trigger and light effects. Off by default (Settings > Controllers); it only listens on this PC.
- Mouse Mode: press the touchpad twice to turn it on. It turns itself off when a game takes the controller, and has a Back To Game button.
- Controller pictures show the real colour of your controller's light.
- Redesigned Live card, easier to read tooltips, lower CPU use in the background.
- New option: Pause Haptics In Silence.
- Much better game recognition, using exe names from Steam's data.
- Fixed: the game's light staying dark after you quit a game; buttons getting stuck with the Xbox 360 type.

### 0.1.6 — 2026-11-13

- In-app updates: a check shortly after start and once a week, and an Update button that downloads the installer from Releases, checks its size and SHA-256, installs it and restarts the app. Turn it off with Check For Updates Automatically (Settings > General).
- Copies run from the zip get a button that opens the download page instead.
- The settings file is backed up the first time a new version starts.

### 0.1.5 — 2026-11-12

- The Overview, Controller and Settings pages fit every window size without scrolling; Settings is split into six tabs.
- Mouse Mode: choose the pointer stick and map 16 buttons to clicks, keys, window switching, volume and media actions.
- Supported Games: an Installed view lists the supported games found on this PC.
- Games whose exe name is not known are recognised by their folder, product name or window title.
- A short PS press opens the app in games too (Also In Games); a second press returns to the game.
- The mic button light always follows the microphone, also while a game drives the lights.
- Smoother controller speaker sound.

### 0.1.4 Preview — 2026-11-11

- The interface scales with the window.
- Connection quality on the Overview (Good / Borderline / Weak) and a Bluetooth adapter test that explains its result.
- Windows keeps your default speaker and microphone when the virtual controller starts; sound device pickers in Quick Settings.
- The controller's microphone works over Bluetooth; the mic button mutes it or turns it off.
- Controller Type: Automatic, or DualSense, DualShock 4 or Xbox 360 for every game.
- Game pictures on the Supported Games list.
- A short PS press opens the app when no game is in front.

### 0.1.0 Preview — 2026-11-10

- First preview: a DualSense over Bluetooth appears to games as a wired one, with the game's haptics and adaptive triggers.
- Installer for the current user and a zip; the Setup page checks for usbip-win2 and HidHide and guides pairing.

## Türkçe

Her sürümün indirme dosyaları ve tam notları [Releases](https://github.com/Midzer00/ds5-untethered/releases) sayfasında.

### 0.2.0 — 18.11.2026

- Yeni Xbox Series kumanda türü: oyunlar bir Xbox Series kumanda görür, Create tuşu View/Back olarak çalışır. Windows 10 21H2'den eski sürümlerde ya da Windows onu başlatamazsa uygulama yerine Xbox 360 kullanır.
- Xbox Tetik Titreşimi (deneysel, varsayılan olarak kapalı; Ayarlar > Kumandalar > Tetikler ya da Oyunlar sayfası): Xbox tetik titreşimi kullanan oyunlar, özellikle yarış oyunları, Xbox Series kumanda alır; oyunun tetik titreşimi Xbox'ın tetik motorlarını taklit ederek DualSense'in kendi tetik motorlarında çalınır (titreşim güçlendikçe daha hızlı ve ince olur, yumuşak başlayıp yumuşak biter). Tetik Titreşimi hissi profile göre seçilir: Xbox Gibi, Yumuşak, Keskin ya da Kapalı. Uygulama bu tür 87 oyunu tanıyor ve yenilerini öğreniyor.
- Oyunlar sayfası: yeni Xbox Tetik Titreşimi bölümü. Otomatik oyuna göre şu sırayla seçer: DualSense'i doğrudan destekleyen oyunlara DualSense, Xbox tetik titreşimi olan oyunlara (Xbox Tetik Titreşimi açıkken) Xbox Series, sonra DualShock 4, Varsayılan Kumanda Türü, o da yoksa Xbox 360.
- Profiller: bir oyunun ayarlarından adını vererek profil oluşturma, yeniden adlandırma ve çoğaltma. Oyun açıkken profil değiştirmek hemen etki ediyor.
- Yenilenen görünüm: her sayfada aynı satırlar, boyutlar ve boşluklar, yeniden tasarlanan oyun ayarları penceresi ve kaydırmadan sığan bir Işık sayfası. Çalışan oyun için oyun penceresinde ve Oyunlar listesinde Oyuna Dön düğmesi.
- Sol menü: Destek Ol düğmesinin yanında Yıldızla düğmesi (GitHub).
- Düzeltildi: oyun kapandıktan sonra kumandanın titremeye devam edebilmesi (özellikle Steam Input'la); titreşim artık oyun kapanınca, kumandayı bırakınca ya da göndermeyi kesince duruyor. Mikrofon daha az kesiliyor (uyarlanan tampon, kaybolan paketlerin gizlenmesi). Oyunlar listesindeki boş profil seçicileri. Sesi Titreşime Çevir oturum günlüğü daha anlaşılır.

### 0.1.10 — 17.11.2026

- Otomatik kumanda türü artık oyuna göre seçiyor: DualSense'i doğrudan destekleyen oyunlara DualSense (uygulamanın kendi listesi, uygulamayla gelen yaklaşık 21.000 oyunluk Steam mağaza bilgisi ya da öndeki oyun birkaç saniye gerçek tetik efekti veya haptik gönderince öğrenilir), yalnızca DualShock 4'ü destekleyen oyunlara DualShock 4, diğerlerine Varsayılan Kumanda Türü, o da yoksa Xbox 360. Oyunları araçlardan ayırmak için Steam'in bilgisayardaki uygulama bilgilerini okur; oyunlarınızla ilgili hiçbir bilgi bir yere gönderilmez.
- Oyunlar sayfası: Otomatik, oyuna neyi seçtiğini gösteriyor, ör. "Otomatik (Xbox 360)". Varsayılan Kumanda Türü artık hakkında bilgi olmayan oyunlar için.
- Sol menüdeki pembe Destek Ol düğmesi ve Ayarlar > Genel > Güncellemeler'deki GitHub Sponsors bağlantısı GitHub Sponsors'u açıyor. Uygulama ücretsiz kalıyor.
- Klasik titreşim kartının adı artık Xbox ve DS4 Titreşimi; Xbox ve DualShock 4 titreşimi için genel profil olduğunu söylüyor.
- Fare Modu ipucu çubuğu ve İpuçlarını Göster anahtarı kaldırıldı; kısa bir PS basışı Fare Modu'nu açıyor.
- Windows, oyun sırasında gizli duran uygulamayı artık yavaşlatmıyor (uygulama için güç kısıtlaması kapatıldı).
- Sesi Titreşime Çevir artık gerçekten çalışıyor; önceki sürümlerde oyunun sesini hiç yakalamıyordu. Anahtar, Güç ve Bas/Tüm Ses oyun açıkken hemen etki ediyor.

### 0.1.9 — 17.11.2026

- Uygulamayı açan kısa PS basışı artık Fare Modu'nu da açıyor ve kısa bir bildirim gösteriyor; oyuna dönünce Fare Modu kapanıyor.
- Fare Modu: dokunmatik yüzeye her basış tam olarak tek tık; basarken imleç artık kaymıyor.
- Yeni Sağ Tık ayarı (Ayarlar > Fare Modu): Kapalı (her basış sol tık, varsayılan), Sağ Taraf ya da İki Parmak.
- DualShock 4 türü artık Windows'a gerçek, kablolu bir DualShock 4 gibi görünüyor; DualShock 4'ü doğrudan destekleyen oyunlar onu kabul ediyor. Önceden ona hiçbir şey göndermeyen, DualShock 4'ü doğrudan destekleyen bir oyun artık ona veri gönderiyor. DualShock 4'ün hoparlörü hiçbir zaman Windows'un varsayılan ses çıkışı olarak kalmıyor.
- Uygulama içi güncellemeler kurulum dosyasının ayrı bir `-Update.exe` kopyasını indiriyor; böylece yeni kurulumlar ile güncellemeler ayrı sayılabiliyor.
- Düzeltildi: kumanda görselindeki ışık parlamasının ışık şeridinin dışına taşması; sol menü simgelerinin yazılarıyla hizasız durması; dil değiştirince seçicilerin seçimini unutması.

### 0.1.8 — 16.11.2026

- Bir oyunun ayarları Oyunlar sayfasının üstünde büyük bir açılır pencerede açılıyor; geniş pencerelerde iki sütun halinde.
- Esc, Kapat ya da pencerenin dışına tıklamak pencereyi kapatır; bir oyunun satırı Enter ile açılır.

### 0.1.7 — 16.11.2026

- DualSense Modu anahtarının yerine oyun başına Kumanda Türü geldi; tek bir oyun profili var. DualSense destekleyen oyunların kendi haptiği ve tetikleri olduğu gibi geçer; klasik titreşim ayarları yalnızca oyunun hiçbir şey göndermediği yerde devreye girer. Şimdi Uygula, oyun açıkken türü hemen değiştirir.
- Oyunlar sayfası: Steam ve Epic'ten bulunan Desteklenen ve Desteklenmeyen oyunlar, Oyna düğmesi, seçilebilen Başlatma Dosyası ve her oyunun kendi ayarları.
- DualSense desteklemeyen oyunlar için Sesi Titreşime Çevir: oyunun kendi sesi haptiğe dönüşür (Bas ya da Tüm Ses, Güç ayarıyla).
- DSX Mod Desteği: DSX için yapılmış oyun modları tetik ve ışık efektlerini gönderebilir. Varsayılan olarak kapalı (Ayarlar > Kumandalar); yalnızca bu bilgisayarın içinden gelenleri dinler.
- Fare Modu: açmak için dokunmatik yüzeye iki kez basın. Bir oyun kumandayı aldığında kendiliğinden kapanır, Oyuna Dön düğmesi var.
- Kumanda görselleri kumandanızın ışığını gerçek rengiyle gösteriyor.
- Yenilenen Canlı kartı, daha kolay okunan araç ipuçları, arka planda daha az işlemci kullanımı.
- Yeni seçenek: Sessizlikte Haptiği Durdur.
- Oyun tanıma çok daha iyi; Steam verisindeki exe adları kullanılıyor.
- Düzeltildi: oyundan çıktıktan sonra oyunun ışığının sönük kalması; Xbox 360 türünde tuşların takılı kalması.

### 0.1.6 — 13.11.2026

- Uygulama içinden güncelleme: açılıştan kısa süre sonra ve haftada bir denetim; Güncelle düğmesi kurulum dosyasını Releases'tan indirir, boyutunu ve SHA-256 özetini denetler, kurar ve uygulamayı yeniden açar. Ayarlar > Genel'deki "Güncellemeleri Otomatik Denetle" ile kapatılır.
- Zip'ten çalışan kopyalarda bunun yerine indirme sayfasını açan bir düğme çıkar.
- Yeni bir sürüm ilk açıldığında ayar dosyasının yedeği alınır.

### 0.1.5 — 12.11.2026

- Genel Bakış, Kumanda ve Ayarlar sayfaları her pencere boyutuna kaydırmadan sığar; Ayarlar altı sekmeye ayrıldı.
- Fare Modu: imleci oynatan analog seçilebilir, 16 tuşa tık, klavye tuşu, pencere değiştirme, ses ve medya işlevi atanabilir.
- Desteklenen Oyunlar: "Yüklü" görünümü bu bilgisayarda bulunan desteklenen oyunları listeler.
- Exe adı bilinmeyen oyunlar klasör adından, ürün adından ya da pencere başlığından tanınır.
- PS tuşuna kısa basış oyundayken de uygulamayı açar (Oyunda Da Aç); ikinci basış oyuna döndürür.
- Mikrofon tuşunun ışığı, ışıkları oyun yönetirken de mikrofonu izler.
- Kumanda hoparlöründen daha pürüzsüz ses.

### 0.1.4 Ön Sürüm — 11.11.2026

- Arayüz pencereyle birlikte büyür.
- Genel Bakış'ta bağlantı kalitesi (İyi / Sınırda / Zayıf); sonucunu açıklayan Bluetooth adaptör testi.
- Sanal kumanda başlarken Windows'un varsayılan hoparlörü ve mikrofonu korunur; Hızlı Ayarlar'da ses aygıtı seçicileri.
- Kumandanın mikrofonu Bluetooth'ta çalışır; mikrofon tuşu sessize alır ya da kapatır.
- Kumanda Türü: Otomatik, ya da bütün oyunlar için DualSense, DualShock 4 veya Xbox 360.
- Desteklenen Oyunlar listesinde oyun görselleri.
- Oyun önde değilken PS tuşuna kısa basınca uygulama açılır.

### 0.1.0 Ön Sürüm — 10.11.2026

- İlk ön sürüm: Bluetooth'la bağlı DualSense oyunlara kabloyla bağlıymış gibi görünür, oyunun haptiği ve adaptif tetikleri çalışır.
- Geçerli kullanıcıya kuran kurulum dosyası ve zip; Kurulum sayfası usbip-win2 ile HidHide'a bakar ve eşleştirmede yol gösterir.
