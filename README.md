<div align="center">

<img src="docs/icon.png" width="112" alt="">

# DLSS 5 Launcher

**One click puts DLSS 5 Neural Rendering into your games — including DirectX 9 and 32-bit titles no other installer reaches.**

[![Release](https://img.shields.io/github/v/release/xdzleo/dlss5-launcher?style=flat-square)](https://github.com/xdzleo/dlss5-launcher/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square)](#install)

[English](README.md) · [Português](README.pt-BR.md)

</div>

![screenshot](docs/screenshot.png)

DLSS 5 Neural Rendering is NVIDIA's post-process that reconstructs detail the renderer never
drew — most visibly in skin, hair and faces. It reached the public as a leaked runtime, with no
installer and no game that ships it.

Getting it into a game means assembling a chain by hand: ReShade with add-on support, the neural
add-on, two runtimes, a motion-vector provider, shader includes, and — in a game that has no DLSS
of its own — a Feeder that fabricates the DLSS contract from scratch. Miss one piece and every
failure looks identical from the outside: the game opens and nothing happens.

This launcher assembles that chain, verifies every link, and tells you which one is missing when
something is wrong.

## What it covers

| Game | How |
| --- | --- |
| DirectX 12 | the add-on hooks the game's own NGX calls |
| DirectX 11 | same, through a private D3D12 device |
| Vulkan | ReShade goes in as a Vulkan layer |
| **DirectX 9** | translated first — DXVK or dgVoodoo2, chosen per game |
| **32-bit** | 32-bit add-on in the game, 64-bit helper process beside it |
| no DLSS at all | the DLSS contract is fabricated from the frame |
| only FSR/XeSS | those calls are redirected to DLSS |

**The 32-bit path is the hard one, and it is why this project exists.** DLSS is x64-only: a 32-bit
game cannot load it, period. The launcher runs a separate 64-bit helper process alongside the
game, shares textures across the process boundary through NT handles and a fence, and runs the
neural pass there.

**DirectX 9 needs a translator**, because ReShade on D3D9 stops at Shader Model 3 and no
motion-vector provider compiles. Two exist, and neither covers everything — so you pick:

- **DXVK** translates D3D9 to Vulkan. Default, and the reason DX9 works here at all: on Vulkan
  ReShade compiles compute shaders, which is what the motion-vector provider needs.
- **dgVoodoo2** translates D3D9 to D3D11. For the games DXVK drops.

Measured on one machine, same add-on and runtime: *Resident Evil Revelations 2* runs **only** on
DXVK (dgVoodoo crashes before the menu); *Saints Row 2* runs **only** on dgVoodoo (DXVK crashes at
~25 s, after DLSS is already evaluating). The sets do not contain each other, so the choice sits
in the UI, remembered per game.

Making the 32-bit add-on speak Vulkan required extending the Feeder: the official 32-bit build
accepts D3D11 only (`only Direct3D 11 games are supported by the 32-bit add-on`, verbatim in its
source). This launcher ships a build with a Vulkan transport added, in the same design the 64-bit
add-on already used — the host creates the textures on D3D12 and the game imports them with
`VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT`.

## Install

1. Download `DLSS5Launcher-setup.exe` from the [latest release](https://github.com/xdzleo/dlss5-launcher/releases/latest).
2. Run it, pick your game, press the DLSS 5 switch.
3. In game: **Home** opens ReShade, **F6** toggles the neural pass.

Windows may show a SmartScreen prompt — see [docs/antivirus.md](docs/antivirus.md) for why and what
to check.

### GPU support

Neural Rendering needs tensor cores, so any RTX card qualifies; GTX/GT/MX and non-NVIDIA do not,
and the launcher says so up front instead of installing 158 MB that cannot run.

The original model is FP8 with Blackwell-only kernels. For earlier cards the launcher picks the
`.SF` build automatically (patched binaries for RTX 40, an FP16 path for RTX 20/30) — the cost of
the pass is much higher there, and the UI tells you which tier you are on.

## What gets installed

Everything is fetched from the projects that made it, at install time:

| Piece | From |
| --- | --- |
| ReShade with add-on support | [reshade.me](https://reshade.me) |
| `renodx-dlss5.addon64`, `nvngx_dlssnr.dll`, DLSS runtimes | [RankFTW/RHI](https://github.com/RankFTW/RHI) |
| `DLSS5_Feed.fx` + the 64-bit add-on | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) |
| motion vectors (`lumenite_*`) | [umar-afzaal/LumeniteFX](https://github.com/umar-afzaal/LumeniteFX) |
| DXVK | [doitsujin/dxvk](https://github.com/doitsujin/dxvk) |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) |
| OptiScaler | [optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler) |

The only thing bundled in the exe is the pair of 32-bit halves with the Vulkan transport (124 KB),
built from the Feeder's MIT source — they exist in no public release.

## The chain, and why it is shown

Every broken link produces the same symptom from outside: the game opens and nothing happens. So
the launcher shows them individually — ReShade, add-on, neural runtime, Ray Reconstruction, early
load, the switch, and the bridge or Feeder when they are required. Green means present; the
install is ready only when all of them are.

`--check` prints the plan without writing anything:

```
DLSS5Launcher.exe dlss5 "Bully" --check

  arquitetura    : 32 bits
  gpu            : NVIDIA GeForce RTX 5090   (custo do pass: RTX 50)
  DLSS proprio   : nao
  tradutor DX9   : DXVK (Vulkan)
  ReShade entra  : camada Vulkan
  metades 32 bits: com transporte Vulkan
  processo extra : host64 (o DLSS e x64; um jogo de 32 bits nao o carrega)
  (nada foi escrito — isto e so o plano)
```

## Also does

The launcher started as a manager for [RenoDX](https://github.com/clshortfuse/renodx) HDR mods and
still does all of it: per-game mod install and toggle, HDR nits from the UI without opening the
game, DLSS runtime swaps with backups, mod update checks, and a games list built from your stores.

## Command line

```
list                    detected games and their state
dlss5 <game>            install DLSS 5      (--dgvoodoo · --check)
dlss5 --all             every eligible game
verify <game>           read ReShade.log and say whether the mod loaded
settings <game>         mod settings from ReShade.ini
set <game> key=value    write settings (close the game first)
doctor                  environment check
```

## Building

```
git clone https://github.com/xdzleo/dlss5-launcher
cd dlss5-launcher
dotnet build src/RenoDXLauncher.csproj -c Release
```

.NET 10 SDK, Windows. `tools/gen_resx.ps1` regenerates the string resources from
`src/Localization/strings.json`; `tools/gen_icon.ps1` regenerates the icon.

Tests: `dotnet run --project tests/SmokeTest` covers install/remove against a fake game folder.
`tests/ChainProbe` runs the real chain logic against a real game folder and prints link by link —
useful when the switch says "install" on a game that is already working.

## Translating

Strings live in `src/Localization/strings.json`, one entry per key with every language side by
side. Add a language tag there, run `tools/gen_resx.ps1`, add the tag to
`<SatelliteResourceLanguages>` in the csproj.

## Credits

- [clshortfuse](https://github.com/clshortfuse) — RenoDX, and the neural add-on this builds on
- [jlrouzies-fr](https://github.com/jlrouzies-fr) — DLSS5-Feeder, which makes DLSS-less games possible
- [umar-afzaal](https://github.com/umar-afzaal) — LumeniteFX, the motion-vector provider
- [crosire](https://github.com/crosire) — ReShade
- [Dege](https://github.com/dege-diosg) — dgVoodoo2 · [doitsujin](https://github.com/doitsujin) — DXVK
- [RankFTW](https://github.com/RankFTW) — RHI, the runtime index

## License

MIT — see [LICENSE](LICENSE). Covers this launcher's own code; everything it downloads keeps its
own license, and the neural add-on is closed-source and community-distributed.
