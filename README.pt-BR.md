<div align="center">

<img src="docs/icon.png" width="112" alt="">

# DLSS 5 Launcher

### 🧠 DLSS 5 Neural Rendering nos seus jogos. Toda RTX. Toda API desde o DirectX 9. Um clique.

[![Release](https://img.shields.io/github/v/release/xdzleo/dlss5-launcher?style=flat-square)](https://github.com/xdzleo/dlss5-launcher/releases/latest)
[![Licença](https://img.shields.io/badge/licen%C3%A7a-MIT-blue?style=flat-square)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square)](#-um-clique)
[![RTX](https://img.shields.io/badge/RTX-20%20%7C%2030%20%7C%2040%20%7C%2050-76B900?style=flat-square)](#-toda-rtx)
[![APIs](https://img.shields.io/badge/DX9%20%7C%20DX10%20%7C%20DX11%20%7C%20DX12%20%7C%20Vulkan-32%20bits%20inclu%C3%ADdo-8A2BE2?style=flat-square)](#-toda-api-toda-época-32-bits-incluído)

[English](README.md) · [Português](README.pt-BR.md)

</div>

![captura](docs/screenshot.png)

O **DLSS 5 Neural Rendering** é o pós-processo neural da NVIDIA. Ele reconstrói detalhe que o
renderizador nunca desenhou — pele, cabelo, rosto, tecido, as coisas que fazem um jogo de 2010
parecer de 2010. Chegou ao público como um runtime vazado: sem instalador, sem documentação e sem
um único jogo que o traga de fábrica.

Colocá-lo num jogo à mão significa montar sete peças exatamente na ordem certa — ReShade com
suporte a add-on, o add-on neural, dois runtimes, um provedor de motion vectors, os includes de
shader e, num jogo sem DLSS nenhum, um Feeder que fabrica o contrato do DLSS do zero. Erre uma peça
e todas as falhas parecem a mesma coisa de fora: **o jogo abre e nada acontece.**

Este launcher monta a cadeia inteira, confere elo por elo, e diz qual está faltando. 🎯

---

## ⚡ Um clique

1. 📥 Baixe o `DLSS5Launcher-setup.exe` do [último release](https://github.com/xdzleo/dlss5-launcher/releases/latest).
2. 🎮 Escolha o jogo. O launcher já o encontrou — Steam, Epic, GOG, Xbox, Ubisoft, EA, Battle.net,
   Rockstar — ou aponte para qualquer pasta, repack incluído.
3. 🟢 Aperte o interruptor de **DLSS 5**.

No jogo: **Home** abre o overlay, **F6** liga e desliga o passe neural. Esse é o tutorial inteiro.

O Windows pode mostrar um aviso do SmartScreen na primeira vez — [docs/antivirus.md](docs/antivirus.md)
explica o porquê e o que conferir.

---

## 💚 Toda RTX

O Neural Rendering roda em tensor core, então **toda GeForce RTX serve — séries 20, 30, 40 e 50.**
O launcher escolhe sozinho o build certo para a sua placa:

| Placa | Build que o launcher instala | Kernels que ele traz | Custo do passe |
| --- | --- | --- | --- |
| 🟢 RTX 50 | `310.8.0` — o modelo FP8 da própria NVIDIA, assinado | `sm_120` | nativo |
| 🟢 RTX 40 | `310.8.0-RTX40` — kernels retargetados para Ada | `sm_89`, `sm_120` | maior |
| 🟢 RTX 30 | `310.8.SF-v2` — rebuild da comunidade, caminho FP16 | `sm_75`, `86`, `89`, `120` | bem maior — a interface avisa |
| 🟢 RTX 20 | o mesmo build universal | `sm_75`, `86`, `89`, `120` | bem maior |
| ⛔ GTX / GT / MX, AMD, Intel | nenhum | — | o launcher diz isso **antes** de baixar 158 MB |

**Quem decide é o arquivo, não uma tabela.** O runtime é uma biblioteca CUDA: o código de GPU vai
em registros `fatbin`, um por arquitetura. O build da própria NVIDIA traz `sm_120` e mais nada,
então numa RTX 20/30/40 ele instala inteiro e **nunca roda** — sem erro do add-on, do jogo ou do
log. O launcher lê esses registros dentro do arquivo baixado (70 ms em 165 MB): se o build não
tem kernel para a sua placa, ele passa para o próximo candidato em vez de deixar 158 MB que não
rodam, e um runtime que já esteja na biblioteca e não sirva vira um bloqueio com o motivo escrito.

---

## 🎮 Toda API. Toda época. 32 bits incluído.

Jogo com DLSS de fábrica em DirectX 12 é o caso fácil. **Todo o resto é o motivo de este projeto
existir.**

| Seu jogo | O que o launcher faz com ele |
| --- | --- |
| ✅ **DirectX 12** | o add-on engancha nas chamadas NGX do próprio jogo |
| ✅ **DirectX 11** | igual, através de um device D3D12 privado (a ponte) |
| ✅ **Vulkan** | o ReShade entra como camada Vulkan implícita — sem DLL de proxy, nada para renomear |
| ✅ **DirectX 10** 🆕 | traduzido para Vulkan pelo DXVK 1.10.3 — o único tradutor que cobre essa API |
| ✅ **DirectX 9** | traduzido antes — DXVK ou dgVoodoo2, escolhido por jogo, trocável em um clique |
| ✅ **32 bits** | add-on de 32 bits dentro do jogo, processo auxiliar de 64 ao lado |
| ✅ **Sem DLSS nenhum** | o contrato do DLSS (motion vectors, profundidade, jitter) é fabricado a partir do frame |
| ✅ **Só FSR / XeSS** | essas chamadas são redirecionadas para DLSS |
| ✅ **Um executável por API** | as duas rotas instaladas de uma vez — você nunca escolhe exe |

Se o seu jogo desenha por qualquer uma dessas, existe rota, e o interruptor é o mesmo interruptor.

**Verificado em jogo de verdade, em hardware de verdade** — reportado pelos logs da própria
cadeia, não a olho:

- 🧨 **Just Cause 2** — DirectX 10.1, 32 bits
- 🏫 **Bully: Scholarship Edition** — DirectX 9, 32 bits
- 🧟 **Resident Evil Revelations 2** — DirectX 9, 32 bits (DXVK)
- 🔫 **Saints Row 2** — DirectX 9, 32 bits (dgVoodoo2)
- 🐒 **ENSLAVED: Odyssey to the West** — DirectX 9, 32 bits
- 🎲 **Baldur's Gate 3** — Vulkan e DirectX 11, os dois instalados
- 😈 **DOOM Eternal** — Vulkan, 64 bits

> ⚠️ **Limites, ditos com honestidade.** Sem tensor core, sem Neural Rendering. Jogo online ou
> com anti-cheat: não — o rebuild do runtime altera um arquivo que o anti-cheat pode conferir, e o
> launcher avisa quando encontra um. Um tradutor pode derrubar um jogo específico; por isso são
> dois, e trocar é um clique. Jogo sem DLSS roda o passe em DLAA, na resolução cheia: **a imagem
> melhora, o FPS não sobe.**

---

## 🔗 Nada falha em silêncio

Todo elo quebrado produz o mesmo sintoma de fora, então o launcher se recusa a escondê-los. A
cadeia é desenhada elo por elo — ReShade, add-on, runtime neural, Ray Reconstruction, carga
antecipada, o interruptor, e a ponte, o Feeder ou o tradutor quando o jogo precisa de um. Verde é
presente. A instalação só está pronta quando todos estão.

O `--check` imprime o plano antes de escrever um byte. Bitness, API, tradutor, por onde o ReShade
entra, se precisa de processo auxiliar — tudo decidido a partir do executável, antes dos 158 MB:

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

`verify <jogo>` lê o ReShade.log depois que você jogou e diz se o add-on carregou mesmo. O
**scanner de conflitos** lê o que *mais* está na pasta do jogo — OptiScaler, Special K, fakenvapi,
um ReShade instalado à mão, um dgVoodoo de tutorial de 2019 — identifica cada um pela informação
de versão, e diz qual está sentado na vaga que a cadeia precisa.

---

## 🧪 Para quem é técnico: o que nenhum outro instalador faz

### 1. 🧬 DLSS dentro de um jogo de 32 bits

DLSS, NGX e o add-on neural são só x64. Um processo de 32 bits **não consegue carregá-los, ponto**
— sem wrapper, sem truque. A rota: um add-on de ReShade minúsculo, de 32 bits, vive dentro do jogo
e captura cor, profundidade e motion vectors; um processo auxiliar separado, de verdade 64 bits
(`dlss5-feed-host64.exe`), abre o próprio device D3D12 e roda DLAA mais o passe neural lá. As
texturas atravessam a fronteira entre processos como handles NT compartilhados, sincronizados por
fence. **Nada encosta na memória do sistema.**

Isso é construído sobre a divisão do Feeder 0.6 do jlrouzies-fr — mas o add-on oficial de 32 bits
aceita só D3D11 (`only Direct3D 11 games are supported by the 32-bit add-on`, literal no fonte),
o que fecha a porta para todo jogo DX9 que o DXVK renderiza, porque o DXVK entrega Vulkan. Este
launcher traz um build com **transporte Vulkan somado**, no mesmo desenho que o add-on de 64 bits
já usava: o host cria as texturas em D3D12 e o jogo as importa com
`VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT`. A direção importa — um recurso criado pelo
Vulkan não pode ser aberto pelo `OpenSharedHandle` do D3D12. As duas metades têm 124 KB,
construídas do fonte MIT do Feeder, embutidas porque não existem em release público nenhum.

### 2. 🔁 DirectX 9 — dois tradutores, medidos por jogo

O ReShade em D3D9 para no Shader Model 3: nenhum provedor de motion vectors compila, e a API não
tem handle compartilhado nem fence. Então o jogo é traduzido antes. O **DXVK** (D3D9 → Vulkan) é o
padrão, e o motivo de DX9 funcionar aqui: em Vulkan o ReShade compila compute shader, que é do que
o provedor de motion vectors precisa. O **dgVoodoo2** (D3D9 → D3D11) cobre os jogos que o DXVK
derruba.

Medido numa máquina, mesmo add-on, mesmo runtime:

| Jogo | dgVoodoo2 | DXVK |
| --- | --- | --- |
| Resident Evil Revelations 2 | crash `0xc0000005` antes do menu | **roda** — 1800 frames avaliados, 64 fps |
| Saints Row 2 | **roda** — estável | crash `0xc0000005` aos ~25 s, com o DLSS já avaliando |

Os conjuntos não se contêm, então a escolha fica na interface, lembrada por jogo. E o dgVoodoo2 é
fixado na 2.83.2, de um mirror de preservação: as 2.87.x dão access violation em Blackwell dentro
do `Direct3DCreate9Ex`, reproduzido com um programa de 40 linhas fora de qualquer jogo.

### 3. 🆕 DirectX 10 — a API que ninguém cobria

Nada na cadeia fala D3D10. O README do Feeder diz `D3D10 is not supported` em uma linha; o
dgVoodoo2 entra como `D3D9.dll` e nunca vê um `D3D10CreateDevice1`; o add-on neural é x64. Até a
v1.69 este launcher recusava esses jogos de cara, e o *Just Cause 2* foi o motivo.

O DXVK também traduz D3D10 — mas **não na versão atual**, e isso foi medido, três vezes, no mesmo
jogo com a mesma cadeia:

| DXVK | O que traz para D3D10 | Resultado |
| --- | --- | --- |
| 3.1 (atual) | `d3d10core.dll` + `d3d11.dll` + `dxgi.dll` | roda sem ReShade; **morre 3 s depois de abrir com ReShade**, com ou sem Feeder |
| wrappers da 1.10.3 sobre o core da 3.1 | `d3d10.dll` + `d3d10_1.dll` antigos | sai limpo em 2 s |
| **1.10.3, os cinco arquivos** | `d3d10.dll` e `d3d10_1.dll` próprios | **roda, com o passe neural avaliando a cada quadro** |

O ReShade.log explica a primeira linha: desde a 2.0 o DXVK não traz `d3d10.dll`/`d3d10_1.dll`
próprios, então o jogo carrega os **do Windows** — e o ReShade, presente pela camada Vulkan,
instala os delayed hooks neles e envolve o device D3D10 do DXVK num wrapper próprio. O processo
cai logo depois. Na rota DX9 isso nunca acontece: o `d3d9.dll` carregado é o local do DXVK, e o
hook na cópia do sistema fica "Delayed" para sempre. A 1.10.3 é a última release com os dois
wrappers; com eles na pasta do jogo, o Windows nunca entra e só existe o runtime Vulkan.

### 4. 🧵 Jogo sem DLSS — fabricando o contrato

O passe neural lê os buffers que o jogo entrega ao DLSS. Um jogo de 2008 não entrega nada. O
Feeder fabrica o contrato: um compute shader do ReShade (LumeniteFX Kernel, provedor 3) produz
motion vectors e profundidade, o add-on abre um device D3D12 privado, e o DLAA roda sobre dado de
verdade. O launcher cuida de cada detalhe que o Feeder deixa para o usuário — o define de
compilação `DLSS5_MV_PROVIDER` (sem ele o shader compila com provedor 0 e o passe roda cego, com
log limpo), a ordem das técnicas no preset (o provedor tem de escrever antes de o Feed ler, no
mesmo quadro), os includes de shader que o instalador do ReShade nunca copia, a textura de blue
noise sem a qual o provedor produz nada em silêncio, o add-on Generic Depth que o ReShade entrega
desligado, e `warmup_rebuild=0` para motor que fecha o pool de memória na largada não receber uma
segunda alocação no pior momento.

Duas lições custaram crash de verdade e estão gravadas: `create_delay` nunca é zerado (o add-on
arma os hooks de NGX de forma assíncrona, e chamar cedo demais mata o *Final Fantasy XV* em todo
carregamento de save), e um runtime de DLSS 1.x nunca é substituído (a geração 1.0 é outra API;
trocar a DLL faz o jogo chamar uma implementação que não responde, e ele morre sem exceção no
Event Log).

### 5. 🔏 Toda RTX, sem confiar em binário aleatório

O modelo original da NVIDIA traz kernels `sm_120` e roda só em Blackwell. Para RTX 20/30/40
existem os rebuilds `.SF` — binários patcheados, o que significa assinatura Authenticode quebrada.
O launcher não os aceita de qualquer jeito: um rebuild da comunidade é instalado **só** quando a
origem é o repositório do RHI que o índice já fixa **e** o SHA-256 bate com um valor conferido à
mão e escrito no código. Qualquer outro é recusado pelo nome da versão. Em Blackwell, só o build
assinado da NVIDIA é usado.

A mesma regra em tudo. O ReShade é conferido contra um certificado fixado antes de ser extraído (e
o ZIP anexado ao setup dele está coberto por essa assinatura — um byte alterado lá dentro muda o
status para HashMismatch). Toda outra peça vem do projeto que a fez, na hora da instalação, por
HTTPS para uma lista fechada de hosts. O que viaja dentro do exe — o add-on neural de 1,7 MB e o
par de 124 KB das metades com transporte Vulkan — é conferido por SHA-256 ao sair de lá.

### 6. 🔍 A cadeia é visível, o plano é imprimível, as decisões são testáveis

O dry-run decide bitness, API e tradutor a partir do executável antes de baixar qualquer coisa. O
`tests/ChainProbe` roda exatamente a lógica de cadeia que a interface roda contra uma pasta de
jogo de verdade e imprime elo por elo — útil quando o interruptor diz "instalar" num jogo que já
está funcionando. O smoke test monta executáveis falsos que importam `d3d10.dll` ou `d3d11.dll` e
confere as decisões de rota contra eles. A camada Vulkan do ReShade é registrada uma vez, global,
com um nome próprio — o *DOOM Eternal* tem lista negra de camadas Vulkan por nome, e "ReShade"
está nela. Uma pasta com um executável por API recebe as duas rotas e uma marca dizendo isso, para
o scanner de conflitos não acusar o próprio trabalho como conflito.

Nada disso é esperteza por esperteza. Cada item é uma falha que produziu o mesmo "nada acontece"
silencioso, encontrada num log, e fechada.

---

## 📦 O que é instalado

Tudo vem dos projetos que fizeram cada peça, na hora da instalação:

| Peça | De |
| --- | --- |
| ReShade com suporte a add-on | [reshade.me](https://reshade.me) |
| `renodx-dlss5.addon64`, `nvngx_dlssnr.dll`, runtimes de DLSS | [RankFTW/RHI](https://github.com/RankFTW/RHI) |
| `DLSS5_Feed.fx` + o add-on de 64 bits | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) |
| motion vectors (`lumenite_*`) | [umar-afzaal/LumeniteFX](https://github.com/umar-afzaal/LumeniteFX) |
| DXVK (atual para DX9, 1.10.3 para DX10) | [doitsujin/dxvk](https://github.com/doitsujin/dxvk) |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) |
| OptiScaler | [optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler) |

Duas coisas viajam dentro do exe: o **add-on neural** (1,7 MB, build 4.70), para não haver nada a
baixar nem arquivo a largar em pasta — a URL de onde ele vinha respondeu 404 quando aquele release
trocou de asset, e o arquivo não tem casa estável em lugar nenhum — e o par de metades de 32 bits
com o transporte Vulkan (124 KB), construído do fonte MIT do Feeder, que não existem em release
público. Um add-on mais novo que você já tenha continua ganhando do embutido.

## 🧰 Também faz

O launcher nasceu como gerenciador dos mods HDR do [RenoDX](https://github.com/clshortfuse/renodx)
e continua fazendo tudo aquilo: instalar e desligar mod por jogo, ajustar os nits de HDR pela
interface sem abrir o jogo, trocar runtimes de DLSS com backup, checar atualização de mod, e
montar a lista de jogos a partir das suas lojas.

## 🖥️ Linha de comando

```
list                    jogos detectados e o estado de cada um
dlss5 <jogo>            instala DLSS 5      (--dgvoodoo · --check)
dlss5 --all             todos os jogos elegíveis
verify <jogo>           lê o ReShade.log e diz se o mod carregou
settings <jogo>         configurações do mod no ReShade.ini
set <jogo> chave=valor  grava configurações (feche o jogo antes)
doctor                  checagem do ambiente
```

## 🧱 Compilar

```
git clone https://github.com/xdzleo/dlss5-launcher
cd dlss5-launcher
dotnet build src/RenoDXLauncher.csproj -c Release
```

SDK do .NET 10, Windows. O `tools/gen_resx.ps1` regenera os recursos de string a partir do
`src/Localization/strings.json`; o `tools/gen_icon.ps1` regenera o ícone.

Testes: `dotnet run --project tests/SmokeTest` cobre instalar/remover contra uma pasta de jogo
falsa. O `tests/ChainProbe` roda a lógica real da cadeia contra uma pasta de jogo de verdade e
imprime elo por elo.

## 🌍 Traduzir

As strings ficam em `src/Localization/strings.json`, uma entrada por chave com todos os idiomas
lado a lado. Acrescente a tag do idioma lá, rode `tools/gen_resx.ps1`, e adicione a tag ao
`<SatelliteResourceLanguages>` no csproj.

## 🙏 Créditos

- [clshortfuse](https://github.com/clshortfuse) — RenoDX, e o add-on neural sobre o qual isto é construído
- [jlrouzies-fr](https://github.com/jlrouzies-fr) — DLSS5-Feeder, que torna possível jogo sem DLSS
- [umar-afzaal](https://github.com/umar-afzaal) — LumeniteFX, o provedor de motion vectors
- [crosire](https://github.com/crosire) — ReShade
- [Dege](https://github.com/dege-diosg) — dgVoodoo2 · [doitsujin](https://github.com/doitsujin) — DXVK
- [RankFTW](https://github.com/RankFTW) — RHI, o índice de runtimes

## 📜 Licença

MIT — veja [LICENSE](LICENSE). Cobre o código do launcher; tudo o que ele baixa mantém a licença
própria, e o add-on neural é closed-source, distribuído pela comunidade.
