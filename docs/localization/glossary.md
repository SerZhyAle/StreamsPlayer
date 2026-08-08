# StreamsPlayer localization glossary

Maintainer material, English-only headings, not localized (SP-0034 non-goal 2).

This file fixes how the product's recurring terms are rendered in each shipped language, so one
concept is not named three ways in one window.

It exists because of a structural trade-off. The sibling product CyrFlip needs no glossary: all
thirteen translations of a string are arguments of a single call, so whoever writes them sees the
neighbours on one screen and term drift is visible immediately. StreamsPlayer keeps one keyed resource
dictionary per language, which is the right shape for a WPF app with 300 strings but loses that free
consistency check. The glossary buys it back by hand.

**Use these renderings.** When a string in `src/StreamsPlayer.App/Localization.<code>.xaml` needs one
of these concepts, it uses the form in this table, not a synonym. Adding a term here is cheaper than
reconciling a window that calls the same thing by two names.

## Core terms

| Term | Meaning in this product |
|---|---|
| **stream** | One playable broadcast - internet radio, live video or RTSP. The unit the app plays. |
| **catalog** | The published stream bank the app downloads on an explicit refresh. Not the user's own list. |
| **channel** | One row in the user's list. May come from the catalog, be added manually, or be imported. |
| **refresh** | The explicit, user-initiated download that merges the catalog into the list. Never automatic. |
| **pinned** | A channel the user raised to the band above the main list. |
| **collection** | A user-named group of channels. |
| **preview** | The still frame shown on a grid tile. |
| **colour theme** | The light/dark palette of the interface itself. Never the stream, the tile or the site. |

## Renderings

The `colour theme` column is the shipped `ThemeLabel` string; keep it in step with the resource
dictionaries rather than retranslating it here.

| Language | stream | catalog | channel | refresh | pinned | collection | preview | colour theme |
|---|---|---|---|---|---|---|---|---|
| English (`en`) | stream | catalog | channel | update | pinned | collection | preview | Colour theme |
| Russian (`ru`) | трансляция | каталог | канал | обновить | закреплённый | подборка | превью | Цветовая тема |
| Ukrainian (`uk`) | потік | каталог | канал | оновити | закріплений | добірка | прев’ю | Колірна тема |
| German (`de`) | Stream | Katalog | Kanal | aktualisieren | angeheftet | Sammlung | Vorschau | Farbdesign |
| Italian (`it`) | flusso | catalogo | canale | aggiorna | fissato | raccolta | anteprima | Tema colore |
| Spanish (`es`) | transmisión | catálogo | canal | actualizar | fijado | colección | vista previa | Tema de color |
| French (`fr`) | flux | catalogue | chaîne | actualiser | épinglé | collection | aperçu | Thème de couleur |
| Portuguese (`pt`) | transmissão | catálogo | canal | atualizar | fixado | coleção | pré-visualização | Tema de cor |
| Chinese (`zh`) | 流 | 目录 | 频道 | 更新 | 已置顶 | 收藏集 | 预览 | 颜色主题 |
| Hindi (`hi`) | स्ट्रीम | सूची | चैनल | अद्यतन करें | पिन किया गया | संग्रह | पूर्वावलोकन | रंग थीम |
| Bengali (`bn`) | স্ট্রিম | তালিকা | চ্যানেল | হালনাগাদ | পিন করা | সংগ্রহ | প্রিভিউ | রঙের থিম |
| Arabic (`ar`) | بث | كتالوج | قناة | تحديث | مثبَّت | مجموعة | معاينة | سمة الألوان |
| Urdu (`ur`) | نشریات | فہرست | چینل | تازہ کاری | پن شدہ | مجموعہ | پیش منظر | رنگ تھیم |

## Terms left untranslated on purpose

| Term | Reason |
|---|---|
| `RTSP`, `M3U`, `URL`, `HTTP`, `HTTPS` | Protocol and format names. Written in Latin script in every shipped language. |
| `STREAMS Player` | The product name in English, German, Italian, Spanish, French, Portuguese, Chinese, Hindi, Bengali, Arabic and Urdu. Russian and Ukrainian ship the established localized names `Трансляции` and `Трансляції`. |
| Catalog facet values | Category, language and country values come from the published bank as-is and stay in their source language (non-goal 1). A Hindi reader gets Hindi buttons and English facet values; that is the deliberate boundary. |

## Notes on individual choices

- **catalog** in Hindi, Bengali and Urdu is rendered as "list" rather than as a transliteration of
  "catalog", because the transliteration reads as a printed sales catalogue in all three.
- **channel** and **stream** must stay distinguishable. Where a language would naturally use one word
  for both, the stream term takes the broadcast sense and the channel term the list-row sense.
- **refresh** is a verb in the UI, never a noun, so the imperative form is listed. Since SP-0059 it is
  no longer the *name* of the command that fetches the bank - that command is "Import channels from
  the internet" (`UpdateCatalogPlain`), which names its source and its effect. The rendering below is
  still the verb to use wherever a string genuinely means "update something"; do not reconstruct the
  old button label from it.
- **preview** in Arabic and Urdu uses the "viewing/inspection" sense rather than a transliteration.
- **stream** in Ukrainian is "потік", not "трансляція", because `Трансляції` is the localized product
  name. Rendering the unit of playback the same way would collide with the product name in the same
  window - "Видалити завантажені трансляції" reads as deleting the application, not its streams.
