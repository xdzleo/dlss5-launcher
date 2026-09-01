<div align="center">

<img src="docs/icon.png" width="112" alt="">

# DLSS 5 Launcher

### 🧠 DLSS 5 Neural Rendering in your games. Every RTX. Every API since DirectX 9. One click.

[![Release](https://img.shields.io/github/v/release/xdzleo/dlss5-launcher?style=flat-square)](https://github.com/xdzleo/dlss5-launcher/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square)](#-one-click)
[![RTX](https://img.shields.io/badge/RTX-20%20%7C%2030%20%7C%2040%20%7C%2050-76B900?style=flat-square)](#-every-rtx)
[![APIs](https://img.shields.io/badge/DX9%20%7C%20DX10%20%7C%20DX11%20%7C%20DX12%20%7C%20Vulkan-32--bit%20included-8A2BE2?style=flat-square)](#-every-api-every-era-32-bit-included)

[English](README.md) · [Português](README.pt-BR.md)

</div>

![screenshot](docs/screenshot.png)

**DLSS 5 Neural Rendering** is NVIDIA's neural post-process. It reconstructs detail the renderer
never drew — skin, hair, faces, fabric, the things that make a 2010 game look like 2010. It reached
the public as a leaked runtime: no installer, no documentation, and not a single game that ships it.

Putting it into a game by hand means assembling seven pieces in exactly the right order — ReShade
with add-on support, the neural add-on, two runtimes, a motion-vector provider, shader includes,
and, in a game with no DLSS of its own, a Feeder that fabricates the DLSS contract from scratch.
Get one piece wrong and every failure looks identical from outside: **the game opens and nothing
happens.**

This launcher assembles the whole chain, verifies every link, and names the one that is missing. 🎯

---

## ⚡ One click

1. 📥 Download `DLSS5Launcher-setup.exe` from the [latest release](https://github.com/xdzleo/dlss5-launcher/releases/latest).
2. 🎮 Pick the game. The launcher already found it — Steam, Epic, GOG, Xbox, Ubisoft, EA, Battle.net,
   Rockstar — or point it at any folder, repacks included.
3. 🟢 Flip the **DLSS 5** switch.

In game: **Home** opens the overlay, **F6** toggles the neural pass. That is the entire tutorial.

Windows may show a SmartScreen prompt on first run — [docs/antivirus.md](docs/antivirus.md) explains
why and what to check.

---

## 💚 Every RTX

Neural Rendering runs on tensor cores, so **every GeForce RTX qualifies — 20, 30, 40 and 50 series.**
The launcher picks the right build for your card by itself:

| GPU | Build the launcher installs | Cost of the pass |
| --- | --- | --- |
| 🟢 RTX 50 | NVIDIA's own FP8 model, signed by NVIDIA | native |
| 🟢 RTX 40 | `.SF` community rebuild (patched kernels), origin + SHA-256 verified | higher |
| 🟢 RTX 20 / 30 | `.SF` community rebuild, FP16 path | much higher — the UI tells you |
| ⛔ GTX / GT / MX, AMD, Intel | no tensor cores | the launcher says so **before** downloading 158 MB |

---

## 🎮 Every API. Every era. 32-bit included.

Games that ship DLSS on DirectX 12 are the easy case. **Everything else is why this project exists.**

| Your game | What the launcher does about it |
| --- | --- |
| ✅ **DirectX 12** | the add-on hooks the game's own NGX calls |
| ✅ **DirectX 11** | same, through a private D3D12 device (the bridge) |
| ✅ **Vulkan** | ReShade goes in as an implicit Vulkan layer — no proxy DLL, nothing to rename |
| ✅ **DirectX 10** 🆕 | translated to Vulkan by DXVK 1.10.3 — the only translator that covers this API |
| ✅ **DirectX 9** | translated first — DXVK or dgVoodoo2, chosen per game, switchable in one click |
| ✅ **32-bit games** | 32-bit add-on inside the game, 64-bit helper process beside it |
| ✅ **No DLSS at all** | the DLSS contract (motion vectors, depth, jitter) is fabricated from the frame |
| ✅ **Only FSR / XeSS** | those calls are redirected to DLSS |
| ✅ **One executable per API** | both routes installed at once — you never pick an exe |

If your game draws through any of those, there is a route, and the switch is the same switch.

**Verified on real games, on real hardware** — reported by the chain's own logs, not by eye:

- 🧨 **Just Cause 2** — DirectX 10.1, 32-bit
- 🏫 **Bully: Scholarship Edition** — DirectX 9, 32-bit
- 🧟 **Resident Evil Revelations 2** — DirectX 9, 32-bit (DXVK)
- 🔫 **Saints Row 2** — DirectX 9, 32-bit (dgVoodoo2)
- 🐒 **ENSLAVED: Odyssey to the West** — DirectX 9, 32-bit
- 🎲 **Baldur's Gate 3** — Vulkan and DirectX 11, both installed
- 😈 **DOOM Eternal** — Vulkan, 64-bit

> ⚠️ **Honest limits.** No tensor cores, no Neural Rendering. Online and anti-cheat games: don't —
> the community runtime rebuild modifies a file the anti-cheat may check, and the launcher warns
> you when it finds one. A translator can crash one specific game; that is why there are two, and
> switching is one click. Games with no DLSS run the pass in DLAA at full resolution: **image
> quality goes up, FPS does not.**

---

## 🔗 Nothing fails silently

Every broken link produces the same symptom from outside, so the launcher refuses to hide them.
The chain is drawn link by link — ReShade, add-on, neural runtime, Ray Reconstruction, early load,
the switch, and the bridge, Feeder or translator when the game needs one. Green means present.
The install is ready only when all of them are.

`--check` prints the plan before a single byte is written. Bitness, API, translator, where ReShade
enters, whether a helper process is needed — all decided from the executable, before the 158 MB:

```
DLSS5Launcher.exe dlss5 "Just Cause 2" --check

  arquitetura    : 32 bits
  gpu            : NVIDIA GeForce RTX 5090   (custo do pass: RTX 50)
  DLSS proprio   : nao
  tradutor DX10  : DXVK 1.10.3 (d3d10.dll proprio -> Vulkan) — o unico que traduz D3D10
  ReShade entra  : camada Vulkan
  metades 32 bits: com transporte Vulkan
  processo extra : host64 (o DLSS e x64; um jogo de 32 bits nao o carrega)
  (nada foi escrito — isto e so o plano)
```

`verify <game>` reads ReShade.log after you played and says whether the add-on actually loaded. The
**conflict scanner** reads what *else* is sitting in the game folder — OptiScaler, Special K,
fakenvapi, a ReShade installed by hand, a dgVoodoo from a 2019 tutorial — identifies each by its
version info, and tells you which one is sitting in the slot the chain needs.

---

## 🧪 For the technical crowd: what no other installer does

### 1. 🧬 DLSS inside a 32-bit game

DLSS, NGX and the neural add-on are x64-only. A 32-bit process **cannot load them, period** — no
wrapper, no trick. The route: a tiny 32-bit ReShade add-on lives inside the game and captures
color, depth and motion vectors; a separate, real 64-bit helper (`dlss5-feed-host64.exe`) opens
its own D3D12 device and runs DLAA plus the neural pass there. Textures cross the process boundary
as NT shared handles synchronized by a fence. **Nothing ever touches system memory.**

This builds on jlrouzies-fr's Feeder 0.6 split — but the official 32-bit add-on accepts D3D11 only
(`only Direct3D 11 games are supported by the 32-bit add-on`, verbatim in its source), which
closes the door on every DX9 game DXVK renders, because DXVK delivers Vulkan. This launcher ships a
build with a **Vulkan transport added**, in the same design the 64-bit add-on already used: the
host creates the textures on D3D12 and the game imports them with
`VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT`. Direction matters — a resource created by
Vulkan cannot be opened by D3D12's `OpenSharedHandle`. Both halves are 124 KB, built from the
Feeder's MIT source, embedded because they exist in no public release.

### 2. 🔁 DirectX 9 — two translators, measured per game

ReShade on D3D9 stops at Shader Model 3: no motion-vector provider compiles, and the API has no
shared handles and no fence. So the game is translated first. **DXVK** (D3D9 → Vulkan) is the
default, and the reason DX9 works here at all: on Vulkan ReShade compiles compute shaders, which is
what the motion-vector provider needs. **dgVoodoo2** (D3D9 → D3D11) covers the games DXVK drops.

Measured on one machine, same add-on, same runtime:

| Game | dgVoodoo2 | DXVK |
| --- | --- | --- |
| Resident Evil Revelations 2 | crash `0xc0000005` before the menu | **runs** — 1800 frames evaluated, 64 fps |
| Saints Row 2 | **runs** — stable | crash `0xc0000005` at ~25 s, after DLSS is already evaluating |

The sets do not contain each other, so the choice sits in the UI, remembered per game. And
dgVoodoo2 is pinned to 2.83.2 from a preservation mirror: 2.87.x access-violates on Blackwell
inside `Direct3DCreate9Ex`, reproduced with a 40-line program outside any game.

### 3. 🆕 DirectX 10 — the API nobody covered

Nothing in the chain speaks D3D10. The Feeder's README says `D3D10 is not supported` in one line;
dgVoodoo2 enters as `D3D9.dll` and never sees a `D3D10CreateDevice1`; the neural add-on is x64.
Until v1.69 this launcher refused these games outright, and *Just Cause 2* was the reason.

DXVK translates D3D10 too — but **not in its current version**, and that was measured, three times,
on the same game with the same chain:

| DXVK | What it ships for D3D10 | Result |
| --- | --- | --- |
| 3.1 (current) | `d3d10core.dll` + `d3d11.dll` + `dxgi.dll` | runs without ReShade; **dies 3 s after launch with ReShade**, Feeder or not |
| 1.10.3 wrappers over the 3.1 core | old `d3d10.dll` + `d3d10_1.dll` | clean exit at 2 s |
| **1.10.3, all five files** | its own `d3d10.dll` and `d3d10_1.dll` | **runs, neural pass evaluating every frame** |

ReShade.log explains the first row: since DXVK 2.0 there is no `d3d10.dll`/`d3d10_1.dll` of its
own, so the game loads **Windows'** — and ReShade, present through the Vulkan layer, installs its
delayed hooks in them and wraps DXVK's D3D10 device in a wrapper of its own. The process dies right
after. On the DX9 route this never happens: the `d3d9.dll` loaded is DXVK's local one, and the
hook on the system copy stays "Delayed" forever. 1.10.3 is the last release with both wrappers;
with them in the game folder, Windows never enters and only the Vulkan runtime exists.

### 4. 🧵 Games with no DLSS — fabricating the contract

The neural pass reads the buffers a game hands to DLSS. A 2008 game hands nothing. The Feeder
fabricates the contract: a ReShade compute shader (LumeniteFX Kernel, provider 3) produces motion
vectors and depth, the add-on opens a private D3D12 device, and DLAA runs on real data. The
launcher owns every detail the Feeder leaves to the user — the compile-time `DLSS5_MV_PROVIDER`
define (without it the shader compiles with provider 0 and the pass runs blind, with a clean log),
the technique order in the preset (the provider must write before the Feed reads, same frame),
the shader includes ReShade's installer never copies, the blue-noise texture without which the
provider silently produces nothing, the Generic Depth add-on that ReShade ships disabled, and
`warmup_rebuild=0` so engines that close their memory pool at startup do not get a second
allocation at the worst moment.

Two lessons cost real crashes and are encoded: `create_delay` is never zeroed (the add-on arms
its NGX hooks asynchronously, and calling in too early kills *Final Fantasy XV* on every save load),
and a DLSS 1.x runtime is never replaced (generation 1.0 is a different API; swapping the DLL makes
the game call an implementation that does not answer, and it dies with no exception in the Event
Log).

### 5. 🔏 Every RTX, without trusting a random binary

NVIDIA's original model carries `sm_120` kernels and runs only on Blackwell. For RTX 20/30/40
there are the `.SF` rebuilds — patched binaries, which means a broken Authenticode signature. The
launcher does not simply accept them: a community rebuild is installed **only** when its origin is
the RHI repository the index already pins **and** its SHA-256 matches a value verified by hand and
written in the code. Anything else is refused by version name. On Blackwell, NVIDIA's signed build
is the only one used.

Same rule everywhere. ReShade is verified against a pinned certificate before extraction (and the
ZIP appended to its setup is covered by that signature — one byte changed inside it flips the
status to HashMismatch). Every other piece is fetched from the project that made it, at install
time, over HTTPS to an allowlist of hosts. The only thing bundled in the exe is the 124 KB pair of
Vulkan-transport halves.

### 6. 🔍 The chain is visible, the plan is printable, the decisions are testable

The dry-run decides bitness, API and translator from the executable before downloading anything.
`tests/ChainProbe` runs the exact chain logic the UI runs against a real game folder and prints
link by link — useful when the switch says "install" on a game that is already working. The
smoke test builds fake executables that import `d3d10.dll` or `d3d11.dll` and checks the routing
decisions against them. The ReShade Vulkan layer is registered once, globally, under a name of
its own — *DOOM Eternal* blacklists Vulkan layers by name, and "ReShade" is on the list. A folder
with one executable per API gets both routes and a marker saying so, so the conflict scanner does
not report its own work as a conflict.

None of this is clever for its own sake. Each item is a failure that produced the same silent
"nothing happens", found in a log, and closed.

---

## 📦 What gets installed

Everything is fetched from the projects that made it, at install time:

| Piece | From |
| --- | --- |
| ReShade with add-on support | [reshade.me](https://reshade.me) |
| `renodx-dlss5.addon64`, `nvngx_dlssnr.dll`, DLSS runtimes | [RankFTW/RHI](https://github.com/RankFTW/RHI) |
| `DLSS5_Feed.fx` + the 64-bit add-on | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) |
| motion vectors (`lumenite_*`) | [umar-afzaal/LumeniteFX](https://github.com/umar-afzaal/LumeniteFX) |
| DXVK (current for DX9, 1.10.3 for DX10) | [doitsujin/dxvk](https://github.com/doitsujin/dxvk) |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) |
| OptiScaler | [optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler) |

The only thing bundled in the exe is the pair of 32-bit halves with the Vulkan transport (124 KB),
built from the Feeder's MIT source — they exist in no public release.

## 🧰 Also does

The launcher started as a manager for [RenoDX](https://github.com/clshortfuse/renodx) HDR mods and
still does all of it: per-game mod install and toggle, HDR nits from the UI without opening the
game, DLSS runtime swaps with backups, mod update checks, and a games list built from your stores.

## 🖥️ Command line

```
list                    detected games and their state
dlss5 <game>            install DLSS 5      (--dgvoodoo · --check)
dlss5 --all             every eligible game
verify <game>           read ReShade.log and say whether the mod loaded
settings <game>         mod settings from ReShade.ini
set <game> key=value    write settings (close the game first)
doctor                  environment check
```

## 🧱 Building

```
git clone https://github.com/xdzleo/dlss5-launcher
cd dlss5-launcher
dotnet build src/RenoDXLauncher.csproj -c Release
```

.NET 10 SDK, Windows. `tools/gen_resx.ps1` regenerates the string resources from
`src/Localization/strings.json`; `tools/gen_icon.ps1` regenerates the icon.

Tests: `dotnet run --project tests/SmokeTest` covers install/remove against a fake game folder.
`tests/ChainProbe` runs the real chain logic against a real game folder and prints link by link.

## 🌍 Translating

Strings live in `src/Localization/strings.json`, one entry per key with every language side by
side. Add a language tag there, run `tools/gen_resx.ps1`, add the tag to
`<SatelliteResourceLanguages>` in the csproj.

## 🙏 Credits

- [clshortfuse](https://github.com/clshortfuse) — RenoDX, and the neural add-on this builds on
- [jlrouzies-fr](https://github.com/jlrouzies-fr) — DLSS5-Feeder, which makes DLSS-less games possible
- [umar-afzaal](https://github.com/umar-afzaal) — LumeniteFX, the motion-vector provider
- [crosire](https://github.com/crosire) — ReShade
- [Dege](https://github.com/dege-diosg) — dgVoodoo2 · [doitsujin](https://github.com/doitsujin) — DXVK
- [RankFTW](https://github.com/RankFTW) — RHI, the runtime index

## 📜 License

MIT — see [LICENSE](LICENSE). Covers this launcher's own code; everything it downloads keeps its
own license, and the neural add-on is closed-source and community-distributed.
