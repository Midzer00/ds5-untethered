<p align="center"><img src="docs/brand/logo.png" alt="DS5 Untethered logo" width="480"></p>

# DS5 Untethered

For DualSense — the game's own haptics and adaptive triggers over Bluetooth.

[English](#english) | [Türkçe](#türkçe)

## Disclaimer

DS5 Untethered is an unofficial project. It is not affiliated with, endorsed or sponsored by Sony Interactive Entertainment.
"DualSense", "DualSense Edge", "PlayStation" and "PS5" are trademarks of Sony Interactive Entertainment Inc. and are
used only to describe compatibility. "DS5" is used only as a common shorthand to describe compatibility. No Sony firmware, software, images or logos are included. The controller drawings
are original. The protocol implementation is an independent interoperability implementation based on public
documentation and our own measurements. Use at your own risk; the software comes with no warranty.

## English

### The problem

On a PC, a DualSense controller's game haptics and adaptive triggers only work when it is connected with a USB cable.
Over Bluetooth, games see a different device and these features are lost. DS5 Untethered relays them over Bluetooth so that
games see a wired controller.

### How it works

DS5 Untethered connects to the controller over Bluetooth and creates a virtual wired DualSense on the same PC through a local
USB/IP server and usbip-win2. Games talk to the virtual controller as if it were plugged in. Input from the real
controller is forwarded to the virtual one; the game's output (adaptive triggers, lights) and its haptics audio are
converted and sent back to the controller over Bluetooth. HidHide hides the real controller from games so they only
see the virtual one.

```
DualSense --Bluetooth--> DS5 Untethered --USB/IP--> usbip-win2 --> virtual wired DualSense --> game
game --> triggers, lights, haptics audio (48 kHz, channels 3-4) --> DS5 Untethered --Bluetooth--> DualSense
```

### Download

A preview build is on the repository's [Releases](https://github.com/Midzer00/ds5-untethered/releases) page, as an
installer (`DS5-Untethered-<version>-Setup.exe`, per-user, no administrator rights) and as a zip
(`DS5-Untethered-<version>-win-x64.zip`, unzip anywhere and run `Kopru.App.exe`). Both include .NET; nothing else needs
to be installed for the app itself.

- The build is not signed, so Windows SmartScreen may warn: choose "More info", then "Run anyway".
- On start, until it has been completed once, the Setup page checks for the two drivers, usbip-win2 and HidHide, and
  opens their official download pages (the app does not install them; their installers need administrator rights),
  then guides Bluetooth pairing of the controller. Both drivers are required: the virtual controller does not start
  without them.
- Settings, profiles and logs are stored in `%LOCALAPPDATA%\Untethered`.
- To uninstall: turn off "Start with Windows", exit the app from the tray icon, uninstall "DS5 Untethered" in Windows
  Settings → Apps (or delete the zip folder), and delete `%LOCALAPPDATA%\Untethered` if you want the settings gone.
  If nothing else uses them, uninstall "USBip" and "HidHide" too.

### Status

Preview. Most features are tested in code only; what was measured on a real controller is listed below.

Done and verified on hardware:
- Bluetooth session: input reading, output reports, haptics stream; Bluetooth adapter check.
- Virtual wired DualSense via usbip-win2: Windows recognizes it as a wired DualSense, input reaches applications,
  game audio arrives.
- Bridge (60 s test): input relayed at about 750 Hz with low added latency and no loss; trigger and light output
  forwarded; game haptics audio converted to Bluetooth haptics reports.
- Bluetooth microphone: Windows records from the controller's microphone, with no ghost input on the virtual
  controller; the controller's mute button turns the microphone on and off.
- Controller speaker: sound played to the controller's speaker is heard by its own microphone.

Done and tested in code, not yet verified on hardware:
- The app: setup page, Overview, one Controller page (Test, Triggers, Remap and Gyro), Light, Supported Games,
  Settings, tray icon and notifications; the bridge is shown as the "Virtual Controller" and starts by itself.
- Hiding the real controller with HidHide; rumble to haptics for games without native haptics; reconnection,
  battery warning, idle power-off.
- Profiles per game, trigger strength, remapping, gyro aim, light effects, mouse mode.
- Microphone button light and notifications, controller speaker in games.
- DualSense Edge, up to four controllers that are remembered and can be named, Xbox 360 or DualShock 4 emulation per
  game.

Not done yet:
- Installing the two drivers from the installer, code signing, update check.
- Long gaming sessions and most features above on real hardware.

### Requirements

- Windows 10 or 11
- A Bluetooth adapter
- [usbip-win2](https://github.com/vadimgrn/usbip-win2)
- [HidHide](https://github.com/nefarius/HidHide)

### Building

Requires the .NET 8 SDK. From the repository root:

```
dotnet build
dotnet test
```

### Screenshots

| | |
|---|---|
| ![Overview](docs/screens/overview-en.png) | ![Overview, DualSense Edge](docs/screens/overview-edge-en.png) |
| ![Controller](docs/screens/controller-test-en.png) | ![Remap and gyro](docs/screens/controller-remap-en.png) |
| ![Light](docs/screens/light-en.png) | ![Settings](docs/screens/settings-en.png) |

### License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Türkçe

DualSense için — oyunun kendi haptikleri ve adaptif tetikleri Bluetooth üzerinden.

### Feragat

DS5 Untethered resmi olmayan bir projedir. Sony Interactive Entertainment ile bağlantılı değildir, onun tarafından
onaylanmamış veya desteklenmemiştir. "DualSense", "DualSense Edge", "PlayStation" ve "PS5", Sony Interactive
Entertainment Inc.'in ticari markalarıdır ve yalnızca uyumluluğu belirtmek için kullanılır. "DS5" yalnızca uyumluluğu belirtmek için yaygın bir kısaltma olarak kullanılır. Sony'ye ait hiçbir
yazılım, donanım yazılımı, görsel veya logo içerilmez. Kumanda çizimleri özgündür. Protokol uygulaması, herkese açık
belgelere ve kendi ölçümlerimize dayanan bağımsız bir birlikte çalışabilirlik uygulamasıdır. Kullanım riski size
aittir; yazılım hiçbir garanti olmadan sunulur.

### Sorun

PC'de DualSense kumandanın oyun haptiği ve adaptif tetikleri yalnızca USB kabloyla bağlıyken çalışır. Bluetooth'ta
oyunlar farklı bir cihaz görür ve bu özellikler kaybolur. DS5 Untethered bunları Bluetooth üzerinden aktarır; oyunlar kablolu
bir kumanda görür.

### Nasıl çalışır

DS5 Untethered kumandaya Bluetooth ile bağlanır ve aynı bilgisayarda yerel bir USB/IP sunucusu ile usbip-win2 aracılığıyla
sanal bir kablolu DualSense oluşturur. Oyunlar sanal kumandayla takılıymış gibi konuşur. Gerçek kumandanın girdisi
sanal kumandaya aktarılır; oyunun çıktısı (adaptif tetikler, ışıklar) ve haptik sesi dönüştürülüp Bluetooth ile
kumandaya gönderilir. HidHide gerçek kumandayı oyunlardan gizler, böylece oyunlar yalnızca sanal kumandayı görür.

```
DualSense --Bluetooth--> DS5 Untethered --USB/IP--> usbip-win2 --> sanal kablolu DualSense --> oyun
oyun --> tetikler, ışıklar, haptik sesi (48 kHz, 3-4. kanallar) --> DS5 Untethered --Bluetooth--> DualSense
```

### İndirme

Ön sürüm, deponun [Releases](https://github.com/Midzer00/ds5-untethered/releases) sayfasında kurulum programı
(`DS5-Untethered-<sürüm>-Setup.exe`, yalnızca sizin kullanıcınıza, yönetici izni istemeden) ve zip
(`DS5-Untethered-<sürüm>-win-x64.zip`, istediğiniz yere açıp `Kopru.App.exe`'yi çalıştırın) olarak bulunur. İkisinde de
.NET içindedir; uygulamanın kendisi için başka bir şey kurmanız gerekmez.

- Sürüm imzalı olmadığı için Windows SmartScreen uyarı verebilir: "Ek bilgi"ye, sonra "Yine de çalıştır"a tıklayın.
- Bir kez tamamlanana kadar her açılışta Kurulum sayfası iki sürücüyü, usbip-win2 ve HidHide'ı kontrol eder ve resmi
  indirme sayfalarını açar (uygulama bunları kurmaz; kurulumları yönetici izni ister), ardından kumandanın Bluetooth
  ile eşleştirilmesine yol gösterir. İki sürücü de gerekli: onlar olmadan sanal kumanda başlamaz.
- Ayarlar, profiller ve günlükler `%LOCALAPPDATA%\Untethered` klasöründe tutulur.
- Kaldırmak için: "Windows ile Başlat"ı kapatın, uygulamadan sistem tepsisindeki simgeden çıkın, Windows Ayarlar →
  Uygulamalar'dan "DS5 Untethered"ı kaldırın (ya da zip klasörünü silin), ayarların da gitmesini istiyorsanız
  `%LOCALAPPDATA%\Untethered` klasörünü silin. Başka bir şey kullanmıyorsa "USBip" ve "HidHide"ı da kaldırın.

### Durum

Ön sürüm. Özelliklerin çoğu yalnızca kodda test edildi; gerçek kumandada ölçülenler aşağıda.

Bitti ve donanımda doğrulandı:
- Bluetooth oturumu: girdi okuma, çıkış raporları, haptik akışı; Bluetooth adaptör testi.
- usbip-win2 ile sanal kablolu DualSense: Windows kablolu DualSense olarak tanıyor, girdi uygulamalara ulaşıyor,
  oyun sesi geliyor.
- DS5 Untethered (60 sn test): girdi yaklaşık 750 Hz'de, düşük ek gecikmeyle ve kayıpsız aktarıldı; tetik ve ışık çıktısı
  iletildi; oyun haptik sesi Bluetooth haptik raporlarına dönüştürüldü.
- Bluetooth mikrofon: Windows kumandanın mikrofonundan kayıt alıyor, sanal kumandada hayalet giriş olmuyor; kumandanın
  mikrofon tuşu mikrofonu açıp kapatıyor.
- Kumanda hoparlörü: kumandanın hoparlörüne çalınan ses kendi mikrofonundan duyuluyor.

Kodda bitti ve testli, donanımda henüz doğrulanmadı:
- Uygulama: kurulum sayfası, Genel Bakış, tek Kumanda sayfası (Test, Tuş Atama ve Gyro), Işık, Desteklenen
  Oyunlar, Ayarlar, tepsi simgesi ve bildirimler; köprü "Sanal Kumanda" olarak görünür ve kendiliğinden başlar.
- Gerçek kumandayı HidHide ile gizleme; yerel haptik desteği olmayan oyunlar için titreşimden haptiğe dönüşüm;
  yeniden bağlanma, pil uyarısı, boşta kapatma.
- Oyuna göre profiller, tetik gücü, tuş atama, gyro ile nişan, ışık efektleri, fare modu.
- Mikrofon tuşu ışığı ve bildirimleri, oyunlarda kumanda hoparlörü.
- DualSense Edge, hatırlanan ve ad verilebilen en fazla dört kumanda, oyun başına Xbox 360 ya da DualShock 4 taklidi.

Henüz yapılmadı:
- İki sürücünün kurulum programıyla kurulması, kod imzalama, güncelleme denetimi.
- Uzun oyun oturumları ve yukarıdaki özelliklerin çoğunun gerçek donanımda denenmesi.

### Gereksinimler

- Windows 10 veya 11
- Bluetooth adaptörü
- [usbip-win2](https://github.com/vadimgrn/usbip-win2)
- [HidHide](https://github.com/nefarius/HidHide)

### Derleme

.NET 8 SDK gerekir. Depo kök dizininde:

```
dotnet build
dotnet test
```

### Ekran görüntüleri

| | |
|---|---|
| ![Genel bakış](docs/screens/overview-tr.png) | ![Genel bakış, DualSense Edge](docs/screens/overview-edge-tr.png) |
| ![Kumanda](docs/screens/controller-test-tr.png) | ![Tuş atama ve gyro](docs/screens/controller-remap-tr.png) |
| ![Işık](docs/screens/light-tr.png) | ![Ayarlar](docs/screens/settings-tr.png) |

### Lisans

GPL-3.0-or-later. Bkz. [LICENSE](LICENSE) ve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
