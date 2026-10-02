---
name: project_localization
description: 13-language interface, CLDR collation, RTL calendar rules, string formatting safety, and encoding traps
type: project
---

# Localization & Multi-Language Rules

- **Single source of truth for languages (InterfaceLanguages.cs):**
  - Shipped languages (`en ru uk de it es fr pt-br zh-hans hi bn ar ur`) are declared in `InterfaceLanguages.cs`.
  - Endonyms reside in `Localization.en.xaml` (`Language*` keys) and are loaded dynamically by site/store tools from built Core assembly bytes.
- **Safe string formatting & parity gates (SP-0057):**
  - All string formatting routes through `LocalizedFormat.Apply` to safely handle missing or surplus arguments.
  - `LocalizedCallSiteTests` and `LocalizationParityTests` enforce key completeness and placeholder parity across all 13 languages at build time.
- **Dictionary editing & line ending precautions:**
  - Never edit dictionaries using PowerShell 5.1 (CP1251 corruption) or Unix `sed`/`awk` (CRLF stripping). Use `pwsh` 7 or direct file tools.
  - Typographic apostrophe U+2019 in PowerShell scripts acts as a string delimiter; construct dynamically with `[char]0x2019` (e.g. Ukrainian `прев’ю`).
  - Multi-line `<sys:String>` entries in XAML must use `&#x0D;&#x0A;` entities instead of raw newlines.
- **RTL layout & calendar normalization (SP-0034):**
  - `LocalizationService.CreateUiCulture` forces Gregorian calendar on `ar-SA` to prevent unexpected Hijri calendar conversion on desktop UI.
  - WPF sets Win32 `WS_EX_LAYOUTRTL` on RTL windows; `PrintWindow` captures require `RotateNoneFlipX` correction.
- **Terminology rules:**
  - Ukrainian renders `stream` as `потік` (not `трансляція`, which is the product name). Russian uses `поток`.
