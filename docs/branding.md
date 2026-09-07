# DLSS 5 Launcher identity

The angular 5, chamfered tile and forward-slanted lettering form one visual identity.
The wordmark is original vector geometry; LAUNCHER uses outlined Inter SemiBold
(SIL OFL, see `src/Assets/Fonts/Inter-LICENSE.txt`). It does not require an installed font.

## Assets

- `icon.svg`: scalable symbol with transparent corners.
- `logo.svg`: transparent wordmark for dark backgrounds.
- `logo-banner.svg`: wordmark on a dark panel, readable in either GitHub theme.
- `icon.png`: 512 px symbol; `logo.png`: 1095 x 290 px wordmark.
- `brand-preview.png`: WPF-rendered review sheet including native icon sizes.
- `../src/Assets/app.ico`: 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px.
- `../src/Assets/Branding.xaml`: shared WPF vector resources, used by the header.

Use the wordmark on a dark surface. Keep at least one main-letter stroke of space
around it. Do not stretch it or add glow. The header uses a 170 x 45 wordmark and
a 42 px symbol; at smaller sizes use the symbol alone.

Colors: electric lime `#B3F45C`, white `#F0F4F3`, dark green `#101A18`,
secondary lettering `#ABBAB5`.

## Regenerate

On Windows with PowerShell and WPF:

```powershell
powershell -NoProfile -STA -File tools/gen_brand.ps1
dotnet build src/RenoDXLauncher.csproj -c Release
```

Edit the geometry in `gen_brand.ps1`; do not hand-edit generated files.
The older `gen_icon.ps1` and `gen_icon.py` entry points call the same generator.
The installer already consumes `app.ico`; rebuilding it picks up the new identity.
