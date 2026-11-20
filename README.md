<p align="center"><img src="docs/brand/logo.png" alt="DS5 Untethered logo" width="240"></p>

<h1 align="center">DS5 Untethered for Linux (experimental)</h1>

<p align="center">For DualSense — the game's own haptics and adaptive triggers over Bluetooth, on Linux.</p>

<p align="center"><a href="#english">English</a> · <a href="#türkçe">Türkçe</a> · <a href="docs/linux/README.md">Linux guide</a> · <a href="CHANGELOG.md">Changelog</a></p>

<p align="center"><sub>Unofficial project, not affiliated with Sony. See the <a href="#disclaimer">Disclaimer</a> (<a href="#yasal-uyarı">Yasal uyarı</a>).</sub></p>

## English

This is the `linux` branch: the **experimental Linux app**, community-maintained. It is built and released separately
from the Windows app, which lives on [`main`](https://github.com/Midzer00/ds5-untethered/tree/main) and has its own
releases. The two are never merged.

DS5 Untethered makes a Bluetooth DualSense look like a wired one: the game sees a virtual wired DualSense (served by the
app over USB/IP on 127.0.0.1 and plugged in with the system's `usbip` tools and the `vhci_hcd` module), and the app sends
the game's haptics and adaptive trigger effects to the real controller over Bluetooth.

### What's in it (experimental)

- The desktop app on X11 (and Wayland through XWayland) with all its pages, in English and Turkish.
- Starting and stopping the virtual controller; plugging it in asks for your password through pkexec (polkit).
- The virtual DualSense with the game's haptics and adaptive triggers; DualShock 4 and Xbox 360 types.
- The setup page checks `vhci_hcd`, `usbip`, `pkexec` and access to the controller's hidraw node, with fix commands.
- Settings and profiles, start when you sign in, a tray icon where the desktop has one, turning the controller off.
- Per-game controller types and profiles for Steam games (native and Proton).

Not there yet: hiding the real controller (games may see two controllers), sound to haptics, Mouse Mode, the system
sound device pickers, the Xbox Series type and in-app updates. Details in the [Linux guide](docs/linux/README.md).

### Download

The latest build of this branch is in the
[`linux-spike` pre-release](https://github.com/Midzer00/ds5-untethered/releases/tag/linux-spike):
`ds5-untethered-linux-app-x64.tar.gz` (the app, self-contained, no .NET needed) and
`ds5-untethered-linux-spike-x64.tar.gz` (a headless test tool). Setup, root rights and what to report are in the
[Linux guide](docs/linux/README.md).

### Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Pull requests for Linux go to the `linux` branch.

### Disclaimer

DS5 Untethered is an unofficial project. It is not affiliated with, endorsed or sponsored by Sony Interactive Entertainment.
"DualSense", "DualSense Edge", "PlayStation" and "PS5" are trademarks of Sony Interactive Entertainment Inc. and are
used only to describe compatibility. "DS5" is used only as a common shorthand to describe compatibility. No Sony firmware,
software, images or logos are included. The controller drawings are original. The protocol implementation is an
independent interoperability implementation based on public documentation and our own measurements. Use at your own
risk; the software comes with no warranty.

### License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Türkçe

Bu, topluluk tarafından sürdürülen **deneysel Linux uygulamasının** yer aldığı `linux` dalıdır. [`main`](https://github.com/Midzer00/ds5-untethered/tree/main) dalında bulunan ve kendi sürümleri olan Windows uygulamasından ayrı derlenir ve yayımlanır. İki dal hiçbir zaman birleştirilmez.

DS5 Untethered, Bluetooth ile bağlı bir DualSense'i kabloluymuş gibi gösterir: oyun, sanal bir kablolu DualSense görür. Uygulama bu sanal kumandayı 127.0.0.1 üzerinden USB/IP ile sunar; sistemin `usbip` araçları ve `vhci_hcd` modülüyle bağlantısı sağlanır. Uygulama, oyunun dokunsal geri bildirim ve uyarlanabilir tetik efektlerini Bluetooth üzerinden gerçek kumandaya gönderir.

Neler var (deneysel):
- Tüm sayfalarıyla, İngilizce ve Türkçe olarak X11 üzerinde (ve XWayland aracılığıyla Wayland üzerinde) masaüstü uygulaması.
- Sanal kumandayı başlatma ve durdurma; bağlantı kurulurken pkexec (polkit) aracılığıyla parolanız istenir.
- Oyunun dokunsal geri bildirimini ve uyarlanabilir tetiklerini destekleyen sanal DualSense; DualShock 4 ve Xbox 360 türleri.
- `vhci_hcd`, `usbip`, `pkexec` ve kumandanın hidraw düğümüne erişimi denetleyen, sorunları gidermek için komutlar sunan kurulum sayfası.
- Ayarlar ve profiller, oturum açıldığında başlatma, destekleyen masaüstlerinde bildirim alanı simgesi, kumandayı kapatma.
- Steam oyunları (yerel ve Proton) için oyuna özel kumanda türleri ve profiller.

Henüz bulunmayanlar: gerçek kumandayı gizleme (oyunlar iki kumanda görebilir), sesi dokunsal geri bildirime dönüştürme, Fare Modu, sistem ses aygıtı seçicileri, Xbox Series türü ve uygulama içi güncellemeler.

İndirme: [`linux-spike` ön sürümü](https://github.com/Midzer00/ds5-untethered/releases/tag/linux-spike) (`ds5-untethered-linux-app-x64.tar.gz`). Kurulum ve ayrıntılar: [Linux kılavuzu](docs/linux/README.md) (İngilizce). Katkıda bulunmak için: [CONTRIBUTING.md](CONTRIBUTING.md); Linux için pull request'ler `linux` dalına gönderilir.

### Yasal uyarı

DS5 Untethered resmi bir proje değildir. Sony Interactive Entertainment ile bağlantısı yoktur, şirketin onayını
ya da desteğini de almamıştır. "DualSense", "DualSense Edge", "PlayStation" ve "PS5",
Sony Interactive Entertainment Inc.'in ticari markalarıdır ve burada yalnızca uyumluluğu belirtmek için kullanılmıştır.
"DS5" de yalnızca uyumluluğu belirtmek için, yaygın bir kısaltma olarak kullanılmıştır. Projede Sony'ye ait hiçbir
donanım yazılımı, yazılım, görsel veya logo bulunmaz. Kumanda çizimleri özgündür. Protokol, herkese açık belgelere ve
kendi ölçümlerimize dayanılarak, birlikte çalışabilirlik amacıyla bağımsız olarak uygulanmıştır. Yazılımın herhangi
bir garantisi yoktur. Kullanımdan doğabilecek her türlü sorumluluk size aittir.

### Lisans

GPL-3.0-or-later. Ayrıntılar [LICENSE](LICENSE) ve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyalarındadır.
