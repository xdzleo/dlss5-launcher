<div align="center">

<img src="docs/icon.png" width="112" alt="">

# DLSS 5 Launcher

**Um clique põe o DLSS 5 Neural Rendering nos seus jogos — inclusive DirectX 9 e títulos de 32 bits, que nenhum outro instalador alcança.**

[![Release](https://img.shields.io/github/v/release/xdzleo/dlss5-launcher?style=flat-square)](https://github.com/xdzleo/dlss5-launcher/releases/latest)
[![Licença](https://img.shields.io/badge/licen%C3%A7a-MIT-blue?style=flat-square)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square)](#instalar)

[English](README.md) · [Português](README.pt-BR.md)

</div>

![captura](docs/screenshot.png)

O DLSS 5 Neural Rendering é o pós-processo da NVIDIA que reconstrói detalhe que o renderizador
nunca desenhou — o efeito aparece mais em pele, cabelo e rosto. Ele chegou ao público como um
runtime vazado, sem instalador e sem nenhum jogo que o traga de fábrica.

Colocá-lo num jogo significa montar uma cadeia à mão: ReShade com suporte a add-on, o add-on
neural, dois runtimes, um provedor de motion vectors, os includes de shader e — num jogo que não
tem DLSS nenhum — um Feeder que fabrica o contrato do DLSS do zero. Falta uma peça e todas as
falhas parecem a mesma coisa de fora: o jogo abre e nada acontece.

Este launcher monta essa cadeia, confere elo por elo, e diz qual está faltando quando algo
está errado.

## O que ele cobre

| Jogo | Como |
| --- | --- |
| DirectX 12 | o add-on engancha nas chamadas NGX do próprio jogo |
| DirectX 11 | igual, através de um device D3D12 privado |
| Vulkan | o ReShade entra como camada Vulkan |
| **DirectX 9** | traduzido antes — DXVK ou dgVoodoo2, escolhido por jogo |
| **32 bits** | add-on de 32 bits no jogo, processo auxiliar de 64 ao lado |
| sem DLSS nenhum | o contrato do DLSS é fabricado a partir do frame |
| só FSR/XeSS | essas chamadas são redirecionadas para DLSS |

**O caminho de 32 bits é o difícil, e é por causa dele que este projeto existe.** O DLSS é só x64:
um jogo de 32 bits não consegue carregá-lo, ponto. O launcher sobe um processo auxiliar de 64 bits
ao lado do jogo, compartilha as texturas entre processos por handles NT com fence, e roda o passe
neural lá.

**DirectX 9 precisa de tradutor**, porque o ReShade em D3D9 para no Shader Model 3 e nenhum
provedor de motion vectors compila. Existem dois, e nenhum cobre tudo — então você escolhe:

- **DXVK** traduz D3D9 para Vulkan. É o padrão, e o motivo de DX9 funcionar aqui: em Vulkan o
  ReShade compila compute shader, que é do que o provedor de motion vectors precisa.
- **dgVoodoo2** traduz D3D9 para D3D11. Para os jogos que o DXVK derruba.

Medido numa máquina, com o mesmo add-on e o mesmo runtime: o *Resident Evil Revelations 2* roda
**só** com DXVK (o dgVoodoo crasha antes do menu); o *Saints Row 2* roda **só** com dgVoodoo (o
DXVK crasha aos ~25 s, depois de o DLSS já estar avaliando). Os conjuntos não se contêm, então a
escolha fica na interface, lembrada por jogo.

Fazer o add-on de 32 bits falar Vulkan exigiu estender o Feeder: o build oficial aceita só D3D11
(`only Direct3D 11 games are supported by the 32-bit add-on`, literal no fonte dele). Este
launcher traz um build com transporte Vulkan somado, no mesmo desenho que o add-on de 64 bits já
usava — o host cria as texturas em D3D12 e o jogo as importa com
`VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT`.

## Instalar

1. Baixe o `DLSS5Launcher-setup.exe` do [último release](https://github.com/xdzleo/dlss5-launcher/releases/latest).
2. Rode, escolha o jogo, aperte o interruptor de DLSS 5.
3. No jogo: **Home** abre o ReShade, **F6** liga e desliga o passe neural.

O Windows pode mostrar um aviso do SmartScreen — [docs/antivirus.md](docs/antivirus.md) explica o
porquê e o que conferir.

### Placas suportadas

O Neural Rendering precisa de tensor core, então qualquer RTX serve; GTX/GT/MX e não-NVIDIA não, e
o launcher avisa antes em vez de instalar 158 MB que não têm como rodar.

O modelo original é FP8 com kernels só de Blackwell. Para placas anteriores o launcher escolhe
sozinho o build `.SF` (binários patcheados para RTX 40, caminho FP16 para RTX 20/30) — o custo do
passe é bem maior nelas, e a interface diz em qual faixa você está.

## O que é instalado

Tudo vem dos projetos que fizeram cada peça, na hora da instalação:

| Peça | De |
| --- | --- |
| ReShade com suporte a add-on | [reshade.me](https://reshade.me) |
| `renodx-dlss5.addon64`, `nvngx_dlssnr.dll`, runtimes de DLSS | [RankFTW/RHI](https://github.com/RankFTW/RHI) |
| `DLSS5_Feed.fx` + o add-on de 64 bits | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) |
| motion vectors (`lumenite_*`) | [umar-afzaal/LumeniteFX](https://github.com/umar-afzaal/LumeniteFX) |
| DXVK | [doitsujin/dxvk](https://github.com/doitsujin/dxvk) |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) |
| OptiScaler | [optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler) |

A única coisa embutida no exe é o par de metades de 32 bits com o transporte Vulkan (124 KB),
construído do fonte MIT do Feeder — elas não existem em release público.

## A cadeia, e por que ela aparece

Todo elo quebrado dá o mesmo sintoma de fora: o jogo abre e nada acontece. Por isso o launcher
mostra os elos um a um — ReShade, add-on, runtime neural, Ray Reconstruction, carga antecipada, o
interruptor, e a ponte ou o Feeder quando são necessários. Verde é presente; a instalação só está
pronta quando todos estão.

O `--check` imprime o plano sem escrever nada:

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

## Também faz

O launcher nasceu como gerenciador dos mods HDR do [RenoDX](https://github.com/clshortfuse/renodx)
e continua fazendo tudo aquilo: instalar e desligar mod por jogo, ajustar os nits de HDR pela
interface sem abrir o jogo, trocar runtimes de DLSS com backup, checar atualização de mod, e
montar a lista de jogos a partir das suas lojas.

## Linha de comando

```
list                    jogos detectados e o estado de cada um
dlss5 <jogo>            instala DLSS 5      (--dgvoodoo · --check)
dlss5 --all             todos os jogos elegíveis
verify <jogo>           lê o ReShade.log e diz se o mod carregou
settings <jogo>         configurações do mod no ReShade.ini
set <jogo> chave=valor  grava configurações (feche o jogo antes)
doctor                  checagem do ambiente
```

## Compilar

```
git clone https://github.com/xdzleo/dlss5-launcher
cd dlss5-launcher
dotnet build src/RenoDXLauncher.csproj -c Release
```

SDK do .NET 10, Windows. O `tools/gen_resx.ps1` regenera os recursos de string a partir do
`src/Localization/strings.json`; o `tools/gen_icon.ps1` regenera o ícone.

Testes: `dotnet run --project tests/SmokeTest` cobre instalar/remover contra uma pasta de jogo
falsa. O `tests/ChainProbe` roda a lógica real da cadeia contra uma pasta de jogo de verdade e
imprime elo por elo — útil quando o interruptor diz "instalar" num jogo que já está funcionando.

## Traduzir

As strings ficam em `src/Localization/strings.json`, uma entrada por chave com todos os idiomas
lado a lado. Acrescente a tag do idioma lá, rode `tools/gen_resx.ps1`, e adicione a tag ao
`<SatelliteResourceLanguages>` no csproj.

## Créditos

- [clshortfuse](https://github.com/clshortfuse) — RenoDX, e o add-on neural sobre o qual isto é construído
- [jlrouzies-fr](https://github.com/jlrouzies-fr) — DLSS5-Feeder, que torna possível jogo sem DLSS
- [umar-afzaal](https://github.com/umar-afzaal) — LumeniteFX, o provedor de motion vectors
- [crosire](https://github.com/crosire) — ReShade
- [Dege](https://github.com/dege-diosg) — dgVoodoo2 · [doitsujin](https://github.com/doitsujin) — DXVK
- [RankFTW](https://github.com/RankFTW) — RHI, o índice de runtimes

## Licença

MIT — veja [LICENSE](LICENSE). Cobre o código do launcher; tudo o que ele baixa mantém a licença
própria, e o add-on neural é closed-source, distribuído pela comunidade.
