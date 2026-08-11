# RenoDX Launcher

<img src="docs/icon.png" width="96" align="right" alt="">

Launcher de mods **[RenoDX](https://github.com/clshortfuse/renodx)** (HDR de verdade por jogo), inspirado no
[DLSS Swapper](https://github.com/beeradmoore/dlss-swapper): detecta seus jogos instalados, mostra quais têm
mod RenoDX disponível e deixa você **instalar, ativar/desativar e configurar** o mod de cada jogo — tudo pelo
launcher, sem abrir o jogo.

![screenshot](docs/screenshot.png)

## Download

Baixe o zip da [última release](https://github.com/xdzleo/renodx-launcher/releases/latest), extraia
em qualquer pasta e rode `RenoDXLauncher.exe`. Build self-contained — não precisa instalar .NET.

### Antivírus acusando (falso-positivo)

Até a assinatura digital entrar em vigor, os releases são **não assinados**, e o SmartScreen/Chrome
e alguns antivírus (em especial o **360 Total Security**) podem acusar como suspeito. É
**falso-positivo**: todo o código-fonte está aqui neste repo. O app baixa o ReShade e escreve DLLs
em pastas de jogos (comportamento legítimo do que ele faz, mas que heurísticas confundem com malware).
Opções enquanto isso:

- Libere/whitelist o `RenoDXLauncher.exe` no seu antivírus, ou
- Compile você mesmo: `dotnet publish -c Release -r win-x64 --self-contained -o app` na pasta `src`.

**Assinatura de código:** os releases estão sendo migrados para assinatura via
[SignPath Foundation](https://signpath.org/) (grátis para open-source). Detalhes em
[docs/code-signing-policy.md](docs/code-signing-policy.md); setup em
[docs/SIGNING-SETUP.md](docs/SIGNING-SETUP.md).

## O que ele faz

- **Detecção de jogos** (algoritmos do DLSS Swapper reimplementados):
  - **Steam** — registry `HKLM\SOFTWARE\Valve\Steam` → `libraryfolders.vdf` → `appmanifest_*.acf`
  - **Epic** — `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`
  - **GOG** — registry `HKLM\SOFTWARE\GOG.com\Games`
  - **Xbox / Game Pass** — arquivos `.GamingRoot` + `MicrosoftGame.config` (alvo: `gamelaunchhelper.exe`)
  - **Manual** — qualquer pasta
- **Catálogo RenoDX em camadas** (~890 entradas):
  1. [`games-index.json`](https://clshortfuse.github.io/renodx/games-index.json) oficial (com Steam AppID → matching exato)
  2. Wiki [Mods.md](https://github.com/clshortfuse/renodx/wiki/Mods) (mods dos forks, Nexus-only, e as tabelas dos mods genéricos de **Unreal Engine** e **Unity**)
  3. [`manifest.json` do RHI](https://github.com/RankFTW/RHI) (GPL-3.0, dados creditados): subpasta de instalação por jogo, API gráfica, nome da DLL, jogos com HDR nativo e notas curadas
- **Instalação em 1 clique**:
  - Baixa o **ReShade (addon support)** do reshade.me e extrai `ReShade64/32.dll` do ZIP embutido no instalador (sem rodar o setup)
  - Detecta a API pelo import table do exe (PE) → instala como `dxgi.dll` / `d3d9.dll` / `opengl32.dll`
  - **Não sobrescreve** DLL de outro mod (ENB/dxvk/SpecialK) — verifica o ProductName antes
  - Baixa o `renodx-<jogo>.addon64/.addon32` do snapshot e coloca junto do exe
- **Ativar/Desativar por jogo**: renomeia `renodx-*.addon64` ⇄ `.addon64.disabled` (o ReShade só carrega `*.addon64`)
- **Configurações por jogo direto no launcher** — edita o `ReShade.ini` do jogo (`[renodx-preset1]`, a seção
  que o mod carrega no boot):
  - **Brilho máximo (nits)** — o pico real do monitor (app Calibração de HDR do Windows)
  - **Brilho do jogo / paper white** — padrão 203 nits (ITU BT.2408)
  - **Brilho da UI**, **tone mapper**, **correção de gamma**, e todos os sliders de color grading do mod
  - Manifest embutido com as **6.698 settings de 294 jogos** extraídas do código-fonte do renodx
    (chave exata com o case certo por mod — PascalCase vs camelCase)
  - Perfil do monitor ("Meu monitor"): define os nits uma vez e aplica em qualquer jogo com 1 clique
- **Guia HDR** embutido: checklist (Windows HDR ON, AutoHDR/RTX HDR OFF, HGIG, aviso de anti-cheat…)

## Regras de segurança que o launcher segue

- Nunca escreve no `ReShade.ini` com o jogo aberto (o overlay sobrescreveria a seção inteira)
- Preserva o case das chaves já existentes no ini (o mod lê case-sensitive)
- Exatamente **um** addon renodx por pasta (dois addons brigam pelas mesmas chaves)
- ReShade com addon support é **não assinado** → cuidado com anti-cheat em jogos online (aviso no app)

## Build

```
cd src
dotnet build          # debug
dotnet publish -c Release -o ..\app
```

Requisitos: .NET 10 SDK, Windows.

## Testes

`tests\SmokeTest` roda o pipeline inteiro contra um jogo **falso** (nunca toca jogos reais):
catálogo → matching → download real do ReShade + extração → download do addon → toggle → escrita/leitura de settings.

```
cd tests\SmokeTest
dotnet run
```

## Linha de comando

Tudo que a interface faz também roda pelo terminal — útil para automatizar, diagnosticar e
reportar bugs sem abrir a janela:

```bash
RenoDXLauncher.exe list                       # jogos detectados + estado do mod
RenoDXLauncher.exe check                      # quais mods têm versão nova
RenoDXLauncher.exe verify                     # o mod carregou mesmo? (lê o ReShade.log)
RenoDXLauncher.exe settings "dying light"     # configurações atuais do mod
RenoDXLauncher.exe set "dying light" ToneMapPeakNits=1300 --dry-run
RenoDXLauncher.exe profile --peak 1300        # perfil de nits do monitor
RenoDXLauncher.exe install "elden ring"       # instala ReShade + addon
RenoDXLauncher.exe enable/disable "sekiro"    # liga/desliga o mod
RenoDXLauncher.exe doctor                     # diagnóstico completo
```

`list` e `check` aceitam `--json`. `set` sempre mostra o arquivo-alvo e o antes→depois;
com `--dry-run` não grava nada. Instalar por CLI **aborta** se detectar anti-cheat (a confirmação
consciente de risco só existe na interface).

## Diagnóstico: "meu jogo não aparece"

`tests\ScanProbe` roda cada detector isoladamente e mostra quantos jogos cada um achou (e em
quantos ms), mais o total após dedupe e quantos casaram com o catálogo:

```bash
cd tests\ScanProbe && dotnet run
```

## Ferramentas

- `tools\extract_settings_manifest.py` — regenera `src\Assets\settings_manifest.json` a partir de clones do
  renodx (repo principal + forks dos maintainers):
  ```
  python tools\extract_settings_manifest.py <renodx> <fork1> ... -o src\Assets\settings_manifest.json
  ```

## Créditos

- [clshortfuse/renodx](https://github.com/clshortfuse/renodx) e todos os maintainers dos mods
- [beeradmoore/dlss-swapper](https://github.com/beeradmoore/dlss-swapper) — inspiração de UX e detecção de jogos
- [RankFTW/RHI](https://github.com/RankFTW/RHI) — dados de instalação por jogo (manifest.json, GPL-3.0)
- [crosire/reshade](https://github.com/crosire/reshade)
