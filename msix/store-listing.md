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

### Prepared for 26.0821.1208

**Stamped and released on GitHub on 2026-08-21, and submitted to winget the same day** as
[#422124](https://github.com/microsoft/winget-pkgs/pull/422124).

**Not submitted to the Store, by the owner's decision on 2026-08-21, and that decision is closed.**
The Store keeps 26.0806.2225. This is not a step left pending for a later run to pick up: with the
Store outside the broken set (see below), what 26.0821.1208 would carry to a Store user is a
minor-changes update, and a Partner Center submission costs a certification cycle for it. The next
Store submission is whichever version has something a Store user actually needs - it will inherit the
accumulated What's new block below, which is why that block is written and kept current even though
this version is not going out. Do not open this as an outstanding task.

Ships SP-0092 (the Inno Setup installer as a fourth delivery channel, per-user and elevation-free) and
SP-0093 (the WPF runtime pinned back from 10.0.11, which broke `MediaElement` network audio outright).

**SP-0093 is why this release matters more than its feature list - but only on two of the four
channels.** 26.0820.1828 shipped runtime 10.0.11 and plays no station at all: the failure is total,
not intermittent. GitHub serves the fix now, and the winget submission above closes the channel that
was actually resolving to the broken build. **The Store was never in that set.** It sits on
26.0806.2225, which predates 10.0.11 entirely, so a Store user's application has been playing
normally throughout - old, not broken. That is what settled the Partner Center question, and it is
worth stating explicitly, because "republish everything that is broken" reads as including the Store
until you check which runtime each channel actually carries. Checking first is what turned a
certification cycle into a decision that took one command.

The Store package exists but is **not queued for upload** - it was built to run checklist step 5, and
it stays on disk as the evidence for it, not as a submission waiting for a click:
`msix/dist/StreamsPlayer-26.0821.1208-windows-x64.msix`, 138,619,159 bytes, `NotSigned` as Partner
Center requires, sha256 `EA86D8B975BEAD9CCF6A50035A791D792A0981AB422D7B9039D9D32BEA9BFCED`. A later
Store submission should rebuild from its own tag rather than reach for this file. Step 5 was done for
real rather than skipped: the self-signed twin was installed with `Add-AppxPackage`, launched from its
`WindowsApps` location and reached `AUDIO LIVE` on a live station, which is what proves the pin
survives MSIX packaging and not only the plain publish. The test package and its machine-wide
certificate were then removed.

**Built from a clean `git worktree` at the tag, and the first attempt was thrown away.** Unlike the zip
and the installer, which `release.yml` builds from a fresh checkout of `v26.0821.1208`, an MSIX built
by `msix/build-msix.ps1` publishes **the working tree as it stands right now**. Unreleased work was in
progress in this one - SP-0094, timestamps three minutes before the pack - so the first package carried
1,502 bytes of UI that is in no release, under a version number claiming to be 26.0821.1208. It was
deleted rather than kept "for reference", because the one thing worse than no prepared package is a
prepared package nobody can tell is wrong. **Build a Store package from
`git worktree add ../<dir> v<version>` unless the tree is provably clean at the tag**; `git status`
before the pack is the whole check, and it costs one command.

New in the release assets: `StreamsPlayer-26.0821.1208-windows-x64-setup.exe` and its `.sha256`
sidecar, beside the portable zip. The zip's name and shape are unchanged, which is what keeps the
existing winget manifest template valid.

Measured against the 1,500-character limit: 817 en, 805 ru, 798 uk. No colon-plus-space anywhere, so
one text survives the winget YAML scalar unchanged. The same three blocks live as
`msix/listing/release-notes/26.0821.1208.{en-us,ru,uk}.txt`, which is what
`tools/store/write-release-notes.ps1` reads - these fences are a copy of those files, not a second
source.

Deliberately not bulleted: the installer's internals, the runtime pin's mechanics, and the fourth tile
on the site. A user reads three sentences about what changed for them, not a build log.


```text
Version 26.0821.1208

- Radio plays again. In 26.0820.1828 every station failed the moment it was opened, on every machine - the fault was in the Windows runtime the application shipped with, not in the station or your connection. If that version played nothing for you, this one fixes it and nothing needs to be reset.
- There is now an installer. Download one file, run it, and the application installs for your account without asking for administrator rights, adds itself to the Start menu and to the list of installed applications, and uninstalls from there. The portable ZIP stays exactly as it was for anyone who prefers to unpack a folder.
- Removing the application leaves your own data alone - pinned channels, collections, history and settings survive an uninstall, and are still there if you install again.
```

```text
Версия 26.0821.1208

- Радио снова играет. В 26.0820.1828 любая станция обрывалась сразу при открытии, на любой машине - причина была в системном рантайме Windows, с которым поставлялось приложение, а не в станции и не в вашем соединении. Если та версия не играла ничего, эта чинит это, и ничего сбрасывать не нужно.
- Появился установщик. Скачайте один файл, запустите, и приложение установится для вашей учётной записи, не спрашивая прав администратора, добавится в меню «Пуск» и в список установленных программ и оттуда же удаляется. Переносимый ZIP остался ровно таким, каким был, для тех, кто предпочитает распаковать папку.
- Удаление приложения не трогает ваши данные - закреплённые каналы, подборки, история и настройки переживают удаление и остаются на месте, если вы установите приложение снова.
```

```text
Версія 26.0821.1208

- Радіо знову грає. У 26.0820.1828 будь-яка станція обривалася одразу при відкритті, на будь-якій машині - причина була в системному рантаймі Windows, з яким постачалася програма, а не в станції і не у вашому з'єднанні. Якщо та версія не грала нічого, ця це виправляє, і нічого скидати не потрібно.
- З'явився інсталятор. Завантажте один файл, запустіть, і програма встановиться для вашого облікового запису, не питаючи прав адміністратора, додасться в меню «Пуск» і до списку встановлених програм і звідти ж видаляється. Переносний ZIP лишився рівно таким, яким був, для тих, хто воліє розпакувати теку.
- Видалення програми не чіпає ваші дані - закріплені канали, добірки, історія та налаштування переживають видалення і лишаються на місці, якщо ви встановите програму знову.
```

#### Store-only What's new for 26.0821.1208

Not a second source for the release notes above - a different text for a different audience. The Store
sits on 26.0806.2225, so its user has never seen 26.0809.0022, 26.0819.0156 or 26.0820.1828. These
blocks cover all four releases and are what Partner Center gets. The ten machine-translated languages
get the English text.

**Identical to the 26.0820.1828 block except the version line, and that is correct, not lazy.** Neither
item this version actually adds is a Store item. The audio fix repairs a runtime that 26.0806.2225
never carried, so the Store user's application was never broken and has nothing to be told it is fixed
from; and the installer is a different delivery channel, which a Store listing has no business
advertising. Measured: 1454 en, 1454 ru, 1419 uk against a 1,500-character limit, so there was no room
for a fifth item either way.

```text
Version 26.0821.1208

- Channels the catalog gives no logo now carry a mark of their own - initials from the name, a colour drawn from that same name, and the country code where it is known.
- A channel the catalog stops publishing keeps your pins, your collections and your history instead of vanishing, and returns on its own if the catalog lists it again.
- The catalog can shrink to a compact radio panel while a station plays - always on top, carrying the station, the current track, the volume, the transport, the sleep timer and Random station.
- Play a random station. One press picks from the whole catalog, ignoring the search and the facets, and skips hidden stations, video and RTSP. One that stays silent gives way to the next draw.
- A built-in channel list. A copy of the stream bank ships inside the app, so the catalog fills on a first launch with no network. It adds and updates, never removes, and says how old it is.
- A topic filter, sharing a channel as one line of text, playback that resumes where the last session left off, and what is on air under the channel name.
- The player says why the picture stopped, a stream that stalls while pretending to play is caught and re-opened, and video quality follows the connection and is remembered per channel.
- Fixed - a very long station name could take the desktop shortcut down with it, dark-theme glyph buttons were unreadable, and a radio stream that ended kept the machine awake.
```

```text
Версия 26.0821.1208

- Каналы, которым каталог не дал логотипа, теперь несут собственный знак - инициалы из названия, цвет, выведенный из того же названия, и код страны, если он известен.
- Канал, который каталог перестал публиковать, сохраняет ваши закрепления, подборки и историю вместо того, чтобы исчезнуть, и возвращается сам, если каталог снова его перечислит.
- Каталог умеет ужаться до компактной радиопанели, пока играет станция - поверх других окон, со станцией, текущим треком, громкостью, управлением, таймером сна и случайной станцией.
- Включить случайную станцию. Одно нажатие выбирает из всего каталога, не глядя на поиск и фасеты, и пропускает скрытые станции, видео и RTSP. Молчащая уступает место следующей.
- Встроенный список каналов. Копия банка потоков лежит внутри программы, поэтому каталог заполняется при первом запуске без сети. Он добавляет и обновляет, ничего не удаляет и называет свой возраст.
- Фильтр по темам, отправка канала одной строкой текста, продолжение воспроизведения с места прошлого сеанса и трек, который станция играет прямо сейчас, под названием канала.
- Плеер объясняет, почему картинка остановилась, поток, который делает вид, что играет, переоткрывается, а качество видео следует за соединением и запоминается для канала.
- Исправлено - очень длинное название станции могло уронить программу при выносе ярлыка, кнопки-глифы в тёмной теме были нечитаемы, а завершившееся радио держало компьютер без сна.
```

```text
Версія 26.0821.1208

- Канали, яким каталог не дав логотипа, тепер несуть власний знак - ініціали з назви, колір, виведений із тієї самої назви, і код країни, якщо він відомий.
- Канал, який каталог перестав публікувати, зберігає ваші закріплення, добірки та історію замість того, щоб зникнути, і повертається сам, якщо каталог знову його перелічить.
- Каталог уміє стиснутися до компактної радіопанелі, поки грає станція - поверх інших вікон, зі станцією, поточним треком, гучністю, керуванням, таймером сну та випадковою станцією.
- Увімкнути випадкову станцію. Одне натискання обирає з усього каталогу, не зважаючи на пошук і фасети, і пропускає приховані станції, відео та RTSP. Мовчазна поступається наступній.
- Вбудований список каналів. Копія банку потоків лежить усередині програми, тож каталог заповнюється при першому запуску без мережі. Він додає та оновлює, нічого не видаляє і називає свій вік.
- Фільтр за темами, надсилання каналу одним рядком тексту, продовження відтворення з місця минулого сеансу і трек, який станція грає просто зараз, під назвою каналу.
- Програвач пояснює, чому картинка зупинилася, потік, який вдає, що грає, перевідкривається, а якість відео йде за з'єднанням і запам'ятовується для каналу.
- Виправлено - дуже довга назва станції могла впустити програму під час винесення ярлика, кнопки-гліфи в темній темі були нечитабельні, а радіо, що завершилося, тримало комп'ютер без сну.
```

### Prepared for 26.0820.1828

**Stamped and released on GitHub on 2026-08-20.** The winget submission and the Partner Center upload
are separate manual steps and were **not** done in the run that cut this version - see the release
checklist, steps 8 and 9.

Landed: tag `v26.0820.1828` on commit `dbb4296`, workflow run 32394456616 green in 2m24s, Release
published 16:55:02Z with `StreamsPlayer-26.0820.1828-windows-x64.zip` (140 086 557 bytes) and its
`.sha256`. The asset's SHA256, which step 8 needs for `InstallerSha256`, is

    8A149D7FD56D93F31C4795E43DA0AF06E59ACCC80EFF82350C1F44C5EDE73A27

verified after download against the published `.sha256` file, and the packaged
`StreamsPlayer.exe` reports FileVersion `26.0820.1828` and ProductVersion
`26.0820.1828+dbb42963d8a7bd5ea322cd850f781657ed1467f2`, so binary, asset name and tag agree.
`THIRD-PARTY-NOTICES.txt`, `LICENSE` and `README.md` are inside the ZIP.

winget followed on the same day as
[#421532](https://github.com/microsoft/winget-pkgs/pull/421532) - see `winget/README.md` for the
submission record and for the one checklist box that went out unticked.

**The Store package is built and waiting for a Partner Center upload.**
`msix/dist/StreamsPlayer-26.0820.1828-windows-x64.msix`, 138 614 500 bytes, sha256
`062E7AD1E76F5888907E52E1F8BF0D3497CECE550F8959929211FEC461623381`. Built by passing all three
identity values explicitly rather than relying on the script's defaults, then read back out of the
packed `AppxManifest.xml` rather than trusted from the command line - `Name` `SZA.StreamsPlayer`,
`Publisher` `CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD`, `PublisherDisplayName` `SZA`, all three
unchanged, which is what keeps every installed copy upgradeable. `Version` is `26.820.1828.0`: the
`YY.MMDD.HHmm` stamp int-cast per component, because the Identity Version schema forbids leading
zeros, with revision 0 and every part under 65535. It is greater than the `26.806.2225.0` the Store
currently carries. The package is **unsigned** and must stay that way - Microsoft re-signs at
certification, and `-SelfSign` produces a package that must never be uploaded. `THIRD-PARTY-NOTICES.txt`
is in the payload and `TargetDeviceFamily` MinVersion is `10.0.17763.0`, matching the winget floor.

Still to do at the console, in this order: build-store-listing-csv and write-release-notes against a
**fresh** Partner Center export, upload the MSIX, then submit. The Store-only What's new below is the
text that goes in.

**The Store is still on 26.0806.2225.** Two GitHub releases have gone past it - 26.0809.0022 and
26.0819.0156 - so a Store user moving to this version receives all three at once. That is why the
Store-only blocks below exist and are longer than the release notes; they are what Partner Center
gets, and GitHub and winget keep the three blocks above them.

Screenshots under `assets/store/` still date from 2026-07-27, so they predate the dark theme, the
consolidated header, the compact panel, the monogram placeholder and everything in this release. Not
a blocker, still the obvious next thing to regenerate.

Ships SP-0087 (the monogram a channel without a favicon draws from its own name), and the three rules
of the 2026-08-20 catalog contract amendment: SP-0088 (a build's `favicon_index` values resolve only
against the atlas that arrived in the same ZIP), SP-0089 (a row the bank stops listing is retired
rather than deleted whenever it carries anything the user made) and SP-0091 (artwork read through
`artwork-manifest.json` and the stable asset names instead of a pinned, frozen revision). Also the
catalog-snapshot offer as a modal question rather than an inline bar, and the regenerated bundled
snapshot.

Deliberately not bulleted: the memory notes and the contract text in `CLAUDE.md`, which are
maintainer-facing, and the refreshed snapshot's channel count, which is mentioned once as a date
rather than sold as a feature.

Measured against the 1,500-character limit: 1290 en, 1278 ru, 1274 uk. Written with no
colon-plus-space anywhere, which is what lets one text survive the winget YAML scalar unchanged.
The same three blocks live as `msix/listing/release-notes/26.0820.1828.{en-us,ru,uk}.txt`, which is
what `tools/store/write-release-notes.ps1` reads - these fences are a copy of those files, not a
second source.

```text
Version 26.0820.1828

- A channel the catalog gives no logo now carries a mark of its own - initials from its name, a colour drawn from that same name so it always looks the same, and its country code where the catalog knows one. Most of the channel list has no logo, and an empty square read as something that failed to load.
- A channel the catalog stops publishing no longer takes your work with it. If you pinned it, put it in a collection, played it or gave it an icon, the channel is kept and marked retired - it leaves the general list and the random station draw, stays in the pinned strip and in its collections, and comes back on its own if the catalog lists it again.
- The picture pack is checked against the publisher's own manifest before a single picture is used, so a pack rebuilt while it was being fetched can no longer put another station's picture on a channel. It also always reads the current pack instead of one frozen build.
- An update that arrives without a usable icon sheet now shows marks rather than the wrong logos, and the next update puts the real icons back.
- The built-in channel list is offered as a plain question you answer once, instead of a bar across the catalog.
- The built-in list itself was refreshed on 2026-08-20 and carries 18 010 channels.
```

```text
Версия 26.0820.1828

- Канал, которому каталог не дал логотипа, теперь несёт собственный знак - инициалы из названия, цвет, выведенный из того же названия, поэтому знак всегда одинаков, и код страны, если каталог его знает. Логотипа нет у большей части списка, а пустой квадрат читался как что-то, что не загрузилось.
- Канал, который каталог перестал публиковать, больше не уносит вашу работу с собой. Если вы его закрепили, положили в подборку, слушали или дали ему иконку, канал остаётся и помечается выведенным - он уходит из общего списка и из выбора случайной станции, остаётся в полосе закреплённых и в своих подборках и возвращается сам, если каталог снова его перечислит.
- Пакет картинок сверяется с манифестом издателя до того, как использована хотя бы одна картинка, поэтому пакет, пересобранный во время загрузки, больше не поставит на канал кадр чужой станции. И читается всегда текущий пакет, а не одна замороженная сборка.
- Обновление, пришедшее без пригодного листа иконок, теперь показывает знаки, а не чужие логотипы, и следующее обновление вернёт настоящие иконки.
- Встроенный список каналов предлагается обычным вопросом, на который вы отвечаете один раз, а не полосой поперёк каталога.
- Сам встроенный список обновлён 2026-08-20 и несёт 18 010 каналов.
```

```text
Версія 26.0820.1828

- Канал, якому каталог не дав логотипа, тепер несе власний знак - ініціали з назви, колір, виведений із тієї самої назви, тож знак завжди однаковий, і код країни, якщо каталог його знає. Логотипа немає у більшої частини списку, а порожній квадрат читався як щось, що не завантажилося.
- Канал, який каталог перестав публікувати, більше не забирає вашу роботу з собою. Якщо ви його закріпили, поклали в добірку, слухали або дали йому піктограму, канал лишається і позначається виведеним - він іде із загального списку та з вибору випадкової станції, лишається в смузі закріплених і у своїх добірках і повертається сам, якщо каталог знову його перелічить.
- Пакет картинок звіряється з маніфестом видавця до того, як використано бодай одну картинку, тож пакет, перезібраний під час завантаження, більше не поставить на канал кадр чужої станції. І читається завжди поточний пакет, а не одна заморожена збірка.
- Оновлення, що прийшло без придатного аркуша піктограм, тепер показує знаки, а не чужі логотипи, і наступне оновлення поверне справжні піктограми.
- Вбудований список каналів пропонується звичайним запитанням, на яке ви відповідаєте один раз, а не смугою впоперек каталогу.
- Сам вбудований список оновлено 2026-08-20 і він несе 18 010 каналів.
```

#### Store-only What's new for 26.0820.1828

Not a second source for the release notes above - a different text for a different audience. The Store
sits on 26.0806.2225, so its user has never seen 26.0809.0022 or 26.0819.0156. These blocks cover all
three releases and are what Partner Center gets. The ten machine-translated languages get the English
text.

```text
Version 26.0820.1828

- Channels the catalog gives no logo now carry a mark of their own - initials from the name, a colour drawn from that same name, and the country code where it is known.
- A channel the catalog stops publishing keeps your pins, your collections and your history instead of vanishing, and returns on its own if the catalog lists it again.
- The catalog can shrink to a compact radio panel while a station plays - always on top, carrying the station, the current track, the volume, the transport, the sleep timer and Random station.
- Play a random station. One press picks from the whole catalog, ignoring the search and the facets, and skips hidden stations, video and RTSP. One that stays silent gives way to the next draw.
- A built-in channel list. A copy of the stream bank ships inside the app, so the catalog fills on a first launch with no network. It adds and updates, never removes, and says how old it is.
- A topic filter, sharing a channel as one line of text, playback that resumes where the last session left off, and what is on air under the channel name.
- The player says why the picture stopped, a stream that stalls while pretending to play is caught and re-opened, and video quality follows the connection and is remembered per channel.
- Fixed - a very long station name could take the desktop shortcut down with it, dark-theme glyph buttons were unreadable, and a radio stream that ended kept the machine awake.
```

```text
Версия 26.0820.1828

- Каналы, которым каталог не дал логотипа, теперь несут собственный знак - инициалы из названия, цвет, выведенный из того же названия, и код страны, если он известен.
- Канал, который каталог перестал публиковать, сохраняет ваши закрепления, подборки и историю вместо того, чтобы исчезнуть, и возвращается сам, если каталог снова его перечислит.
- Каталог умеет ужаться до компактной радиопанели, пока играет станция - поверх других окон, со станцией, текущим треком, громкостью, управлением, таймером сна и случайной станцией.
- Включить случайную станцию. Одно нажатие выбирает из всего каталога, не глядя на поиск и фасеты, и пропускает скрытые станции, видео и RTSP. Молчащая уступает место следующей.
- Встроенный список каналов. Копия банка потоков лежит внутри программы, поэтому каталог заполняется при первом запуске без сети. Он добавляет и обновляет, ничего не удаляет и называет свой возраст.
- Фильтр по темам, отправка канала одной строкой текста, продолжение воспроизведения с места прошлого сеанса и трек, который станция играет прямо сейчас, под названием канала.
- Плеер объясняет, почему картинка остановилась, поток, который делает вид, что играет, переоткрывается, а качество видео следует за соединением и запоминается для канала.
- Исправлено - очень длинное название станции могло уронить программу при выносе ярлыка, кнопки-глифы в тёмной теме были нечитаемы, а завершившееся радио держало компьютер без сна.
```

```text
Версія 26.0820.1828

- Канали, яким каталог не дав логотипа, тепер несуть власний знак - ініціали з назви, колір, виведений із тієї самої назви, і код країни, якщо він відомий.
- Канал, який каталог перестав публікувати, зберігає ваші закріплення, добірки та історію замість того, щоб зникнути, і повертається сам, якщо каталог знову його перелічить.
- Каталог уміє стиснутися до компактної радіопанелі, поки грає станція - поверх інших вікон, зі станцією, поточним треком, гучністю, керуванням, таймером сну та випадковою станцією.
- Увімкнути випадкову станцію. Одне натискання обирає з усього каталогу, не зважаючи на пошук і фасети, і пропускає приховані станції, відео та RTSP. Мовчазна поступається наступній.
- Вбудований список каналів. Копія банку потоків лежить усередині програми, тож каталог заповнюється при першому запуску без мережі. Він додає та оновлює, нічого не видаляє і називає свій вік.
- Фільтр за темами, надсилання каналу одним рядком тексту, продовження відтворення з місця минулого сеансу і трек, який станція грає просто зараз, під назвою каналу.
- Програвач пояснює, чому картинка зупинилася, потік, який вдає, що грає, перевідкривається, а якість відео йде за з'єднанням і запам'ятовується для каналу.
- Виправлено - дуже довга назва станції могла впустити програму під час винесення ярлика, кнопки-гліфи в темній темі були нечитабельні, а радіо, що завершилося, тримало комп'ютер без сну.
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
