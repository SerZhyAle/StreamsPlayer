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
1. Start the app and choose Update catalog.
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
