<div align="center">

<img src="docs/icon.png" width="112" alt="">

# RenoDX Launcher

**Instale, ative e ajuste os mods HDR do [RenoDX](https://github.com/clshortfuse/renodx) nos seus jogos — sem sair do launcher.**

[![Release](https://img.shields.io/github/v/release/xdzleo/renodx-launcher?style=flat-square)](https://github.com/xdzleo/renodx-launcher/releases/latest)
[![Licença](https://img.shields.io/badge/licença-MIT-blue?style=flat-square)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square)](#instalação)

[English](README.md) · [Português](README.pt-BR.md)

</div>

![screenshot](docs/screenshot.png)

O [**RenoDX**](https://github.com/clshortfuse/renodx) — de "Renovation Engine for DirectX Games" —
é o conjunto de ferramentas do [clshortfuse](https://github.com/clshortfuse) para modificar jogos
através do sistema de add-ons do ReShade, e a razão de existir HDR de verdade, por jogo, em
centenas de títulos no PC. Este projeto não é o RenoDX. Ele é um launcher **para** o RenoDX: varre
os jogos que você tem instalados, casa cada um com o catálogo do RenoDX e cuida da instalação que
esses mods normalmente exigem na mão — o ReShade, o addon certo, a DLL de proxy certa e as
configurações de brilho do próprio mod.

Os mods, e o trabalho que faz tudo isso valer a pena, são deles.

## Instalação

Baixe o **`RenoDXLauncher-<versão>-setup.exe`** da
[última release](https://github.com/xdzleo/renodx-launcher/releases/latest) e execute.

Self-contained: não precisa instalar o runtime do .NET. Windows 10 ou 11, x64. Junto do
instalador sai também um `.zip` portátil, para quem prefere não instalar nada, e toda release
traz o `SHA256SUMS.txt`.

## O que ele faz

**Detecção de jogos** na Steam, Epic, GOG, Xbox / Game Pass e Battle.net, além de qualquer pasta
que você adicionar à mão — inclusive pasta que não tem o nome do jogo, que é resolvida pelo
executável.

**Catálogo combinado de ~890 jogos**, montado a partir do índice oficial do RenoDX, da wiki de
mods (mods dos forks, mods só do Nexus, e as tabelas dos genéricos de Unreal e Unity) e do
conjunto de dados do RHI, que traz caminho de instalação por jogo, API gráfica e notas curadas.

**Instalação em um clique.** Baixa o ReShade com suporte a add-ons, confere a assinatura, lê a
import table do executável do jogo para escolher a DLL de proxy correta — `dxgi.dll`, `d3d9.dll`,
`opengl32.dll` — e coloca o `renodx-<jogo>.addon64` correspondente ao lado.

**Ativa e desativa por jogo**, e atualiza todos os mods instalados de uma vez quando saem builds
novas.

**As configurações do mod, dentro do launcher.** Brilho máximo, paper white, brilho da interface,
tone mapper, gamma e todo o conjunto de color grading, gravados direto no `ReShade.ini` do jogo.
Um manifest embutido com 6.698 configurações de 294 jogos — extraídas do código-fonte do renodx —
garante que cada chave seja escrita com a caixa exata que aquele mod espera.

**Perfil de monitor.** Meça o pico de nits do seu monitor uma vez e aplique em qualquer jogo com
um clique.

**Checklist de HDR embutido**, cobrindo o que estraga HDR em silêncio: HDR do Windows ligado,
AutoHDR e RTX HDR desligados, HGIG, e o aviso de anti-cheat para jogos online.

**Português do Brasil e inglês**, seguindo o idioma do Windows por padrão.

## Linha de comando

Tudo que a interface faz também roda sem janela — para automatizar, para diagnosticar e para
abrir um relatório de bug que sirva para alguma coisa.

```bash
RenoDXLauncher.exe list                       # jogos detectados e estado do mod
RenoDXLauncher.exe check                      # quais mods têm build mais nova
RenoDXLauncher.exe verify                     # o mod carregou mesmo? (lê o ReShade.log)
RenoDXLauncher.exe settings "dying light"     # configurações atuais do mod
RenoDXLauncher.exe set "dying light" ToneMapPeakNits=1300 --dry-run
RenoDXLauncher.exe profile --peak 1300        # perfil de nits do monitor
RenoDXLauncher.exe install "elden ring"       # instala ReShade + addon
RenoDXLauncher.exe enable "sekiro"            # ativa / desativa o mod
RenoDXLauncher.exe add "C:\caminho\da\pasta"  # registra pasta que as lojas não conhecem
RenoDXLauncher.exe doctor                     # diagnóstico completo
```

O nome do jogo casa com qualquer trecho, sem diferenciar maiúsculas. `list` e `check` aceitam
`--json`. `set` mostra o arquivo-alvo e o antes → depois; com `--dry-run` nada é gravado.
Instalar pela linha de comando aborta quando detecta anti-cheat — a confirmação consciente de
risco existe só na interface.

## Como ele trata seus jogos

O launcher é conservador de propósito com arquivo que não é dele:

- nunca escreve no `ReShade.ini` com o jogo aberto, porque o overlay reescreve a seção inteira
  quando o jogo fecha;
- preserva a caixa das chaves que já estão no ini, já que o mod as lê diferenciando maiúsculas;
- se recusa a sobrescrever uma DLL de proxy que ele não consiga identificar como ReShade, então
  instalações de ENB, dxvk e Special K ficam intactas;
- mantém exatamente um addon renodx por pasta, porque dois addons brigam pelas mesmas chaves;
- confere o download do ReShade contra o certificado de assinatura do autor do ReShade antes de
  extrair qualquer coisa;
- detecta anti-cheat e avisa antes de instalar, porque o ReShade com add-ons é um build não
  assinado e isso é risco de banimento em jogo online.

## Compilando

Precisa do SDK do .NET 10, no Windows.

```powershell
dotnet build src\RenoDXLauncher.csproj          # debug
pwsh tools\build-installer.ps1 -Zip             # publish + instalador em dist\
```

O `tests\SmokeTest` exercita o pipeline inteiro contra um jogo falso — catálogo, matching,
download e extração reais do ReShade, instalação do addon, toggle e ida e volta das
configurações. Ele nunca encosta numa pasta de jogo de verdade.

```powershell
cd tests\SmokeTest; dotnet run
```

O `tests\ScanProbe` roda cada detector de loja isoladamente e mostra o que achou e em quanto
tempo — é a primeira coisa a rodar quando um jogo não aparece.

## Traduzindo

Os textos ficam em [`src/Localization/strings.json`](src/Localization/strings.json), com todos os
idiomas lado a lado para a tradução ser revisada num lugar só.

1. acrescente sua entrada `"<tag-bcp-47>"` nas strings que for traduzir;
2. rode `python tools/gen_resx.py`;
3. registre a tag em `L.Available` (`src/Localization/L.cs`) e em `SatelliteResourceLanguages`
   (`src/RenoDXLauncher.csproj`).

Chave sem tradução cai no português do Brasil, então tradução parcial já é utilizável.

## Documentação

- [Política de assinatura de código](docs/code-signing-policy.md)
- [Configuração da assinatura de release](docs/SIGNING-SETUP.md)
- [Falso-positivo de antivírus](docs/antivirus.md)
- [Changelog](CHANGELOG.md)

## Créditos

Este launcher é um cliente para o trabalho de outras pessoas. Todo ele.

- **[clshortfuse/renodx](https://github.com/clshortfuse/renodx)** — o RenoDX em si, e cada
  mantenedor de mod que porta e calibra um jogo. O catálogo, as configurações que este launcher
  grava e a razão de a imagem sair certa na tela vêm deles.
  [Lista de mods](https://github.com/clshortfuse/renodx/wiki/Mods) ·
  [Discord](https://discord.gg/F6AUTeWJHM)
- **[crosire/reshade](https://github.com/crosire/reshade)** — o runtime de add-ons sobre o qual o
  RenoDX é construído, e que este launcher instala.
- **[RankFTW/RHI](https://github.com/RankFTW/RHI)** — dados de instalação por jogo
  (`manifest.json`, GPL-3.0), usados com crédito.

Relato de bug sobre um *mod* é upstream, com o mantenedor daquele mod, não aqui. Problema com o
launcher em si — detecção, instalação, a interface de configurações — é
[neste repositório](https://github.com/xdzleo/renodx-launcher/issues).

## Licença

MIT — veja [LICENSE](LICENSE). A fonte Inter, embutida, sob a SIL Open Font License 1.1.
