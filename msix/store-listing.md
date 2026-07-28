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

### Prepared for 26.0728.1352

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
