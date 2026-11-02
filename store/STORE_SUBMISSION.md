# ByteScape — Microsoft Store submission

This folder holds everything needed to build the Store package. Package type: **MSIX, x64, full-trust desktop app**
(Desktop Bridge), self-contained .NET 8 (users do not need to install .NET).

| File | Purpose |
|---|---|
| `make-assets.ps1` | Draws every logo and splash image (63 PNG files, scale-100…400 and targetsize-16…256, plated and unplated) and `ByteScape.Wpf\ByteScape.ico` from one vector definition |
| `Assets\` | The generated images |
| `AppxManifest.template.xml` | The package manifest; `{{IdentityName}}`, `{{Publisher}}`, `{{PublisherDisplayName}}` and `{{Version}}` are filled in by the build script |
| `build-msix.ps1` | publish → assets + manifest → `resources.pri` (makepri) → `makeappx pack` → optional developer signature |
| `run-wack.ps1` | Runs the Windows App Certification Kit on the package (asks for administrator rights) |
| `build-portable.ps1` | The two portable exes for GitHub releases (see [docs/releasing.md](../docs/releasing.md)) |
| `build-site.ps1` | The optional download site |
| `out\ByteScape_<version>_x64.msix` | The package |

## How the app behaves when packaged

- `AppConfig.ConfigPath`: when the exe folder is not writable or the app runs with a package identity
  (`GetCurrentPackageFullName`), settings go to `%LOCALAPPDATA%\ByteScape\ByteScapeConfig.xml` (WindowsApps is read-only).
  An existing file next to the exe is preferred for portable use.
- Four-part version (`1.0.0.0`, AssemblyVersion/FileVersion), `ApplicationIcon` = `ByteScape.ico`, Company/Copyright.
- The package is **not single-file** (self-extraction is unnecessary in the Store and nothing is extracted to
  `%TEMP%\.net`); the files are in the package.
- The manifest declares the `.sns` file association (a double-click opens a snapshot), the `bytescape.exe` execution alias
  (from a terminal: `bytescape scan C:\ export Totals out.txt autoclose`), the Explorer menu and `runFullTrust`.
- The packaged app never offers the update check (the Store updates it), the donation link or "Restart as administrator".

## Building the package

```powershell
# 1) images (once; again when the logo changes)
.\store\make-assets.ps1

# 2) developer package (local tests, unsigned)
.\store\build-msix.ps1

# 3) signed package for a local install test (creates a self-signed certificate)
.\store\build-msix.ps1 -Sign
#    -> import store\out\ByteScapeDev.cer once into "Local Machine \ Trusted People" (administrator),
#       then double-click the .msix or run: Add-AppxPackage store\out\ByteScape_1.0.0.0_x64.msix

# 4) Store submission package (with the Partner Center identity)
.\store\build-msix.ps1 -IdentityName "12345PublisherName.ByteScape" -Publisher "CN=0A1B2C3D-…" -PublisherDisplayName "<publisher display name>" -Version 1.0.0.0
```

A Store submission does not need to be signed; Partner Center signs the package with the Store certificate.
`Identity/Name` and `Publisher` in the manifest must match the **Product identity** page in Partner Center exactly,
otherwise the upload is rejected.

## Submission checklist

1. **Partner Center account** → *Apps and games* → *New product* → *MSIX or PWA app* → reserve the name **ByteScape**.
2. From the *Product identity* page take `Package/Identity/Name`, `Package/Identity/Publisher` and
   `PublisherDisplayName` → build the package with command 4 above. `PublisherDisplayName` must be the account's publisher
   display name (it is shown as the publisher on the Store page).
3. **Windows App Certification Kit**: `powershell -ExecutionPolicy Bypass -File store\run-wack.ps1` (asks for
   administrator rights; trusts the developer certificate only while the kit runs) → `store\out\wack.xml`. Expected:
   PASS. The optional "Blocked executables" check warns about text matches inside the .NET / WPF runtime files and about
   the app opening Explorer, Settings and uninstallers; this is normal for full-trust desktop apps and does not block a
   submission.
4. **Submission → Packages**: upload the `.msix`. Architecture x64; minimum Windows 10 1809 (17763).
5. **Properties**: category *Utilities & tools*; no special hardware; declare access to the user's files (runFullTrust).
   *Product declarations* → "This product has been tested to meet accessibility guidelines": tick it after the five-minute
   Narrator check at the end of [docs/accessibility.md](../docs/accessibility.md) (the automated keyboard, UI Automation
   and contrast tests are in place).
6. **Age ratings**: IARC questionnaire — no violence, gambling and so on → 3+ / PEGI 3.
7. **Pricing and availability**: free, all markets. No trial, add-ons or in-app purchases.
   **License terms**: paste the MIT License (the repository's `LICENSE`; also in the package root as `LICENSE.txt`) into
   *Additional license terms*, so that the Store page states the same terms as the source code.
8. **Store listing** (texts below; the package declares 7 languages — en-US, tr-TR, ru-RU, zh-CN, fr-FR, es-ES, pt-BR —
   and Partner Center takes a listing per language; the screenshots should be in that language). Screenshots: 1920×1080
   PNG, 4 per language, rendered from a made-up drive (no real names appear):
   `$env:BYTESCAPE_STORE_SHOTS="$PWD\store\out\screenshots"; dotnet test ByteScape.Tests --filter StoreShotTests`
   → `store\out\screenshots\<language>\1-map.png … 4-forecast.png`.
   Order and captions: 1 the map with the "What is this?" panel, 2 comparison with three months earlier, 3 programs on disk,
   4 disk-full forecast.
9. **Privacy policy URL**: the app collects and sends nothing, but the Store may still ask for a URL for apps that access
   the file system: `https://github.com/hayrettincevik/ByteScape/blob/main/PRIVACY.md` (or `<site>/privacy.html` when the
   optional site is published).
10. **Notes for certification**: "Full-trust desktop app. Scans folders/drives the user selects and draws a treemap; no
    network connections by default (an optional, off-by-default local AI feature talks only to a server the user sets up),
    no telemetry. Test: launch → New view → select a drive or folder → treemap appears; right click → Open in Explorer."

## Listing texts

### English

**Title:** ByteScape

**Short description (100):** See where your disk space went — an interactive treemap of your drives and folders.

**Description:**
ByteScape shows the contents of a drive or folder as a treemap: every rectangle is a file or folder, its area proportional to its size.
The big blocks are the space hogs — double-click to zoom in, Backspace to go back.

- Scans while it draws: results appear immediately, no waiting for the full scan
- Live view: files created, changed or deleted while ByteScape is open are updated and blink
- Filters: `*.mp4`, `>100mb`, `<6months`, tags, attributes, file classes — combine them with `;`
- Colour tags (red / yellow / green / blue) to mark what you looked at
- Free and unscanned space shown on the map
- Programs on disk: every installed program with everything it uses — install folder, app data, caches, Store app data — and
  the folders left behind by programs you removed
- Games: your Steam, Epic Games and GOG games with their caches, mods and saves, when you last played each one and for how long —
  "not played for a year: 3 games, 180 GB" — and what uninstalled games left behind. Numbered Steam folders get the game's name on the map
- Developer leftovers: node_modules, bin/obj, target, .venv and other folders your tools rebuild, per project, with the last time you
  worked on each project and the command that brings them back — "untouched for 6 months: 7 projects, 12 GB"
- Duplicate files: the same content found by length and SHA-256 (hard links and online-only cloud files are never mistaken for copies); one copy of each file always stays
- Compare two folders: only on one side, different or the same, optionally byte by byte (check a backup)
- Disk usage history: the drive and its biggest folders over time, as charts
- Reports: CSV (opens in Excel), JSON and a self-contained HTML page
- "Open in ByteScape" in Explorer's right-click menu of folders and drives
- Safe cleanup: delete to the Recycle Bin (or permanently) with a clear answer to "is it safe to delete?", protected Windows and program folders, and a warning for what needs administrator rights or is open in a program
- Cleanup lists: large files, temporary files, old installation files and programs you have not used for months
- Quick rescans: NTFS keeps a journal of every change, so a rescan lists only the folders that changed (a whole system drive in under a second instead of ten), also the next time you open ByteScape
- Pause, stop and continue a scan later, even after closing ByteScape
- Hard links counted once: Windows' WinSxS folder and other hard-linked files are no longer counted twice (on our test PC: 9.3 GB → 3.5 GB)
- Disk forecast: when the drive will be full at the current pace, where the new files went, and what grew since your last scan
- Cache Radar: known caches and temp folders are marked on the map, with what deleting them does and how to clean them properly
- OneDrive, Dropbox and iCloud aware: online-only files are counted as using no space, and you see how much "Free up space" would give back
- Compare with a snapshot: what grew, shrank, appeared or disappeared since last month
- Filters in plain words: "videos over 100 MB not opened for 6 months"
- Right click: open in Explorer, properties, copy path, Windows context menu
- Export reports (text templates) and binary snapshots (.sns) you can reopen later
- Command line: `bytescape scan C:\ export Totals report.txt autoclose`
- Interface in English, Turkish, Russian, Chinese (Simplified), French, Spanish and Portuguese (Brazil) — it follows the Windows display language
- Free and open source (MIT License): no ads, no trial, no account
- Keyboard and screen reader friendly (Narrator, NVDA, JAWS), and it follows the Windows contrast themes
- No telemetry; by default ByteScape makes no network connections

**Features (Partner Center "Product features", max. 20 × 200 characters):**
1. Treemap of any drive or folder, drawn while it scans
2. Space used by each installed program, with its app data and caches
3. Games you no longer play (Steam, Epic Games, GOG) and what they left behind
4. Old projects' node_modules, bin/obj and other rebuildable folders
5. "Is it safe to delete?" answered for every file and folder
6. Safe delete to the Recycle Bin; Windows and program folders are protected
7. Cleanup lists: large, temporary and old installation files, unused programs
8. Known caches and temp folders marked and explained (Cache Radar)
9. Disk-full forecast: when the drive fills up and where new files went
10. Compare with an earlier snapshot: what grew, shrank, appeared or disappeared
11. Duplicate files, found by content; one copy always stays
12. Folder comparison, optionally byte by byte
13. Disk usage history charts for the drive and its biggest folders
14. Quick rescans from the NTFS change journal; pause and resume scans
15. Hard links and online-only cloud files counted correctly
16. Filters, also in plain words: "videos over 100 MB not opened for 6 months"
17. CSV, JSON and HTML reports; snapshots to reopen later; command line
18. "Open in ByteScape" in the Explorer right-click menu
19. Keyboard, screen reader and high contrast support; 7 languages
20. Free and open source (MIT): no ads, no trial, no account, no telemetry

**Keywords (max. 7):** disk space, disk usage, treemap, storage, clean up, large files, folder size

### Türkçe

**Başlık:** ByteScape

**Kısa açıklama:** Disk alanınızın nereye gittiğini görün — sürücü ve klasörlerinizin etkileşimli treemap haritası.

**Açıklama:**
ByteScape bir sürücüyü ya da klasörü treemap olarak gösterir: her dikdörtgen bir dosya ya da klasördür, alanı boyutuyla orantılıdır.
Büyük bloklar yer kaplayanlardır — çift tıklayarak içine girin, Backspace ile geri dönün.

- Tararken çizer: sonuçlar anında görünür, taramanın bitmesi beklenmez
- Canlı görünüm: ByteScape açıkken oluşturulan, değişen, silinen dosyalar haritada güncellenir ve yanıp söner
- Filtreler: `*.mp4`, `>100mb`, `<6months`, etiketler, öznitelikler, dosya sınıfları — `;` ile birleştirilir
- Renkli etiketler (kırmızı / sarı / yeşil / mavi)
- Boş ve taranmamış alan haritada gösterilir
- Programlar diskte: her kurulu programın kapladığı her şey — kurulum klasörü, uygulama verisi, önbellekler, Store verisi — ve
  kaldırdığınız programlardan geride kalan klasörler
- Oyunlar: Steam, Epic Games ve GOG oyunlarınız önbellek, mod ve kayıtlarıyla; her birini en son ne zaman ve ne kadar oynadığınız —
  "bir yıldır oynanmamış: 3 oyun, 180 GB" — ve kaldırılan oyunlardan geride kalanlar. Numaralı Steam klasörlerine haritada oyunun adı yazılır
- Geliştirici artıkları: node_modules, bin/obj, target, .venv gibi araçların yeniden ürettiği klasörler proje proje; her projede en son ne zaman
  çalıştığınız ve klasörü geri getiren komut — "6 aydır dokunulmamış: 7 proje, 12 GB"
- Yinelenen dosyalar: aynı içerik uzunluk ve SHA-256 ile bulunur (hard link'ler ve yalnız çevrimiçi bulut dosyaları kopya sanılmaz); her dosyanın bir kopyası her zaman kalır
- İki klasörü karşılaştırma: yalnız bir tarafta olan, farklı, aynı; istenirse bayt bayt (yedek kontrolü)
- Disk kullanım geçmişi: sürücü ve en büyük klasörleri zaman içinde, grafiklerle
- Raporlar: CSV (Excel'de açılır), JSON ve tek dosyalık HTML sayfası
- Gezgin'in sağ tık menüsünde "ByteScape ile aç" (klasörler ve sürücüler)
- Güvenli silme: Geri Dönüşüm Kutusu'na (ya da kalıcı), "silmek güvenli mi?" açıklamasıyla; Windows ve program klasörleri korunur; yönetici izni gerektiren ve bir programda açık olanlar ayrıca belirtilir
- Temizlik listeleri: büyük dosyalar, geçici dosyalar, eski kurulum dosyaları ve aylardır kullanılmayan programlar
- Hızlı yeniden tarama: NTFS her değişikliği bir günlükte tutar; yeniden taramada yalnız değişen klasörler listelenir (bütün sistem sürücüsü on saniye yerine bir saniyenin altında), ByteScape'i bir sonraki açışta da
- Taramayı duraklatın, durdurun, sonra — ByteScape'i kapattıktan sonra bile — kaldığı yerden sürdürün
- Hard link'ler bir kez sayılır: Windows'un WinSxS klasörü ve diğer hard link'li dosyalar artık iki kez sayılmaz (test bilgisayarımızda 9,3 GB → 3,5 GB)
- Disk dolma tahmini: bu gidişle disk ne zaman dolacak, yeni dosyalar nereye gitti, son taramadan beri ne büyüdü
- Cache Radar: bilinen önbellek ve geçici klasörler haritada işaretlenir; silince ne olacağı ve doğru temizleme yolu yazılır
- OneDrive, Dropbox ve iCloud farkındalığı: yalnızca çevrimiçi dosyalar yer kaplamaz sayılır, "Yer aç" ile ne kadar kazanılacağı görünür
- Snapshot karşılaştırma: geçen aydan beri ne büyüdü, küçüldü, eklendi, silindi
- Düz cümleyle filtre: "6 aydır açılmamış 100 MB üstü videolar"
- Sağ tık: Explorer'da aç, özellikler, yolu kopyala, Windows menüsü
- Metin raporları ve daha sonra açılabilen ikili snapshot'lar (.sns)
- Komut satırı: `bytescape scan C:\ export Totals rapor.txt autoclose`
- Arayüz 7 dilde: İngilizce, Türkçe, Rusça, Çince (Basitleştirilmiş), Fransızca, İspanyolca, Portekizce (Brezilya) — Windows'un diline uyar
- Ücretsiz ve açık kaynak (MIT Lisansı): reklam yok, deneme süresi yok, hesap yok
- Klavyeyle ve ekran okuyucularla (Ekran Okuyucusu, NVDA, JAWS) kullanılır; Windows karşıtlık temalarına uyar
- Telemetri yok; ByteScape varsayılan olarak hiçbir ağ bağlantısı kurmaz

**Anahtar kelimeler:** disk alanı, disk kullanımı, treemap, depolama, temizlik, büyük dosyalar, klasör boyutu

### Русский (ru-RU)

**Название:** ByteScape

**Краткое описание:** Узнайте, куда ушло место на диске, — интерактивная карта (treemap) ваших дисков и папок.

**Описание:**
ByteScape показывает содержимое диска или папки в виде карты: каждый прямоугольник — файл или папка, его площадь пропорциональна размеру.
Самые большие блоки занимают больше всего места — двойной щелчок приближает, Backspace возвращает назад.

- Рисует во время сканирования: результаты видны сразу
- Программы на диске: сколько места занимает каждая установленная программа вместе с данными и кэшами
- Игры: Steam, Epic Games и GOG — в какие вы давно не играли и что осталось от удалённых игр
- Остатки разработки: node_modules, bin/obj, target, .venv старых проектов
- Повторяющиеся файлы, сравнение папок, история использования диска в виде диаграмм
- Безопасное удаление в корзину с ответом на вопрос «безопасно ли удалять?» и защитой системных папок
- Очистка: большие, временные и старые установочные файлы, программы, которыми вы давно не пользуетесь
- Быстрое повторное сканирование по журналу изменений NTFS, прогноз заполнения диска
- Жёсткие ссылки учитываются один раз, облачные файлы OneDrive / Dropbox / iCloud распознаются
- Отчёты CSV / JSON / HTML, командная строка, «Открыть в ByteScape» в Проводнике
- Бесплатно и с открытым исходным кодом (лицензия MIT): без рекламы, без пробного периода, без учётной записи
- Удобен для клавиатуры и программ чтения с экрана (Экранный диктор, NVDA, JAWS), поддерживает контрастные темы Windows
- Интерфейс на 7 языках; без телеметрии, по умолчанию никаких сетевых подключений

**Ключевые слова:** место на диске, анализ диска, treemap, очистка диска, большие файлы, размер папок

### 简体中文 (zh-CN)

**标题：** ByteScape

**简短描述：** 看看磁盘空间都去哪儿了——以交互式矩形树图展示您的驱动器和文件夹。

**描述：**
ByteScape 以矩形树图显示驱动器或文件夹的内容：每个矩形是一个文件或文件夹，面积与大小成正比。
最大的方块就是占用空间最多的内容——双击放大，按 Backspace 返回。

- 边扫描边绘制：结果立即可见
- 磁盘上的程序：每个已安装程序连同其数据和缓存占用的空间
- 游戏：Steam、Epic Games 和 GOG——哪些游戏已很久没玩，已卸载的游戏留下了什么
- 开发残留：旧项目的 node_modules、bin/obj、target、.venv
- 重复文件、文件夹比较、磁盘使用历史图表
- 安全删除到回收站，清楚回答“可以安全删除吗？”，并保护系统文件夹
- 清理：大文件、临时文件、旧安装文件以及数月未使用的程序
- 基于 NTFS 更改日志的快速重新扫描，磁盘写满预测
- 硬链接只计一次，识别 OneDrive / Dropbox / iCloud 云文件
- CSV / JSON / HTML 报告、命令行、资源管理器中的“在 ByteScape 中打开”
- 免费且开源（MIT 许可证）：无广告、无试用期、无需账户
- 支持键盘和屏幕阅读器（讲述人、NVDA、JAWS），并遵循 Windows 对比度主题
- 界面支持 7 种语言；无遥测，默认不建立任何网络连接

**关键字：** 磁盘空间, 磁盘分析, 矩形树图, 磁盘清理, 大文件, 文件夹大小

### Français (fr-FR)

**Titre :** ByteScape

**Description courte :** Découvrez où est passé l'espace de votre disque — une carte interactive (treemap) de vos lecteurs et dossiers.

**Description :**
ByteScape affiche le contenu d'un lecteur ou d'un dossier sous forme de carte : chaque rectangle est un fichier ou un dossier, sa surface est
proportionnelle à sa taille. Les gros blocs sont ceux qui prennent le plus de place — double-cliquez pour zoomer, Retour arrière pour revenir.

- Dessine pendant l'analyse : les résultats apparaissent tout de suite
- Programmes sur le disque : l'espace pris par chaque programme installé, avec ses données et ses caches
- Jeux : Steam, Epic Games et GOG — ceux auxquels vous ne jouez plus et ce que les jeux désinstallés ont laissé
- Restes de développement : node_modules, bin/obj, target, .venv des anciens projets
- Fichiers en double, comparaison de dossiers, historique de l'utilisation du disque en graphiques
- Suppression sûre vers la Corbeille, avec une réponse claire à « suppression sans risque ? » et la protection des dossiers système
- Nettoyage : gros fichiers, fichiers temporaires, anciens fichiers d'installation et programmes inutilisés depuis des mois
- Réanalyses rapides grâce au journal des modifications NTFS, prévision du remplissage du disque
- Liens physiques comptés une seule fois, fichiers cloud OneDrive / Dropbox / iCloud reconnus
- Rapports CSV / JSON / HTML, ligne de commande, « Ouvrir dans ByteScape » dans l'Explorateur
- Gratuit et open source (licence MIT) : sans publicité, sans période d’essai, sans compte
- Utilisable au clavier et avec les lecteurs d’écran (Narrateur, NVDA, JAWS), respecte les thèmes de contraste de Windows
- Interface en 7 langues ; aucune télémétrie, aucune connexion réseau par défaut

**Mots-clés :** espace disque, analyse du disque, treemap, nettoyage, gros fichiers, taille des dossiers

### Español (es-ES)

**Título:** ByteScape

**Descripción breve:** Descubre adónde ha ido el espacio de tu disco: un mapa interactivo (treemap) de tus unidades y carpetas.

**Descripción:**
ByteScape muestra el contenido de una unidad o carpeta como un mapa: cada rectángulo es un archivo o una carpeta y su área es proporcional
a su tamaño. Los bloques grandes son los que más ocupan: doble clic para acercar, Retroceso para volver.

- Dibuja mientras analiza: los resultados aparecen al instante
- Programas en el disco: el espacio que ocupa cada programa instalado con sus datos y cachés
- Juegos: Steam, Epic Games y GOG: a cuáles ya no juegas y qué dejaron los juegos desinstalados
- Restos de desarrollo: node_modules, bin/obj, target, .venv de proyectos antiguos
- Archivos duplicados, comparación de carpetas, historial de uso del disco con gráficos
- Eliminación segura a la Papelera de reciclaje, con una respuesta clara a «¿es seguro eliminarlo?» y protección de las carpetas del sistema
- Limpieza: archivos grandes, temporales, instaladores antiguos y programas que no usas desde hace meses
- Reanálisis rápidos con el diario de cambios de NTFS, previsión de cuándo se llenará el disco
- Vínculos físicos contados una sola vez; archivos en la nube de OneDrive / Dropbox / iCloud reconocidos
- Informes CSV / JSON / HTML, línea de comandos, «Abrir en ByteScape» en el Explorador
- Gratis y de código abierto (licencia MIT): sin anuncios, sin periodo de prueba, sin cuenta
- Se usa con el teclado y con lectores de pantalla (Narrador, NVDA, JAWS) y respeta los temas de contraste de Windows
- Interfaz en 7 idiomas; sin telemetría, sin conexiones de red por defecto

**Palabras clave:** espacio en disco, análisis de disco, treemap, limpieza, archivos grandes, tamaño de carpetas

### Português (pt-BR)

**Título:** ByteScape

**Descrição curta:** Descubra para onde foi o espaço do seu disco — um mapa interativo (treemap) das suas unidades e pastas.

**Descrição:**
O ByteScape mostra o conteúdo de uma unidade ou pasta como um mapa: cada retângulo é um arquivo ou uma pasta, com área proporcional ao
tamanho. Os blocos grandes são os que mais ocupam espaço — clique duas vezes para ampliar, Backspace para voltar.

- Desenha enquanto analisa: os resultados aparecem na hora
- Programas no disco: o espaço de cada programa instalado, com seus dados e caches
- Jogos: Steam, Epic Games e GOG — quais você não joga mais e o que os jogos desinstalados deixaram
- Sobras de desenvolvimento: node_modules, bin/obj, target, .venv de projetos antigos
- Arquivos duplicados, comparação de pastas, histórico de uso do disco em gráficos
- Exclusão segura para a Lixeira, com uma resposta clara a "é seguro excluir?" e proteção das pastas do sistema
- Limpeza: arquivos grandes, temporários, instaladores antigos e programas sem uso há meses
- Reanálises rápidas com o diário de alterações do NTFS, previsão de quando o disco vai encher
- Links físicos contados uma única vez; arquivos na nuvem do OneDrive / Dropbox / iCloud reconhecidos
- Relatórios CSV / JSON / HTML, linha de comando, "Abrir no ByteScape" no Explorador
- Gratuito e de código aberto (licença MIT): sem anúncios, sem período de teste, sem conta
- Funciona com teclado e leitores de tela (Narrador, NVDA, JAWS) e segue os temas de contraste do Windows
- Interface em 7 idiomas; sem telemetria, nenhuma conexão de rede por padrão

**Palavras-chave:** espaço em disco, análise de disco, treemap, limpeza, arquivos grandes, tamanho de pastas

## Known limits and decisions

- **The fast NTFS (MFT) scan** needs administrator rights. A packaged (Store) app cannot elevate itself, so the package
  hides "Restart as administrator" and the Store app always uses the unprivileged parallel scan (quick rescans from the
  change journal still work). Offering the MFT mode in the Store app would need `<rescap:Capability Name="allowElevation" />`
  in the manifest and a justification in Partner Center (restricted capability, extra review). The portable exe can
  restart itself as administrator from the menu.
- Only an x64 package is built (ARM64 would be a second package with `-r win-arm64` and `ProcessorArchitecture` set in the
  manifest).
- `SeBackupPrivilege` silently fails for non-administrators, and Store apps cannot run elevated: folders that cannot be read
  appear on the map as "Unreadable space".
- The package is about 63 MB (self-contained .NET 8 + WPF). A framework-dependent package would be smaller, but users would
  then need the .NET 8 Desktop Runtime, which the Store does not install by itself.
