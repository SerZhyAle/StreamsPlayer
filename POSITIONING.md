# Product positioning - the source

Every external surface of STREAMS Player is written from this file: the README and its two translations, the product
site and the Store listing. No surface is written from another surface; where two disagree, this file decides which
one is wrong. The rule is `SITE-REPRESENTATION` rules 1 and 2, and the surfaces it binds are in
[DOCS_SURFACES.md](DOCS_SURFACES.md).

## 1. What the product is

STREAMS Player is an independent Windows desktop player for internet radio, live video and RTSP streams: a curated
catalog to start from, your own streams beside it, and nothing running that you did not ask for.

## 2. The pillars

What the product is for, in the order every surface lists it. A surface too short for all three names the first ones.
The terms are the words a surface uses in each language; the gate reads this table.

| # | Pillar | What it covers | en | ru | uk |
| --- | --- | --- | --- | --- | --- |
| 1 | Internet radio | Radio stations, played in the main window | Internet radio | Интернет-радио | Інтернет-радіо |
| 2 | Live video | Live video streams, opened in the player window | Live video | Live-видео | Live-відео |
| 3 | RTSP | Network camera and RTSP stream addresses, opened in the player window | RTSP | RTSP | RTSP |

A term that is the same in all three languages (RTSP) is not translated, and the site checks it verbatim in every one
of its thirteen locales. The ten machine-translated site locales are held to the pillar count and to those invariant
terms only; their wording is their own.

## 3. Where it is checked

`tools/site/site-facts.json` names the surfaces and `tools/site/SiteFacts.ps1` reads this table:

- the README in English, Russian and Ukrainian, the Store listing in the same three languages, and the site's lead
  sentence (`what-p1`) in the same three first name all three pillars, in this order;
- the site's key line (`hero-kicker`) in all thirteen locales lists exactly three items, in this order.

`pwsh -NoProfile -File tools/site/build-site.ps1 -Check` runs it, and so does `scripts/check.ps1`.

## 4. Changing it

Change this file first. A surface whose wording turns out to be wrong is fixed here before it is fixed there, or the
drift starts again. A new pillar is a new row, and every surface above is then rewritten to list it in its place.
