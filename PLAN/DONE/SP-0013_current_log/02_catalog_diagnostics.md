# Phase 02: Catalog lifecycle diagnostics

**Produces:** concise catalog load/refresh diagnostic events and documented local-data behaviour.

**Consumes:** phase 01 logger injection.

1. Update `MainWindow` to log local catalog load success/failure and explicit catalog refresh start/success/failure, using counts only and exception details on failures. Static check: no stream URL or catalog row is passed to the logger.
2. Update the English/Russian/Ukrainian README local-storage statement and the privacy site text to disclose the local `Current.log` and its diagnostic purpose. Static check: each localized README and site localization text mention the file.
