# Microsoft Store submission copy

Use this file as the offline copy deck for Partner Center. It describes the current application; remove any item that is not present in the package being submitted.

## Submission profile

- Product name: `StreamPlayer`
- Publisher display name: `SerZhyAle`
- Primary category: `Entertainment`
- Optional secondary category: `Music`
- Device family: Windows Desktop, x64
- Minimum OS: Windows 10 version 1809 (`10.0.17763.0`)
- Price: Free
- License: MIT
- Website: `https://serzhyale.github.io/StreamPlayer/`
- Support: `https://github.com/SerZhyAle/StreamPlayer/issues`
- Support email: `serzhyale@gmail.com`
- Privacy policy: `https://serzhyale.github.io/StreamPlayer/privacy.html`
- Source: `https://github.com/SerZhyAle/StreamPlayer`

## English (en-US)

### Short description

```text
Browse internet radio and live video in a focused Windows player with a curated catalog, local favorites, live grid previews, and no account or advertising.
```

### Description

```text
StreamPlayer is an independent Windows desktop application for browsing and playing internet radio, live video, and RTSP stream addresses.

Explore a curated catalog by category, language, country, or media type. Search and sort channels, pin the streams you use most, or add a direct stream URL manually. Audio plays in the main window, while video opens in a dedicated window with always-on-top and borderless fullscreen controls.

Choose a compact list or a visual grid. Grid mode can capture current thumbnails for visible HTTP(S) video streams; tile size and automatic thumbnail updates are controlled in Settings. The complete interface switches between English and Russian.

Catalog downloads happen only when you choose Update catalog. StreamPlayer has no account, advertising, analytics, or telemetry. Catalog state, manual entries, favorites, playback marks, settings, and cached thumbnails stay in your Windows profile.

Playback compatibility depends on the stream provider, network availability, protocol, and media codecs supported by the included or Windows media components. StreamPlayer does not host or control third-party stream content.

StreamPlayer is open source under the MIT License.
```

### Product features

Enter each line as a separate feature; Partner Center adds the bullets.

```text
Curated catalog for internet radio, live video, and RTSP addresses
Search, sort, and filter by category, language, country, and media type
Pin favorite streams and add direct stream URLs
List and visual grid layouts with selectable tile sizes
Optional live thumbnails for visible HTTP(S) video streams
English and Russian interface
Independent always-on-top controls for the app and video player
Borderless video fullscreen with F11 and Escape shortcuts
Explicit catalog refresh with no surprise background catalog downloads
No account, advertising, analytics, or telemetry
```

### Keywords

Use no more than these seven entries:

```text
internet radio
live TV
stream player
RTSP
radio catalog
video streams
IPTV player
```

## Russian (ru-RU)

### Short description

```text
Каталог интернет-радио и live-видео для Windows с избранным, живыми миниатюрами, русским интерфейсом, без аккаунта и рекламы.
```

### Description

```text
StreamPlayer — независимое приложение Windows для просмотра каталога и воспроизведения интернет-радио, live-видео и адресов RTSP-потоков.

Просматривайте подобранный каталог по категории, языку, стране или типу медиа. Ищите и сортируйте каналы, закрепляйте нужные потоки и добавляйте прямые URL вручную. Аудио воспроизводится в главном окне, а видео открывается в отдельном проигрывателе с режимами «поверх всех окон» и безрамочным полноэкранным просмотром.

Выбирайте компактный список или наглядную сетку. В режиме сетки приложение может получать текущие миниатюры видимых HTTP(S)-видеопотоков; размер плиток и автоматическое обновление миниатюр задаются в Настройках. Весь интерфейс переключается между русским и английским языками.

Каталог загружается только после нажатия «Обновить каталог». В StreamPlayer нет аккаунта, рекламы, аналитики и телеметрии. Состояние каталога, добавленные вручную адреса, избранное, отметки воспроизведения, настройки и кэш миниатюр хранятся в профиле Windows.

Совместимость воспроизведения зависит от поставщика потока, сети, протокола и медиакодеков, доступных во встроенных компонентах и Windows. StreamPlayer не размещает и не контролирует содержимое сторонних потоков.

Исходный код StreamPlayer опубликован по лицензии MIT.
```

### Product features

```text
Каталог интернет-радио, live-видео и адресов RTSP
Поиск, сортировка и фильтры по категории, языку, стране и типу медиа
Закрепление любимых потоков и добавление прямых URL
Режимы списка и сетки с выбором размера плиток
Отключаемые живые миниатюры видимых HTTP(S)-видеопотоков
Русский и английский интерфейс
Независимый режим «поверх всех окон» для приложения и видеоплеера
Безрамочный полноэкранный режим с клавишами F11 и Escape
Обновление каталога только по команде пользователя
Без аккаунта, рекламы, аналитики и телеметрии
```

### Keywords

```text
интернет радио
онлайн телевидение
плеер потоков
RTSP
каталог радио
видеопотоки
IPTV плеер
```

## What's new

Leave this field blank for the first submission. For updates, replace the version and keep the text within 1,500 characters.

### English template

```text
Version REPLACE_VERSION

- REPLACE_USER_VISIBLE_CHANGE
- REPLACE_USER_VISIBLE_CHANGE
```

### Russian template

```text
Версия REPLACE_VERSION

- REPLACE_USER_VISIBLE_CHANGE
- REPLACE_USER_VISIBLE_CHANGE
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
StreamPlayer does not require an account, payment, activation, or test credentials.

Suggested test path:
1. Start the app and choose Update catalog.
2. Search or filter the catalog and play an audio entry.
3. Switch to Grid mode to observe cached/live thumbnails for visible HTTP(S) video entries.
4. Open Settings to change tile size and disable or re-enable automatic thumbnails.
5. Open a video entry and exercise always-on-top, Fullscreen, F11, and Escape.
6. Use RU / EN to verify both interface languages.

Individual third-party streams can be offline or use formats unsupported by the current media backend. This is reported in the player and does not prevent catalog browsing or another stream from being selected.
```

## runFullTrust justification

```text
StreamPlayer is a full-trust .NET WPF desktop application packaged as MSIX. runFullTrust is required to launch the desktop executable and use its Windows and LibVLC-based media and thumbnail components. Network requests occur only for an explicit catalog refresh, selected stream playback, or enabled Grid thumbnail updates. The app has no account, advertising, analytics, telemetry, or personal-data collection. Source code: https://github.com/SerZhyAle/StreamPlayer
```

## Privacy and age-rating declarations

- Declare the network capability and answer Partner Center privacy questions from actual package behavior. Provide the privacy URL even if Partner Center considers it optional.
- Complete the IARC questionnaire accurately for an app that can open third-party live audio/video URLs. Do not copy a rating from this document.
- The app does not provide user accounts, chat, purchases, advertising, location, or user-to-user content publishing.

## Screenshot set

At least one desktop screenshot is required; prepare four or more, without unrelated windows or unsupported claims:

1. English catalog in List mode.
2. Russian catalog in Grid mode with representative thumbnails.
3. Compact Settings window showing tile size, thumbnail preference, version, and links.
4. Video player showing always-on-top and fullscreen controls.
5. Optional Add stream dialog and filtering example.

Before submission, verify every screenshot matches the uploaded version and that any visible third-party channel artwork is appropriate for the selected markets.

## Maintainer references

- Store listing fields and limits: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info`
- Store certification policies: `https://learn.microsoft.com/en-us/windows/apps/publish/store-policies`
- MSIX categories: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/categories-and-subcategories`
- Privacy and support declarations: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info`
