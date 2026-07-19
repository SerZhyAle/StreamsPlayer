# Microsoft Store package

`build-msix.ps1` publishes the WPF app self-contained for x64, copies the
required StreamsPlayer tile logos from `assets/msix/` and packs a full-trust MSIX. Generated `stage/`
and `dist/` folders are intentionally ignored by Git.

It requires the Windows SDK tools `makeappx.exe` and (for `-SelfSign`)
`signtool.exe`. Install them with `winget install Microsoft.WindowsSDK`.

Before the first Store submission, reserve **StreamsPlayer** in Partner Center
and copy the three exact values from Product identity. They are permanent for
this product and must be supplied on every update.

```powershell
.\msix\build-msix.ps1 `
  -IdentityName '<Package/Identity/Name>' `
  -Publisher '<Package/Identity/Publisher>' `
  -PublisherDisplayName '<Package/Properties/PublisherDisplayName>' `
  -Version '26.0719.0131.0'
```

The application and release version uses `YY.MMDD.HHmm`; MSIX requires a fourth
numeric component, so the package identity uses `YY.MMDD.HHmm.0`. When
`-Version` is omitted, the script derives that value from the current UTC time.
The three-part value is embedded in the application and shown in Settings.

The Store package must remain unsigned because Microsoft signs it during
certification. For local sideload testing only, use
`./msix/build-msix.ps1 -SelfSign`, trust the generated certificate, then install
the generated package. Never upload a self-signed test package to Partner Center.
