# Microsoft Store submission profile

Everything Partner Center asks for that is **not** per-language listing copy. The copy itself lives in
`msix/listing/` - one plain-text deck per listing language, plus the shared rows, the search terms and
the forbidden-term list. See `msix/listing/README.md`, and `STORE_PUBLISHING.md` for the build and
import flow. Do not restate listing copy here: two sources of truth is how a corrected claim survives
in the live listing.

## Submission profile

- Product name (Store title, matches the reserved app name): `Streams Player`
- In-app / documentation wordmark (unchanged): `STREAMS Player`
- Publisher display name: `SerZhyAle`
- Primary category: `Entertainment`
- Optional secondary category: `Music`
- Device family: Windows Desktop, x64
- Minimum OS: Windows 10 version 1809 (`10.0.17763.0`)
- Price: Free
- License: MIT
- Website: `https://serzhyale.github.io/StreamsPlayer/`
- Support: `https://github.com/SerZhyAle/StreamsPlayer/issues`
- Support email: `serzhyale@gmail.com`
- Privacy policy: `https://serzhyale.github.io/StreamsPlayer/privacy.html`
- Source: `https://github.com/SerZhyAle/StreamsPlayer`

## Listing languages

Thirteen, matching the interface: `en-us`, `ru`, `uk`, `de`, `it`, `es`, `fr`, `pt-br`, `zh-hans`,
`hi`, `bn`, `ar`, `ur`. The list is not maintained here either - it comes from the
`InterfaceLanguages` registry in `StreamsPlayer.Core`, which every tool reads.

Each language needs a description **and** at least one screenshot to leave Incomplete.
`assets/store/app-<listing-code>.png` holds one real capture per language, produced by
`tools/store/capture-store-screenshots.ps1`.

## What's new

Leave blank for a first submission. For an update, replace the version and keep the text within 1,500
characters. Written per submission, in the same three languages the release notes use elsewhere
(English, Russian, Ukrainian) - the ten machine-translated languages get the English text, because a
release note is dated prose nobody proofreads twice.

```text
Version REPLACE_VERSION

- REPLACE_USER_VISIBLE_CHANGE
- REPLACE_USER_VISIBLE_CHANGE
```

```text
Версия REPLACE_VERSION

- REPLACE_USER_VISIBLE_CHANGE
- REPLACE_USER_VISIBLE_CHANGE
```

```text
Версія REPLACE_VERSION

- REPLACE_USER_VISIBLE_CHANGE
- REPLACE_USER_VISIBLE_CHANGE
```

### Prepared for 26.0819.0156

**Released on GitHub and submitted to winget on 2026-08-19.** The Partner Center upload for this
version is console work; see the gap note below before writing the Store's What's new.

**The Store is still on 26.0806.2225.** The 26.0809.0022 package was built and released on GitHub and
winget but never uploaded, so a Store user moving to this version also receives everything in
`msix/listing/release-notes/26.0809.0022.{en-us,ru,uk}.txt` - the bundled channel list, the topic
filter, sharing a channel as text, resumed playback, what is on air, the caption over the video, the
silent-freeze detection and the adaptive quality ceiling. The three blocks below are written for
GitHub and winget, which shipped 26.0809.0022 already. A Store submission needs its own What's new
covering both releases, written when the upload is actually made rather than improvised at the
console.

Screenshots under `assets/store/` still date from 2026-07-27, so they predate the dark theme, the
consolidated header, the compact panel and everything in this release. Not a blocker, still the
obvious next thing to regenerate.

Ships SP-0080 (the compact radio panel, with `ScreenPlacement` bringing it back onto a visible screen)
and SP-0086 (the random station and its silent-station hunt), plus the SP-0008 shortcut-name fix, the
click-to-position volume sliders in both windows, and the dark-theme glyph foreground.

Deliberately not bulleted: the `.gitignore` and `docs/PLAYBACK_RESILIENCE.md` housekeeping and the
regenerated catalog snapshot - the snapshot is a data refresh a user reads as the catalog simply being
current, not as a feature.

Measured against the 1,500-character limit: 1357 en, 1316 ru, 1302 uk. The same three blocks live as
`msix/listing/release-notes/26.0819.0156.{en-us,ru,uk}.txt`, which is what
`tools/store/write-release-notes.ps1` reads - these fences are a copy of those files, not a second
source.

```text
Version 26.0819.0156

- The catalog can shrink to a compact radio panel while a station plays. The small window stays above other programs and carries the station, the current track, the volume, the transport, the sleep timer with its countdown and Random station - one taskbar button, one Alt+Tab entry, one sound.
- Volume, the sleep timer and stop work the same on the panel and in the catalog, and coming back restores the full window as you left it - scroll position, filter and selection included.
- Stopping the radio, or a station that drops out, leaves the panel where it is instead of throwing the catalog over your work. Dragged anywhere, the panel returns inside a visible screen. It is a mode for the session - the next launch opens the catalog.
- Play a random station. One press picks from the whole catalog, ignoring the search, the facets and the active collection, and skips hidden stations, video and RTSP. It is on the Operations menu and on the panel.
- A drawn station that stays silent is replaced by the next draw without a dialog, and after a run of silent ones the hunt stops and says so.
- Click anywhere on a volume slider to jump straight to that level, in the catalog and in the player.
- Pin a station with a very long name to the desktop without losing the application, and glyph buttons are legible again in the dark theme.
```

```text
Версия 26.0819.0156

- Каталог умеет ужаться до компактной радиопанели, пока играет станция. Маленькое окно держится поверх других программ и несёт станцию, текущий трек, громкость, управление, таймер сна с отсчётом и «Случайную станцию» - одна кнопка на панели задач, один пункт в Alt+Tab, один звук.
- Громкость, таймер сна и остановка работают одинаково на панели и в каталоге, а возврат восстанавливает полное окно таким, каким вы его оставили - вместе с прокруткой, фильтром и выделением.
- Остановка радио или пропавшая станция оставляют панель на месте, а не бросают каталог поверх вашей работы. Куда бы вы панель ни перетащили, она вернётся в пределы видимого экрана. Это режим сеанса - следующий запуск снова открывает каталог.
- Включить случайную станцию. Одно нажатие выбирает из всего каталога, не глядя на поиск, фасеты и активную подборку, и пропускает скрытые станции, видео и RTSP. Пункт есть в меню «Операции» и на панели.
- Выпавшая станция, которая молчит, заменяется следующей без единого диалога, а после череды молчаливых перебор останавливается и говорит об этом.
- Щелчок в любом месте ползунка громкости сразу ставит этот уровень - и в каталоге, и в плеере.
- Станцию с очень длинным названием можно вынести на рабочий стол, не теряя программу, а кнопки-глифы снова читаются в тёмной теме.
```

```text
Версія 26.0819.0156

- Каталог уміє стиснутися до компактної радіопанелі, поки грає станція. Маленьке вікно тримається поверх інших програм і несе станцію, поточний трек, гучність, керування, таймер сну з відліком і «Випадкову станцію» - одна кнопка на панелі задач, один пункт в Alt+Tab, один звук.
- Гучність, таймер сну та зупинка працюють однаково на панелі й у каталозі, а повернення відновлює повне вікно таким, яким ви його лишили - разом із прокручуванням, фільтром і виділенням.
- Зупинка радіо або станція, що зникла, лишають панель на місці, а не кидають каталог поверх вашої роботи. Хоч куди ви панель перетягнете, вона повернеться в межі видимого екрана. Це режим сеансу - наступний запуск знову відкриває каталог.
- Увімкнути випадкову станцію. Одне натискання обирає з усього каталогу, не зважаючи на пошук, фасети й активну добірку, і пропускає приховані станції, відео та RTSP. Пункт є в меню «Операції» та на панелі.
- Станція, що випала і мовчить, замінюється наступною без жодного діалогу, а після низки мовчазних перебір зупиняється і каже про це.
- Клац у будь-якому місці повзунка гучності одразу ставить цей рівень - і в каталозі, і у програвачі.
- Станцію з дуже довгою назвою можна винести на робочий стіл, не втрачаючи програму, а кнопки-гліфи знову читаються в темній темі.
```

#### Store-only What's new for 26.0819.0156

Not a second source for the release notes above - a different text for a different audience. The Store
sits on 26.0806.2225, so its user has never seen 26.0809.0022. These blocks cover both releases and
are what Partner Center gets; GitHub and winget keep the blocks above. The ten machine-translated
languages get the English text.

```text
Version 26.0819.0156

- The catalog can shrink to a compact radio panel while a station plays - always on top, carrying the station, the current track, the volume, the transport, the sleep timer and Random station.
- Play a random station. One press picks from the whole catalog, ignoring the search and the facets, and skips hidden stations, video and RTSP. One that stays silent gives way to the next draw.
- A built-in channel list. A copy of the stream bank ships inside the app, so the catalog fills on a first launch with no network. It adds and updates, never removes, and says how old it is.
- A topic filter, sharing a channel as one line of text, playback that resumes where the last session left off, and what is on air under the channel name.
- The player says why the picture stopped, a stream that stalls while pretending to play is caught and re-opened, and video quality follows the connection and is remembered per channel.
- Fixed - a very long station name could take the desktop shortcut down with it, dark-theme glyph buttons were unreadable, closing the catalog over a tile could crash it, and a radio stream that ended kept the machine awake.
```

```text
Версия 26.0819.0156

- Каталог умеет ужаться до компактной радиопанели, пока играет станция - поверх других окон, со станцией, текущим треком, громкостью, управлением, таймером сна и случайной станцией.
- Включить случайную станцию. Одно нажатие выбирает из всего каталога, не глядя на поиск и фасеты, и пропускает скрытые станции, видео и RTSP. Молчащая уступает место следующей.
- Встроенный список каналов. Копия банка потоков лежит внутри программы, поэтому каталог заполняется при первом запуске без сети. Он добавляет и обновляет, ничего не удаляет и называет свой возраст.
- Фильтр по темам, отправка канала одной строкой текста, продолжение воспроизведения с места прошлого сеанса и трек, который станция играет прямо сейчас, под названием канала.
- Плеер объясняет, почему картинка остановилась, поток, который делает вид, что играет, переоткрывается, а качество видео следует за соединением и запоминается для канала.
- Исправлено - очень длинное название станции могло уронить программу при выносе ярлыка, кнопки-глифы в тёмной теме были нечитаемы, закрытие каталога над плиткой могло его уронить, а завершившееся радио держало компьютер без сна.
```

```text
Версія 26.0819.0156

- Каталог уміє стиснутися до компактної радіопанелі, поки грає станція - поверх інших вікон, зі станцією, поточним треком, гучністю, керуванням, таймером сну та випадковою станцією.
- Увімкнути випадкову станцію. Одне натискання обирає з усього каталогу, не зважаючи на пошук і фасети, і пропускає приховані станції, відео та RTSP. Мовчазна поступається наступній.
- Вбудований список каналів. Копія банку потоків лежить усередині програми, тож каталог заповнюється при першому запуску без мережі. Він додає та оновлює, нічого не видаляє і називає свій вік.
- Фільтр за темами, надсилання каналу одним рядком тексту, продовження відтворення з місця минулого сеансу і трек, який станція грає просто зараз, під назвою каналу.
- Програвач пояснює, чому картинка зупинилася, потік, що вдає, ніби грає, перевідкривається, а якість відео стежить за з'єднанням і запам'ятовується для каналу.
- Виправлено - дуже довга назва станції могла аварійно завершити програму при винесенні ярлика, кнопки-гліфи в темній темі були нечитабельні, закриття каталогу над плиткою могло її уронити, а радіо, що скінчилося, тримало комп'ютер без сну.
```

### Prepared for 26.0809.0022

**Released on GitHub and submitted to winget on 2026-08-09; the Partner Center upload is still to
happen.** The package is built and waiting at `msix/dist/StreamsPlayer-26.0809.0022-windows-x64.msix`
(139 133 279 bytes, Identity `SZA.StreamsPlayer`, Publisher `CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD`,
Identity Version `26.809.22.0`, x64, `runFullTrust` only, `THIRD-PARTY-NOTICES.txt` inside), and the
listing import with this version's notes in all thirteen languages is at
`msix/dist/store-listing-import.csv`, built from the owner's 2026-08-08 export. Both steps past that -
the upload and the certification submission - are console work only.

Screenshots under `assets/store/` still date from 2026-07-27 and therefore predate the dark theme, the
consolidated header and everything in this release. They were already stale for 26.0806.2225 and are
not a blocker, but they are the obvious next thing to regenerate.

Everything on `main` after 26.0806.2225, which is the Store's own previous stamp, and after
26.0806.2131 on GitHub and winget. The two channels converge again here - this one text goes to the
GitHub Release body, the three winget locale manifests and Partner Center, because it was written
with no colon-plus-space anywhere and therefore survives the winget YAML scalar unchanged.

Ships SP-0052/SP-0066 (the bundled channel list), SP-0059, SP-0061 (the topic filter), SP-0058
(sharing a channel as text), SP-0062 (playback resumes), SP-0056, then SP-0070 (silent-freeze
detection), SP-0079 (the shorter reconnect budget), SP-0071/SP-0076/SP-0077 (the adaptive quality
ceiling, remembered per channel and re-opened there), SP-0072 (the caption over the video),
SP-0073/SP-0074 (what is on air), SP-0081 (stop keeps the station), SP-0065, SP-0085, SP-0069's
memory fixes and the accessibility, resize and chrome work.

Deliberately not bulleted: SP-0054, SP-0055, SP-0057, SP-0060, SP-0064 and SP-0067 - a clock-jitter
budget, log retention, a formatter that cannot throw, a build guard, an automation name and list
virtualization are none of them a line a user would recognise as their own. SP-0078 is not bulleted
either, because the live-edge corridor only runs on the opt-in FlyleafLib engine.

Measured against the 1,500-character limit: 1466 en, 1499 ru, 1492 uk. The same three blocks live as
`msix/listing/release-notes/26.0809.0022.{en-us,ru,uk}.txt`, which is what
`tools/store/write-release-notes.ps1` reads - these fences are a copy of those files, not a second
source.

```text
Version 26.0809.0022

- A built-in channel list. A copy of the stream bank now ships inside the app, so the catalog fills on a first launch with no network - offered at the start, after a failed update, and always from Settings. It adds and updates, never removes, and says how old it is.
- A topic filter over the catalog's own topics, translated for reading while each channel keeps its original name.
- Share a channel as one short line of text, and paste one back in.
- Playback resumes where the last session left off.
- What is on air - the track the station is playing right now, under the channel name.
- Stopping the sound now keeps the station. The button becomes Resume audio, and the volume slider and the sleep timer stay where they were.
- The player says why the picture stopped - connecting, signal lost, reconnecting, switching quality - over the video, so the explanation is there after the controls hide.
- A stream that stops sending while pretending to play is caught and re-opened, and the Retry dialog now arrives in about half a minute instead of two.
- Video quality follows what the connection actually delivers, is remembered per channel, and the next play opens there.
- Minimizing the catalog no longer minimizes the player, and Settings can be resized.
- Fixed - closing the catalog with the pointer over a tile could crash it, a radio stream that ended kept the machine awake, and the player controls did not mirror in Arabic and Urdu.
```

```text
Версия 26.0809.0022

- Встроенный список каналов. Копия банка потоков лежит внутри программы, поэтому каталог заполняется при первом запуске без сети - на старте, после неудачного обновления и всегда из настроек. Он добавляет и обновляет, ничего не удаляет и называет свой возраст.
- Фильтр по темам каталога - названия тем переведены для чтения, а канал сохраняет исходное имя.
- Поделиться каналом одной короткой строкой текста и вставить такую строку обратно.
- Воспроизведение продолжается с того места, где закончился прошлый сеанс.
- Что сейчас в эфире - трек, который станция играет прямо сейчас, под названием канала.
- Остановка звука больше не сбрасывает станцию. Кнопка становится «Продолжить», а громкость и таймер сна остаются на месте.
- Плеер объясняет, почему картинка остановилась - подключение, сигнал потерян, переподключение, смена качества - надписью поверх видео, видной и после того, как панель спряталась.
- Поток, который перестал передавать, но делает вид, что играет, распознаётся и переоткрывается, а окно с кнопкой «Повторить» приходит примерно за полминуты вместо двух.
- Качество видео следует за тем, что реально выдаёт соединение, запоминается для канала, и следующее включение начинается с него.
- Свёртывание каталога больше не свёртывает плеер, а окно настроек можно менять по размеру.
- Исправлено - закрытие каталога с указателем на плитке могло уронить программу, завершившееся радио держало компьютер без сна, а панель плеера не зеркалилась в арабском и урду.
```

```text
Версія 26.0809.0022

- Вбудований список каналів. Копія банку потоків лежить усередині програми, тож каталог заповнюється при першому запуску без мережі - на старті, після невдалого оновлення і завжди з налаштувань. Він додає та оновлює, нічого не видаляє і називає свій вік.
- Фільтр за темами каталогу - назви тем перекладені для читання, а канал зберігає початкове ім'я.
- Поділитися каналом одним коротким рядком тексту і вставити такий рядок назад.
- Відтворення продовжується з того місця, де закінчився минулий сеанс.
- Що зараз в ефірі - трек, який станція грає просто зараз, під назвою каналу.
- Зупинка звуку більше не скидає станцію. Кнопка стає «Продовжити», а гучність і таймер сну лишаються на місці.
- Програвач пояснює, чому картинка зупинилася - підключення, сигнал втрачено, перепідключення, зміна якості - написом поверх відео, видним і після того, як панель сховалася.
- Потік, який перестав передавати, але вдає, що грає, розпізнається і перевідкривається, а вікно з кнопкою «Повторити» приходить приблизно за пів хвилини замість двох.
- Якість відео стежить за тим, що насправді видає з'єднання, запам'ятовується для кожного каналу, і наступне ввімкнення починається з неї.
- Згортання каталогу більше не згортає програвач, а вікно налаштувань можна змінювати за розміром.
- Виправлено - закриття каталогу з вказівником на плитці могло аварійно завершити програму, радіо, що скінчилося, тримало комп'ютер без сну, а панель програвача не дзеркалилася в арабській та урду.
```
### Prepared for 26.0806.2225 (Store only - the channels deliberately disagree)

**The Store ships 26.0806.2225 while GitHub and winget stay on 26.0806.2131.** The owner's call on
2026-08-06, after the packaging defect below was found an hour past the 26.0806.2131 release.

`VideoLAN.LibVLC.Windows.targets` keys its three copy switches off `$(Platform)`, which is `AnyCPU`
for a WPF project, so `win-x64`, `win-x86` **and** `win-arm64` all landed in the output no matter which
runtime was published. Only the tree matching the process architecture can ever be loaded, so two of
them were pure weight: the MSIX fell from 209.4 MB to 125.3 MB - **84.1 MB, about 40%** - when
`StreamsPlayer.App.csproj` started keying them off `$(RuntimeIdentifier)` instead.

That is different package content, so it cannot ship under 26.0806.2131, whose zip is already
published. Hence a second stamp for one channel. Proof that the slimming is inert at runtime:
`libvlc/win-x64` is byte-identical between the two packages - all 425 files match on name, size and
CRC32 - and the slimmed publish output was run, loaded the native engine (`VLC | module=direct3d11`,
`module=mp4`, `module=drawable` in `Current.log`) and captured a grid preview.

The bullets below therefore cover everything since **26.0728.1352**, the last version actually
submitted here, and not merely since the previous GitHub release. Written per submission, in the same
three languages the release notes use elsewhere; the ten machine-translated languages get the English
text. Measured: 1232 / 1276 / 1288 characters against the 1,500 limit.

The listing copy itself needed no import - `build-store-listing-csv.ps1` against a fresh export taken
2026-08-07 filled **0 cells across all thirteen languages**, every one `complete`. Only the
`ReleaseNotes` row had to be written, which that builder deliberately never touches.

```text
Version 26.0806.2225

- A dark interface that follows Windows and switches with it while the app runs. Settings has the final word: follow the system, always light, or always dark.
- About the channel - a new item in a channel's menu that lists its properties and reports what the stream actually sends: video and audio formats, picture size, frame rate, sound channels and sample rate, and the observed data rate.
- A calmer main window: search now sits beside the product name, the filter and sorting row appears only when you press "Filters and sorting", and the remaining header actions moved into one "Operations" menu.
- The interface language is now the first tab in Settings, marked with a globe, so it can be found without reading a word.
- The player's signal stripe is colour-coded: green while the stream is fine, yellow while it stalls or rebuffers, red when the signal is gone.
- The player's controls now hide after a short idle in a window too, not only in fullscreen.
- A fourth, smallest grid tile: the picture alone, with the name and buttons under the pointer.
- Settings, About, Send logs to the author now writes the archive to your own save folder and tells you its full path.
- About 40% smaller to download.
```

```text
Версия 26.0806.2225

- Тёмное оформление, которое следует за Windows и переключается вместе с системой прямо во время работы. Последнее слово за настройками: как в системе, всегда светлое или всегда тёмное.
- «О канале» - новый пункт в меню канала: перечисляет свойства канала и показывает, что поток передаёт на самом деле - форматы видео и звука, размер картинки, частоту кадров, число звуковых каналов и частоту дискретизации, а также измеренную скорость потока.
- Спокойнее главное окно: поиск перебрался к названию программы, строка фильтров и сортировки появляется только по кнопке «Фильтры и сортировка», а остальные действия шапки собраны в одно меню «Операции».
- Язык интерфейса стал первой вкладкой настроек и отмечен глобусом - его можно найти, не читая ни слова.
- Полоса сигнала в плеере теперь цветная: зелёная, пока поток в порядке, жёлтая при задержках и перебуферизации, красная, когда сигнала нет.
- Панель управления плеера прячется после паузы и в оконном режиме, а не только в полноэкранном.
- Четвёртый, самый мелкий размер плитки: только картинка, а название и кнопки - под указателем.
- «Настройки», «О программе», «Отправить журналы автору» теперь кладёт архив в вашу же папку сохранения и сообщает его полный путь.
- Загрузка примерно на 40% меньше.
```

```text
Версія 26.0806.2225

- Темне оформлення, яке слідує за Windows і перемикається разом із системою просто під час роботи. Останнє слово за налаштуваннями: як у системі, завжди світле або завжди темне.
- «Про канал» - новий пункт у меню каналу: перелічує властивості каналу й показує, що потік передає насправді - формати відео та звуку, розмір картинки, частоту кадрів, кількість звукових каналів і частоту дискретизації, а також виміряну швидкість потоку.
- Спокійніше головне вікно: пошук перемістився до назви програми, рядок фільтрів і сортування з'являється лише за кнопкою «Фільтри та сортування», а решта дій шапки зібрані в одне меню «Операції».
- Мова інтерфейсу стала першою вкладкою налаштувань і позначена глобусом - її можна знайти, не читаючи жодного слова.
- Смуга сигналу у програвачі тепер кольорова: зелена, поки потік у порядку, жовта під час затримок і перебуферизації, червона, коли сигналу немає.
- Панель керування програвача ховається після паузи й у віконному режимі, а не лише в повноекранному.
- Четвертий, найдрібніший розмір плитки: лише картинка, а назва та кнопки - під вказівником.
- «Налаштування», «Про програму», «Надіслати журнали авторові» тепер кладе архів у вашу ж папку збереження й повідомляє його повний шлях.
- Завантаження приблизно на 40% менше.
```

### Prepared for 26.0806.2131 (stamped, released on GitHub and winget, not submitted to the Store)

Written for everything on `main` after 26.0730.1512, which is the largest payload the product has
shipped in one release: SP-0045 (the player's signal stripe gains colour), SP-0046 (a dark interface
that follows Windows), SP-0047 (the player's controls auto-hide in a window too), SP-0048 (a fourth,
smallest grid tile), SP-0049 (the diagnostic archive lands in the user's own save folder), SP-0050 (the
consolidated header, the on-demand filter row and the language tab in Settings) and SP-0053 (About the
channel). The version was stamped from the UTC minute of this release pass into these three blocks and
`Directory.Build.props` together; the GitHub Release body and the three winget locale manifests take
the same value.

Deliberately not bulleted: the localization, documentation and site work that carries those features,
the dark-theme fix to the language list, and the packaging commits - none of them is a change a user
would recognise as its own line.

**Shipped ahead of a complete observation sheet - the owner's explicit call on 2026-08-06.** Observed
on the development machine and captured under `temp/release-26.0806/`: the consolidated header, the
operations menu in grid mode, the on-demand filter row, the dark interface across the catalog, the
player's controls hiding and returning, and the green signal state on a playing channel. Not observed:
the About-the-channel measurement end to end, the archive path from "Send logs to the author", the
smallest grid tile, the restart-persistence of the filter row, and the language tab in the dark theme
after its fix. Those five sit in `PLAN` at `BlockNeedUserTest`; the copy shipping is not the
observation happening, and this note is where the gap is recorded.

```text
Version 26.0806.2131

- A dark interface that follows Windows and switches with it while the app runs. Settings has the final word: follow the system, always light, or always dark.
- About the channel - a new item in a channel's menu that lists its properties and reports what the stream actually sends: video and audio formats, picture size, frame rate, sound channels and sample rate, and the observed data rate.
- A calmer main window: search now sits beside the product name, the filter and sorting row appears only when you press "Filters and sorting", and the remaining header actions moved into one "Operations" menu.
- The interface language is now the first tab in Settings, marked with a globe, so it can be found without reading a word.
- The player's signal stripe is colour-coded: green while the stream is fine, yellow while it stalls or rebuffers, red when the signal is gone.
- The player's controls now hide after a short idle in a window too, not only in fullscreen, and come back on a click on the picture.
- A fourth, smallest grid tile: the picture alone, with the name and the buttons appearing under the pointer.
- "Send logs to the author" writes the archive to your own save folder and tells you its full path.
```

```text
Версия 26.0806.2131

- Тёмное оформление, которое следует за Windows и переключается вместе с системой прямо во время работы. Последнее слово за настройками: как в системе, всегда светлое или всегда тёмное.
- «О канале» - новый пункт в меню канала: перечисляет свойства канала и показывает, что поток передаёт на самом деле - форматы видео и звука, размер картинки, частоту кадров, число звуковых каналов и частоту дискретизации, а также измеренную скорость потока.
- Спокойнее главное окно: поиск перебрался к названию программы, строка фильтров и сортировки появляется только по кнопке «Фильтры и сортировка», а остальные действия шапки собраны в одно меню «Операции».
- Язык интерфейса стал первой вкладкой настроек и отмечен глобусом - его можно найти, не читая ни слова.
- Полоса сигнала в плеере теперь цветная: зелёная, пока поток в порядке, жёлтая при задержках и перебуферизации, красная, когда сигнала нет.
- Панель управления плеера прячется после паузы и в оконном режиме, а не только в полноэкранном, и возвращается по щелчку на картинке.
- Четвёртый, самый мелкий размер плитки: только картинка, а название и кнопки появляются под указателем.
- «Отправить журналы автору» кладёт архив в вашу же папку сохранения и сообщает его полный путь.
```

```text
Версія 26.0806.2131

- Темне оформлення, яке слідує за Windows і перемикається разом із системою просто під час роботи. Останнє слово за налаштуваннями: як у системі, завжди світле або завжди темне.
- «Про канал» - новий пункт у меню каналу: перелічує властивості каналу й показує, що потік передає насправді - формати відео та звуку, розмір картинки, частоту кадрів, кількість звукових каналів і частоту дискретизації, а також виміряну швидкість потоку.
- Спокійніше головне вікно: пошук перемістився до назви програми, рядок фільтрів і сортування з'являється лише за кнопкою «Фільтри та сортування», а решта дій шапки зібрані в одне меню «Операції».
- Мова інтерфейсу стала першою вкладкою налаштувань і позначена глобусом - її можна знайти, не читаючи жодного слова.
- Смуга сигналу у програвачі тепер кольорова: зелена, поки потік у порядку, жовта під час затримок і перебуферизації, червона, коли сигналу немає.
- Панель керування програвача ховається після паузи й у віконному режимі, а не лише в повноекранному, і повертається за клацанням на картинці.
- Четвертий, найдрібніший розмір плитки: лише картинка, а назва та кнопки з'являються під вказівником.
- «Надіслати журнали авторові» кладе архів у вашу ж папку збереження й повідомляє його повний шлях.
```

### Prepared for 26.0730.1512 (stamped, released on GitHub, never submitted to the Store)

Written for the changes on `main` after 26.0728.1352, so the text exists before the tag rather than
being improvised during it. The version was stamped from the UTC minute of the release pass into these
three blocks and `Directory.Build.props` together; the GitHub Release body and the three winget locale
manifests take the same value whenever those legs run. The log was re-read at stamping time: `main`
carries nothing after `9742a6d` that needs its own bullet, and the playback-reachability work in
progress was parked out of this release rather than shipped unreviewed.

Covered here: `944f40b` (catalog window returns to the front) and `53a89ca` (SP-0040, send logs to
the author, which also keeps the previous session's log). Deliberately not bulleted: the richer
per-session playback records, which exist for the author reading a report and change nothing the user
sees, and the packaging and documentation commits.

**Shipped ahead of two owner observations - the owner's explicit call on 2026-07-30.** The first two
bullets describe SP-0040, whose criterion 4 is *the prepared mail seen on a configured client*. That
criterion is not merely un-run, it is **unobservable on the development machine**: the default
`mailto:` handler there is the new Outlook, never configured, so the button opens an account-setup
screen instead of a compose window. SP-0040 proved the link with unit tests for exactly that reason.
SP-0034's exit condition is likewise unobserved.

Both tickets stay at `BlockNeedUserTest` and neither was marked Verified - the copy shipping is not
the observation happening. Proven: implemented, and the release-parity gate green over it (402/402).
Not proven: a configured client actually rendering the message. The first user with a working mail
client settles it; until then this note is where the gap is recorded.

```text
Version 26.0730.1512

- Report a problem without hunting for files: Settings, About, Send logs to the author packs the diagnostic logs and opens your mail program with the message ready. Nothing is sent automatically - you attach the archive and press Send.
- The player keeps the previous session's log too, so a problem can still be reported after a restart.
- The catalog window comes back to the front when you close the last player window.
```

```text
Версия 26.0730.1512

- Сообщить о проблеме без поиска файлов: «Настройки», «О программе», «Отправить журналы автору» - приложение упакует журналы диагностики и откроет вашу почтовую программу с готовым письмом. Автоматически ничего не отправляется: архив вы прикрепите и нажмёте «Отправить» сами.
- Журнал предыдущего сеанса теперь сохраняется, поэтому о проблеме можно сообщить и после перезапуска.
- Окно каталога возвращается на передний план, когда вы закрываете последнее окно плеера.
```

```text
Версія 26.0730.1512

- Повідомити про проблему без пошуку файлів: «Налаштування», «Про програму», «Надіслати журнали авторові» - програма запакує журнали діагностики й відкриє вашу поштову програму з готовим листом. Автоматично нічого не надсилається: архів ви прикріпите й натиснете «Надіслати» самі.
- Журнал попереднього сеансу тепер зберігається, тому про проблему можна повідомити й після перезапуску.
- Вікно каталогу повертається на передній план, коли ви закриваєте останнє вікно програвача.
```

### Prepared for 26.0728.1352 (shipped)

Paste these as-is at the next Partner Center submission; the ten other languages get the English
block. Kept here rather than substituted into the template above, because the template outlives the
release. The same five bullets ship in the GitHub Release body and in the three winget locale
manifests, so any edit here has to be made there too or the surfaces disagree.

```text
Version 26.0728.1352

- The broadcast-language filter lists your interface language first, together with its regional variants.
- The header shows only the view mode you can switch to.
- Hidden channels moved into Settings, on the Playlists (M3U) tab.
- The player's camera button saves the current frame as a JPEG into a folder you choose, or Downloads.
- Local settings survive a value written by a newer build instead of resetting the catalog.
```

```text
Версия 26.0728.1352

- Фильтр языков вещания ставит язык интерфейса первым, вместе с региональными вариантами.
- В шапке показана только та кнопка режима, в который можно переключиться.
- Спрятанные каналы переехали в настройки, на вкладку «Плейлисты (M3U)».
- Кнопка с фотоаппаратом в плеере сохраняет текущий кадр в JPEG в выбранную папку или в «Загрузки».
- Локальные настройки переживают значение, записанное более новой сборкой, без сброса каталога.
```

```text
Версія 26.0728.1352

- Фільтр мов мовлення ставить мову інтерфейсу першою, разом з регіональними варіантами.
- У шапці показана лише та кнопка режиму, у який можна перемкнутися.
- Приховані канали переїхали в налаштування, на вкладку «Плейлисти (M3U)».
- Кнопка з фотоапаратом у програвачі зберігає поточний кадр у JPEG у вибрану теку або в «Завантаження».
- Локальні налаштування переживають значення, записане новішою збіркою, без скидання каталогу.
```

## Additional system requirements

Enter these as separate items without manual bullets:

```text
Windows 10 version 1809 or later, x64
Internet connection for catalog refresh, playback, and live thumbnails
Playback support varies by stream protocol, provider availability, and codec
```

## Certification notes

```text
STREAMS Player does not require an account, payment, activation, or test credentials.

Suggested test path:
1. Start the app and choose Import channels from the internet.
2. Search or filter the catalog and play an audio entry.
3. Switch to Grid mode to observe cached/live thumbnails for visible HTTP(S) video entries.
4. Open Settings to change tile size and disable or re-enable automatic thumbnails.
5. Open a video entry and exercise always-on-top, Fullscreen, F11, and Escape.
6. Open the language picker in the toolbar and switch the interface; pick Arabic or Urdu to see the
   whole layout mirror right-to-left, then switch back.

Individual third-party streams can be offline or use formats unsupported by the current media backend. This is reported in the player and does not prevent catalog browsing or another stream from being selected.
```

## runFullTrust justification

```text
StreamsPlayer is a full-trust .NET WPF desktop application packaged as MSIX. runFullTrust is required to launch the desktop executable and use its Windows and LibVLC-based media and thumbnail components. Network requests occur only for an explicit catalog refresh, selected stream playback, or enabled Grid thumbnail updates. The app has no account, advertising, analytics, telemetry, or personal-data collection. Source code: https://github.com/SerZhyAle/StreamsPlayer
```

## Privacy and age-rating declarations

- Declare the network capability and answer Partner Center privacy questions from actual package behavior. Provide the privacy URL even if Partner Center considers it optional.
- Complete the IARC questionnaire accurately for an app that can open third-party live audio/video URLs. Do not copy a rating from this document.
- The app does not provide user accounts, chat, purchases, advertising, location, or user-to-user content publishing.

## Screenshot set

At least one desktop screenshot is required per listing language, and a language without one stays
Incomplete with nothing said about it. `tools/store/capture-store-screenshots.ps1` produces exactly
that set. Beyond it, prepare a few composed cards or extra captures without unrelated windows or
unsupported claims:

1. Catalog in List mode.
2. Grid mode with representative thumbnails.
3. Compact Settings window showing tile size, thumbnail preference, version, and links.
4. Video player showing always-on-top and fullscreen controls.
5. Optional Add stream dialog and filtering example.

Before submission, verify every screenshot matches the uploaded version and that any visible
third-party channel artwork is appropriate for the selected markets.

## Maintainer references

- Store listing fields and limits: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info`
- Store certification policies: `https://learn.microsoft.com/en-us/windows/apps/publish/store-policies`
- MSIX categories: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/categories-and-subcategories`
- Privacy and support declarations: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info`
