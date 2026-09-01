# Changelog

## v1.66.0

O Hitman: Blood Money aparecia com a cadeia inteira verde e "instalado". Não havia `d3d9.dll`
nenhum na pasta, o proxy era um `dxgi.dll` que um jogo D3D9 nunca carrega, e nada rodava.

### Executáveis empacotados liam como "sem API gráfica"

`HitmanBloodMoney.exe` importa exatamente **uma** DLL: `kernel32.dll`. Isso não é um jogo sem API
gráfica — é a assinatura de um protetor de 2006 (SecuROM, SafeDisc) que remonta a tabela de imports
em tempo de execução. Nenhuma varredura estática acha D3D9 ali, nem no import nem nas strings,
porque o binário está comprimido.

O estrago era silencioso e encadeado:

1. sem sinal de D3D9, `DgVoodooService.Applies` respondia não
2. sem sinal de nada, `ReachesD3D12` respondia **sim** — o padrão permissivo do silêncio
3. o jogo virava "alcança D3D12", e o launcher instalava o Feeder **sem tradutor**
4. e ainda deixava um proxy `dxgi.dll` que aquele processo nunca abre

Quando o próprio binário não pode falar, a pasta fala. Duas evidências, as duas fortes: `d3dx9_27.dll`
distribuído junto (a D3DX9 é a biblioteca auxiliar do D3D9 e de mais nada), e o `configure.exe` ao
lado, sem empacotamento, importando `d3d9.dll` de forma limpa — um utilitário que abre um device
D3D9 para enumerar adaptadores só existe num jogo D3D9.

A regra é estreita de propósito: só vale quando o executável está **empacotado** (tabela de imports
degenerada, uma ou duas DLLs). Fora disso, a leitura normal decide. Das 42 pastas testadas, os
únicos jogos detectados como D3D9 continuam sendo os genuinamente antigos de 32 bits — nenhum
título moderno foi arrastado junto.

### A cadeia não checava o tradutor

Esse é o motivo de a tela poder dizer "instalado" sobre uma instalação que não roda. Sem tradutor
não há o que enganchar: o ReShade em D3D9 puro para no Shader Model 3 e nenhum provedor de motion
vectors compila; e a API não tem handle compartilhado nem fence, que é por onde as texturas chegam
ao device D3D12 do passe.

Agora há um elo **Tradutor D3D9**, mostrado só nos jogos que precisam de um. O Hitman: Blood Money
passa a acusar vermelho ali — reinstalar resolve.

## v1.65.0

Uma tela que pedia escolhas demais, e uma delas era inventada.

### O painel de controle do dgVoodoo virou uma "escolha de API"

Na Bayonetta, o cartão de API gráfica listava `dgVoodooCpl.exe` como se fosse uma alternativa —
rotulado **DX12**, ao lado do jogo em DX9. `dgVoodooCpl.exe` é o painel de configuração do próprio
dgVoodoo, um arquivo que *nós* colocamos ali.

A causa é uma função permissiva usada para a coisa errada: `ReachesD3D12` responde "sim" quando o
binário não menciona API nenhuma. Isso é **certo para rotear** — não barrar um jogo sem base — e
errado para escrever na tela. Agora exibir exige evidência positiva, e o que não dá sinal de
renderizar em nada simplesmente não aparece.

### O cartão de escolha de API saiu

Ele empurrava uma decisão técnica para quem só quer o DLSS 5 funcionando. No lugar dele, o
instalador cobre **todas** as APIs da pasta de uma vez.

Isso é possível porque os dois caminhos não disputam arquivo nenhum: a Ponte é um addon próprio
(`dlss5-dx11-bridge.addon64`), e o Feeder é o addon neural sob outro nome mais os shaders em
`reshade-shaders`. Qual deles trabalha é decidido em tempo de execução — num processo Vulkan a
Ponte não encontra device D3D11 e fica quieta.

A evidência de que um addon fora do seu contexto é inofensivo estava na própria pasta do Baldur's
Gate: o addon neural já convivia ali com a rota Feeder, em Vulkan, funcionando.

Uma marca (`.dlss5-multi-api`) registra que a convivência é intencional, para o scanner de
conflitos não acusar como defeito a instalação que a tela acabou de fazer.

Das 42 pastas testadas, só o Baldur's Gate 3 muda de comportamento — é o único com um executável
por API.

### O tradutor de D3D9 virou chave

Era uma caixa de seleção com dois itens, o que obriga a abrir e ler para descobrir que só existe uma
outra opção. Agora os dois lados ficam visíveis e a troca é um clique — o gesto certo para "se
crashar com um, tente o outro".

É a **única** escolha que continua na tela, e por um motivo: não há resposta certa. O Resident Evil
Revelations 2 só roda com DXVK; o Saints Row 2 só roda com dgVoodoo2. Não dá para deduzir qual serve
sem abrir o jogo.

## v1.64.0

O Baldur's Gate 3 não ligava, e a causa não era o Baldur's Gate 3.

### A regra de escolha de caminho existia em duas cópias

O launcher decide entre três caminhos — Ponte, OptiScaler e Feeder — conforme o jogo ter DLSS
próprio e alcançar D3D12. Essa decisão estava escrita **duas vezes**: uma no instalador, outra na
tela de detalhe. E as duas saíram do lugar.

| | instalador | tela |
|---|---|---|
| pede a Ponte quando | `temDlss && !alcançaD3D12 && !éVulkan` | `temDlss && !alcançaD3D12` |

O Baldur's Gate 3 é Vulkan. O instalador mandou para o Feeder de propósito: o addon engancha
`NVSDK_NGX_D3D12_EvaluateFeature_C`, e um jogo Vulkan chama a família `NVSDK_NGX_VULKAN_*`, que ele
não procura. A tela, sem o termo de Vulkan, cobrava a Ponte de um jogo cujo Feeder estava instalado
e funcionando — elo vermelho permanente, interruptor travado, e nada que o usuário pudesse clicar.

Agora existe uma função só, `Dlss5Installer.Rotear`, chamada pelo instalador, pela tela e pela
sonda de testes. Duas cópias de uma regra de três termos só vão divergir de novo.

Junto: o que **já está instalado** passa a ter precedência sobre o que a regra escolheria hoje.
Trocar de caminho é uma reinstalação, não um elo faltando — e a resposta pode mudar entre uma
instalação e a seguinte, quando a detecção é corrigida ou o jogo é atualizado.

### O aviso "sem DLSS nativo" aparecia em jogo com DLSS nativo

Ele estava preso a "o Feeder está instalado", e o texto fala de outra coisa: que os motion vectors
são estimados por shader porque o jogo não os fornece. Num jogo que tem DLSS e ficou com o Feeder,
o aviso contradizia a própria tela logo acima. Agora ele segue a ausência de DLSS, que é a condição
que ele descreve.

### O que MAIS está na pasta do jogo

Uma pasta de jogo não é nossa. Antes de o launcher chegar nela já passaram OptiScaler, fakenvapi,
dlssg-to-fsr3, Special K, um ReShade instalado à mão, um dgVoodoo de um tutorial de 2019 — e cada
um ocupa exatamente os mesmos slots de que precisamos.

O `bin` do Baldur's Gate 3 tinha **quatro** empilhados, com o OptiScaler sentado num `dxgi.dll` de
25 MB. O resultado não era um erro na tela: era o caminho DX11 simplesmente não carregando.

O cartão novo diz o que está ali, o que colide com o quê, e deixa a decisão com o usuário — parte
dessas ferramentas ele pode querer manter (o dlssg-to-fsr3 é geração de quadros, e não concorre com
o passe neural). Nada é apagado: o que sai ganha um sufixo reversível.

Os graus são conservadores de propósito. A primeira versão desta varredura acusava o **nosso
próprio ReShade** em 38 das 42 pastas testadas, e o teste de carga dupla disparava em 34 — a camada
Vulkan é registrada num caminho global, então `IsRegistered` responde "sim" para qualquer pasta.
Corrigidos contra a varredura real, sobrou 1 bloqueio provado (Bayonetta, com DXVK e dgVoodoo2
disputando o mesmo `d3d9.dll`) e 5 avisos.

### Escolher a API gráfica

Um jogo pode ter um executável por API na mesma pasta, e a rota do DLSS 5 muda com a escolha: o
`bg3.exe` é Vulkan e vai para o Feeder; o `bg3_dx11.exe` importa `d3d11.dll` e vai para a Ponte. Até
agora o launcher escolhia sozinho, pelo maior executável, e não dizia qual tinha escolhido.

O cartão só aparece quando há mais de uma API — o DOOM Eternal, que é só Vulkan, não mostra nada.

Instalar nas duas ao mesmo tempo não é possível hoje, e o motivo não é o que parece: os dois
executáveis entram por portas diferentes, então não haveria carga dupla do ReShade. O obstáculo é
que os dois addons ficariam na mesma pasta, sob um `ReShade.ini` só, e o addon da Ponte entraria
também no processo Vulkan — que é exatamente o caso do `Failed to allocate video memory` numa placa
com memória sobrando.

### As bolinhas acendem na abertura

Havia **duas** portas fechadas na varredura de fundo, não uma: ela só rodava em jogo com mod HDR no
catálogo, e só avisava a interface quando encontrava alguma coisa. O DLSS 5 não depende do mod HDR —
é a razão de existirem duas bolinhas — então a maioria da lista nascia vermelha até o clique
disparar a releitura por outro caminho.

Vermelho por não ter sido lido e vermelho por estar desligado são a mesma cor na tela, e foi por
isso que o defeito passou tanto tempo parecendo cosmético.

Medido, não deduzido: 59 jogos varridos em 183 ms, 49 bolinhas verdes antes de qualquer clique.

## v1.59.0

Quatro ideias vindas do [DLSS5oneclick](https://github.com/faisalkindi/DLSS5oneclick), depois de
ler o código dele. Nenhuma é código copiado — são decisões boas que o nosso não tomava.

### RTX 20, 30 e 40 deixam de ser recusadas

O modelo de Neural Rendering original é FP8 com kernels `sm_120`, que só existem em Blackwell — e
por isso o launcher recusava tudo abaixo de RTX 50, mandando o usuário achar um build alternativo
por conta própria.

Só que esse build **já estava no manifesto do RHI que o launcher consulta**: os `.SF`, do
ShortFuse, que acrescentam binários patcheados para RTX 40 e um caminho FP16 para RTX 20/30. A
ordenação nunca os escolhia porque `310.8.SF-v2` não é uma versão parseável — `Version.TryParse`
falhava, a entrada caía para `0.0` e perdia para `310.8.0`. O launcher baixava 158 MB do build que
a placa do usuário não roda.

Agora o índice escolhe pela GPU: em Blackwell, o mais novo; fora dela, um `.SF`. A recusa fica só
para o que realmente não tem como rodar — placa não-NVIDIA (não existe NGX) e NVIDIA sem tensor
core (GTX/GT/MX). A interface passa a dizer o custo esperado (`RTX 50` · `RTX 40` · `RTX 20/30`),
porque o preço do passe sobe bastante fora do Blackwell e "instalei e ficou lento" é uma conclusão
fácil de tirar sem esse aviso.

### O `HTTP 403` que ainda não te aconteceu

A API do GitHub permite **60 requisições por hora por IP** quando anônima. O launcher consultava
releases pela API em quatro serviços; quem instala em muitos jogos estoura a cota e passa a
receber `403 Forbidden` em tudo — sem nenhuma pista de que a causa é cota, não rede.

As páginas públicas de release não têm essa cota:

```
github.com/{repo}/releases/latest                 -> 302 para .../tag/{versão}
github.com/{repo}/releases/expanded_assets/{tag}  -> HTML com os links
```

`GitHubReleaseService` passa a resolver assets por aí. Um `GITHUB_TOKEN` no ambiente ainda vale a
pena (5000/hora) e é tentado primeiro; a página é o caminho normal, não o remendo.

### `--check`: o que seria feito, sem escrever nada

```
dlss5 "Bully" --check

  arquitetura    : 32 bits
  gpu            : NVIDIA GeForce RTX 5090   (custo do pass: RTX 50)
  DLSS proprio   : nao
  tradutor DX9   : DXVK (Vulkan)
  ReShade entra  : camada Vulkan
  metades 32 bits: com transporte Vulkan
  processo extra : host64 (o DLSS e x64; um jogo de 32 bits nao o carrega)
  (nada foi escrito — isto e so o plano)
```

Achou um bug na primeira execução: o Saints Row 2 aparecia com DXVK apesar de estar na lista de
exceções, porque o executável detectado é `sr2_pc_unpatched.exe` e a lista tinha só `sr2_pc.exe`.
Passou a casar por prefixo.

### Segunda engine para jogo com DLSS próprio

`OptiScalerNrService` traz o fork [OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR)
do Dagherbou — OptiScaler com o passe de Neural Rendering embutido, que entra sozinho como
`dxgi.dll`, sem ReShade nem add-on separado. As duas engines não convivem (ambas carregam como
`dxgi.dll`), então a escolha é exclusiva. A remoção é guiada por um manifesto do que foi escrito:
o pacote espalha vários arquivos na raiz do jogo, e adivinhar ali significaria apagar arquivo
alheio.

## v1.58.1

**Uma camada Vulkan por jogo era errado, e o Bully mostrou por quê.** Camada implícita é
**global**: o registro fica em HKLM e o loader a aplica a *todo* aplicativo Vulkan da máquina, não
só ao jogo em cuja pasta o arquivo mora.

Registrar uma por jogo dava, nesta máquina, **cinco entradas com o mesmo nome de camada**
(`VK_LAYER_renodx_neural`). O loader escolhe uma por ordem — e o Bully acabou carregando o
`ReShade32.dll` que estava **dentro da pasta do Resident Evil Revelations 2**:

```
Initializing ReShade (32-bit) loaded from
  D:\...\RESIDENT EVIL REVELATIONS 2\vklayer\ReShade32.dll
into  ...\Bully Scholarship Edition\Bully.exe
```

Funcionava por coincidência, porque é o mesmo binário. Desinstalar aquele jogo levaria os outros
junto, e o comportamento com nomes duplicados não é definido.

Agora a camada vive **uma vez**, na biblioteca do launcher, e as pastas de jogo saem do registro —
inclusive as entradas que versões anteriores deixaram, limpas na primeira instalação. Uma camada
atende todos os jogos porque o ReShade já decide sozinho onde se ativa, pela presença do
`ReShade.ini` ao lado do executável.

Desinstalar de um jogo **não** derruba a camada compartilhada, pelo mesmo motivo: ela serve os
outros, e onde não é usada não custa nada (carrega e sai).

Verificado nos três jogos de 32 bits: cinco registros viraram um, e Bully, ENSLAVED e RE
Revelations 2 seguem com a cadeia completa.

## v1.58.0

**Trocar o tradutor agora troca de verdade.** Antes o seletor só gravava a preferência e pedia
para reinstalar — o que era um beco sem saída: com o DLSS 5 já ligado, o interruptor **remove** em
vez de reinstalar, então não havia caminho pela interface. Agora, se já estiver instalado, trocar
reinstala sozinho.

E reinstalar não é só pôr o novo: é **desfazer o outro**. A troca move quatro peças de uma vez, e
deixar qualquer uma para trás quebra tudo em silêncio.

| | DXVK | dgVoodoo2 |
| --- | --- | --- |
| `d3d9.dll` | DXVK | dgVoodoo2 |
| ReShade | camada Vulkan registrada | proxy `dxgi.dll` |
| `addon32` | build com transporte Vulkan | build oficial (D3D11) |
| camada Vulkan | registrada | removida |

Verificado nos dois sentidos, com o estado do disco lido antes e depois de cada troca.

### O proxy que sobrava era pior que lixo

Ao voltar para o DXVK, o `dxgi.dll` do ReShade continuava na pasta. Isso não é sujeira inofensiva:
**o DXVK usa DXGI por dentro**, então ele carregaria esse proxy — e o ReShade entraria duas vezes
no mesmo processo, uma pela camada e outra pelo proxy. Carga dupla de ReShade é a receita
conhecida de `0xc0000005`.

Agora o proxy é guardado como `.pre-dxvk` ao entrar na rota Vulkan, e devolvido ao voltar para o
dgVoodoo. Nada é apagado: as duas trocas são reversíveis.

## v1.57.2

**O interruptor não ligava em jogo de 32 bits pela rota DXVK** — e desta vez a causa foi
encontrada executando o código, não deduzindo.

A checagem de integridade do Feeder compara o **tamanho** do `dlss5-feed.addon32` instalado com o
da cópia na biblioteca, para detectar arquivo corrompido. Só que na rota DXVK o addon instalado
não é o da biblioteca: é o **embutido, com transporte Vulkan**, que tem mais código e por isso
outro tamanho (56.832 contra 49.664 bytes). A comparação reprovava toda instalação Vulkan como
"Feeder ausente", o elo ficava vermelho, `Dlss5Ready` nunca virava — e o botão seguia dizendo
"instalar" num jogo que já estava rodando DLSS 5.

Agora a checagem aceita as duas origens: a cópia da biblioteca **ou** a embutida.

### Um teste que executa a decisão de verdade

As três correções anteriores desse mesmo interruptor (v1.55.1, v1.57.1) foram feitas olhando
arquivos no disco e raciocinando sobre o código — e as três erraram o elo. Nenhuma rodou a lógica
que a interface roda.

`tests/ChainProbe` agora executa exatamente a sequência de `BuildDlss5Chain` contra uma pasta de
jogo real e imprime elo por elo, com o porquê de cada um:

```
dotnet build tests/ChainProbe && ChainProbe.exe "<pasta do jogo>"

  [OK   ] ReShade    proxy=nenhum  camadaVulkan=True  jogo64=False
  [FALHA] Feeder     pede=True  ativo=False
  Dlss5Ready = False  -> o interruptor continua dizendo 'instalar'
```

Foi ele que apontou o elo certo em trinta segundos, depois de três tentativas erradas.

Verificado nos três jogos de 32 bits desta máquina — ENSLAVED e RE Revelations 2 (rota DXVK) e
Saints Row 2 (rota dgVoodoo, que não podia regredir): todos com `Dlss5Ready = True`.

### O seletor de tradutor mudou de lugar

Saiu de "instruções e detalhes", lá no fim da tela, e passou a viver **dentro do card do DLSS 5**,
logo abaixo dos elos — que é onde a decisão pertence, junto com o interruptor que ela afeta.

Continua sendo uma lista de escolha única, e não duas chaves, porque DXVK e dgVoodoo2 disputam o
mesmo `d3d9.dll`: é um **ou** o outro, nunca os dois. Ao trocar, o que estava lá é guardado com
sufixo `.pre-dxvk` em vez de apagado.

## v1.57.1

**O interruptor não mudava em jogo da rota Vulkan.** Clicar instalava — a instalação ia inteira e
correta para o disco — e a interface continuava mostrando "desligado", então parecia que o botão
não fazia nada.

O elo "ReShade" da cadeia media a presença de um proxy `dxgi.dll` na pasta do jogo. Mas em jogo
Vulkan — nativo, ou D3D9 traduzido pelo DXVK — o ReShade entra como **camada**, e proxy nenhum é
carregado: a ausência do `dxgi.dll` ali é o funcionamento correto, não a falha. O elo ficava
vermelho para sempre, e como `Dlss5Ready` exige a cadeia inteira, o interruptor nunca virava.

Agora o elo aceita as duas formas: proxy **ou** camada Vulkan registrada.

Apareceu no ENSLAVED: Odyssey to the West, um Unreal Engine 3 de 32 bits. A instalação ia toda
para `Binaries\Win32` — DXVK, camada Vulkan, addon32 com transporte Vulkan, `host64` completo — e
a tela mostrava quatro elos vermelhos. É a terceira vez que o mesmo padrão aparece (v1.55.1 foi o
`host64\`), e sempre pela mesma causa: a cadeia media um caminho que aquela rota deliberadamente
não usa.

## v1.57.0

**O tradutor de DirectX 9 virou uma escolha sua, na interface.** Jogo DX9 de 32 bits agora mostra
um seletor entre DXVK (Vulkan) e dgVoodoo2 (D3D11), lembrado por jogo.

A escolha existe porque não há resposta certa, e isso foi medido — não deduzido. Com o mesmo
add-on, o mesmo runtime e a mesma máquina:

| jogo | dgVoodoo2 | DXVK |
| --- | --- | --- |
| Resident Evil Revelations 2 | crash `0xc0000005` antes do menu | **roda** — 1800 frames, 64 fps |
| Saints Row 2 | **roda** — estável | crash `0xc0000005` aos ~25 s |

Os dois crashes são idênticos no sintoma (access violation dentro do `d3d9.dll`), em tradutores
opostos. O caso do Saints Row 2 é o mais traiçoeiro: com DXVK o jogo **sobe**, o DLSS fica pronto
(`feature ready: 1024x768 DLAA`) e o feed entrega 600 frames — e só então o jogo morre. Um teste
de trinta segundos diria que funcionou.

Os conjuntos que cada um cobre não se contêm, e não dá para saber qual serve sem abrir o jogo.
Então quem abre escolhe.

O padrão passou a ser o **DXVK**, por cobrir mais jogos e ser mantido ativamente — com uma lista
de exceções verificadas em jogo (hoje: Saints Row 2), e só entra nela o que foi testado dentro do
jogo, nunca por suposição. Trocar o seletor pede reinstalação, porque muda o `d3d9.dll`, o modo do
ReShade (camada x proxy) e as metades de 32 bits de uma vez.

No CLI a inversão correspondente: `--dxvk` saiu (virou padrão) e entrou `--dgvoodoo`.

## v1.56.0

**Uma segunda rota para jogo Direct3D 9, e com ela jogos que antes não tinham rota nenhuma.**

O caminho DX9 → DLSS 5 sempre precisou de um tradutor: o ReShade em D3D9 para no Shader Model 3,
então nenhum provedor de motion vectors compila. O dgVoodoo2 resolvia isso entregando D3D11 —
quando funciona. Em jogo que ele derruba não havia o que fazer.

E ele derruba jogos que não têm defeito nenhum. O Resident Evil Revelations 2 crasha com
`0xc0000005` dentro do próprio `d3d9.dll` do dgVoodoo, em **toda** configuração testada (VRAM,
`OutputAPI`, `PresentationModel`, `VideoCard`), com o binário de SHA idêntico ao que roda Saints
Row 2 e Bully sem uma queixa. Sem dgVoodoo o jogo abre normal; com ele, morre antes do menu.

O DXVK traduz D3D9 para **Vulkan** em vez de D3D11, e roda esses jogos. Só que isso muda o resto
da cadeia inteira: o ReShade entra como camada Vulkan em vez de proxy `d3d9.dll`, e o add-on
precisa falar Vulkan.

### O add-on de 32 bits agora fala Vulkan

O Feeder oficial recusa, e a linha é literal:

```cpp
if (dev_api->get_api() != reshade::api::device_api::d3d11)
{ FeedDisable("only Direct3D 11 games are supported by the 32-bit add-on"); return; }
```

O launcher passa a embutir duas metades construídas do fonte do Feeder (MIT) com um transporte
Vulkan somado, no mesmo desenho que o add-on de 64 bits já usava: **o host cria as texturas em
D3D12 e o jogo as importa** com `VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT`.

A direção não é escolha. Um recurso criado pelo Vulkan não pode ser aberto pelo
`OpenSharedHandle` do D3D12; o contrário funciona. Num processo de 32 bits o único D3D12 da
máquina está no host, então ele passa a ser quem cria — o protocolo do pipe foi para a v2 para
carregar os handles no sentido inverso.

São 124 KB embutidos em vez de baixados: é um fork, não existe em release público, e um download
a mais é mais uma coisa para falhar offline.

### Como usar

A rota é opcional, e o dgVoodoo2 continua sendo o padrão — ele é o caminho testado em mais jogos,
e o launcher não tem como saber qual dos dois um jogo aceita antes de tentar. Para os que o
dgVoodoo derruba:

```
RenoDXLauncher.exe dlss5 "<jogo>" --dxvk
```

Medido no Resident Evil Revelations 2 (D3D9, 32 bits): `feature ready: 1920x1080 DLAA`, 1800
frames avaliados, 64 fps, e o feed custando 0% do frame. O Lumenite Kernel compila — coisa
impossível em D3D9 puro, e o motivo pelo qual o DXVK destrava a cadeia toda.

### Também nesta versão

O gerador de `.resx` ganhou um equivalente em PowerShell (`tools/gen_resx.ps1`), byte a byte igual
ao de Python. As strings novas saíam cruas na tela porque o JSON é a fonte mas os `.resx` é que
rodam, e a máquina de build não tinha Python — só o alias da Microsoft Store, que apenas abre a
loja.

## v1.55.1

**O interruptor não mudava depois de instalar, em todo jogo de 32 bits.** A instalação funcionava —
o Hitman: Absolution avaliou 7200 frames com DLSS 5 enquanto a interface continuava dizendo
"instalar".

A cadeia de elos media a pasta do jogo, e num jogo de 32 bits o pass neural não roda lá: roda no
`host64\`, e é lá que o addon e os runtimes moram. O próprio `DeployBits32Async` os tira da raiz de
propósito, porque são 271 MB que um processo de 32 bits nunca carregaria. Três elos — `addon`,
`neural` e `carga antecipada` — ficavam vermelhos para sempre, `Dlss5Ready` nunca virava true, e o
botão continuava oferecendo instalar o que já estava instalado e rodando.

Agora os três procuram também em `host64\`, e o elo de carga antecipada lê o `ReShade.ini` de lá —
o da raiz nunca lista carga antecipada, porque o processo do jogo não carrega addon de 64 bits.

**A recusa do Direct3D 10 dizia a coisa errada.** Um jogo D3D10 caía na mensagem genérica "não traz
runtime de DLSS", que soa como arquivo faltando e manda o usuário procurar um download que não
existe. Aqui não falta nada: o Feeder diz "D3D10 is not supported" em uma linha, o dgVoodoo entra
como `D3D9.dll` e nunca vê um `D3D10CreateDevice1`, e o addon de NR é x64, fora de alcance de um
processo de 32 bits. A string específica já existia no projeto e nunca era usada — agora é.

Foi o Just Cause 2 que expôs as duas: ele é D3D10, e o Hitman: Absolution — D3D11 de 32 bits, o
caso que funciona — expôs o interruptor travado.

## v1.55.0

Quatro correções, todas vindas de jogo real. A primeira é a que mais importa: havia uma classe
inteira de instalação que terminava com tudo no lugar e o recurso desligado.

### O addon trocou de contrato, e o launcher não sabia

O build do addon da comunidade que adicionou DX9/DX11/DX12 **trocou o esquema de configuração
inteiro**. Onde antes se lia `[RenoDX.DLSS5]` com a chave `NeuralUplift`, agora se lê
`[RENODX-DLSS]` com chaves `DirectNeuralRendering*` — e a versão nova não consulta mais a antiga.

O efeito era silencioso do pior jeito: o addon ia para a pasta, o runtime de 158 MB ia junto, o ini
era gravado, a instalação terminava sem erro nenhum — e o addon subia desligado, porque a única
chave que ele lê não existia. Nada na tela, nada no log, nada para o usuário desconfiar.

Agora as duas seções são escritas, e `IsApplied` consulta a nova primeiro. A versão do addon que
cada usuário tem é desconhecida, e uma chave a mais num ini que ninguém lê não custa nada.

O interruptor também deixou de estar espalhado por cinco pontos do arquivo e passou a viver em uma
função só. Foi justamente o espalhamento que deixou o contrato novo passar batido: cada esquema
novo exigia achar os cinco lugares de novo.

### DLSS 1.0 não é DLSS antigo, é outra API

A geração 1.0 não usa motion vectors, tem modelo treinado por jogo e um contrato de chamada
distinto. Trocar essa DLL por uma 310.x não atualiza nada: **desliga** o DLSS do jogo, porque a
implementação nova não atende as chamadas que ele faz.

A comparação de versão sozinha não protegia — 1.0.11 é menor que 310.8, então a troca parecia um
upgrade óbvio. Aconteceu no Final Fantasy XV, e o sintoma era o jogo fechar ao terminar de carregar
o save, sem exceção no Event Log e sem breadcrumb: não há código quebrado, é uma chamada válida a
uma DLL que não a responde.

Três guardas agora: `DlssRuntimeService.Apply` pula runtime 1.x, `NeuralUpliftService.Detect` não
conta 1.x como "tem DLSS", e o caminho do Feeder se recusa a sobrescrever uma runtime 1.x — esta
última porque o Feeder chegava por trás e recolocava a 310.x, desfazendo a decisão sem avisar.

Entre quebrar um jogo que funciona e não instalar um recurso, não instalar ganha.

### `create_delay` volta a ser respeitado

A tentação era zerá-lo: o Feeder passa a alocar no primeiro quadro, antes de o motor fechar o pool,
e o "Failed to allocate video memory" do DOOM Eternal some. Mas o README do Feeder é explícito
sobre o que esse atraso protege — o addon arma os hooks NGX de forma assíncrona, e chamar cedo
demais **trava**. Carregar um save é um re-init de runtime; com o atraso zerado, o Final Fantasy XV
fechava toda vez que terminava de carregar.

Trocar um crash garantido por um problema de memória que só aparece em motor que reserva o pool
inteiro na largada é mau negócio. Agora só o `warmup_rebuild` sai — ele reconstrói a feature lá
pelo frame 180 para contornar um problema que as builds "v45+" do addon não têm mais.

### O addon era copiado, não movido

`GarantirNomeDoFeeder` copiava o addon em vez de mover. O resultado era o mesmo binário carregado
duas vezes — uma pelo early-load e outra pela varredura de diretório — e carga dupla de addon é
`0xc0000005` na certa. Agora move, e corrige a entrada de early-load junto.

## v1.37.0

Caçada a bugs com dois revisores independentes, um na camada de serviços e outro na de interface.
Dezoito achados; onze corrigidos aqui. Vários eram regressões introduzidas nas versões 1.21–1.35,
o que era de esperar: aquela sequência mexeu em quase tudo que escreve arquivo dentro de pasta de
jogo.

### Dois que destruíam arquivo do usuário

**`Remove` apagava o `nvngx_dlssnr.dll` mesmo sem tê-lo instalado.** O comentário dizia "só o que
este launcher colocou aqui" e o código não cumpria: o runtime era apagado incondicionalmente. Como
o próprio `AutoDiscoverRuntime` documenta, as pastas que já contêm esse arquivo são exatamente onde
vivem as únicas cópias que existem numa máquina — e a NVIDIA não o distribui em driver nem em SDK.
Desligar a feature numa dessas pastas apagava 158 MB irrecuperáveis.

Agora o launcher marca o que ele mesmo escreveu e só apaga isso; substituir uma cópia alheia passa
a fazer backup antes, como todo outro runtime.

**`Restore` e `RestoreAll` varriam `*.renodx-bak`, incluindo o backup do addon.** O `IsApplied`
tinha sido estreitado para contar só backup de runtime; os dois caminhos de reversão não. Reverter
o runtime de um jogo revertia o addon de DLSS 5 junto, para um build antigo, sem ninguém pedir. No
`RestoreAll` era pior: ele **apaga o backup depois de restaurar**, então destruía a única cópia do
build substituído.

### O "instala e não funciona"

`AddonSupportsNr` significava duas coisas ao mesmo tempo: "o mod do jogo sabe acionar NR" e "existe
algum addon genérico nesta pasta". Com as duas grudadas, uma pasta que **já tinha** o addon genérico
era tratada como "o mod próprio dirige", e a chave mestra ia para `[renodx-preset1]` — seção que o
addon genérico não lê.

Encontrado no disco: um jogo com `[RenoDX.DLSS5] NeuralUplift=0` e `[renodx-preset1]
NeuralUplift=1.000000`. Addon carregado, desligado, e o "ligado" do launcher onde ninguém consulta.
Instalar reportava falha depois de fazer todo o trabalho.

A separação em duas perguntas não bastou na primeira tentativa: `AddonService.GetState` devolve
qualquer `renodx-*.addon64` da pasta, e o genérico casa com esse padrão — então "esse arquivo tem o
marcador de NR?" respondia sim para ele mesmo. Agora o addon do jogo não pode ser um dos genéricos.

### O resto das correções

- **Instalação parcial sem retorno.** `NeuralUpliftService.Apply` era chamado fora de `try` dentro
  do instalador. Uma exceção ali descartava a lista de passos e deixava a pasta com o conjunto
  Streamline já trocado e o proxy do ReShade já instalado, para uma feature que não foi instalada.
- **O runtime de Ray Reconstruction nunca era atualizado depois da primeira vez.** O backup usava
  `overwrite: false`, que lança quando o backup já existe; o `catch` externo engolia a exceção e
  levava junto a cópia da linha seguinte. Único sinal: um `Log.Warn`.
- **158 MB rebaixados a cada tentativa.** A checagem de "já descompactado" olhava só o nível
  superior, enquanto o consumidor procura recursivamente.
- **O interruptor mostrava estado velho.** `RaiseModState` era chamado em 2 dos 10 caminhos que
  mudam o estado do addon — o botão de energia da barra inferior desativava o mod e deixava o
  interruptor acima dele dizendo LIGADO. Centralizado no `RaiseCommands()`.
- **O interruptor ficava na posição errada quando a instalação falhava.** Um `ToggleButton` move a
  si mesmo ao ser clicado; só uma notificação da origem o traz de volta, e a notificação era
  suprimida por igualdade quando o valor recalculado era o mesmo. Como o modal é reaproveitado, a
  posição errada acompanhava o próximo jogo aberto.

### Achados registrados e ainda abertos

- Um `Repair` disparado por "arquivo não assinado" faz troca completa de Frame Generation em todas
  as pastas. Efeito medido: nove arquivos Streamline **criados** na raiz do The Witcher 3, sem
  backup, numa pasta que nunca teve conjunto — permanentes.
- `AutoDiscoverStreamlineSet` não aplica a guarda de pré-release que o caminho por arquivo aplica.
- O botão Reparo liga em `InstallCommand`, desabilitado justamente nos jogos que alcançam o estado
  que ele existe para consertar.
- O interruptor do mod perdeu a guarda de `DownloadUrl`: num mod só-Nexus ele instala o ReShade e
  só então falha, em vez de apontar a página.
- Os pré-requisitos passaram a renderizar abaixo do controle de instalar, reintroduzindo o problema
  que o comentário no código descreve.
- Propriedades calculadas com `L.T(...)` não notificam na troca de idioma.
- Aplicar perfil de monitor num jogo já instalado ficou sem caminho na interface.

## v1.28.0

### Faltava o runtime de Ray Reconstruction ao lado do addon

O controle de denoiser do addon oferece Ray Reconstruction, e RR é um runtime **diferente** do
Super Resolution — `nvngx_dlssd.dll`. O guia da comunidade lista os três como necessários
(`nvngx_dlss`, `nvngx_dlssd`, `nvngx_dlssnr`); o launcher só instalava o neural. A opção ficava na
tela e morta.

Medido nesta máquina: **oito de onze** jogos com o addon não tinham `nvngx_dlssd.dll` ao lado dele.
Ter o arquivo em outro lugar da instalação não conta — o addon carrega pelo nome, da pasta do
executável, o mesmo lugar onde procura o runtime neural.

Aplicar agora copia o RR junto, com a mesma exigência de assinatura da NVIDIA de qualquer runtime
que este launcher grava. É inerte num jogo que nunca pede RR: o NGX só carrega um runtime quando a
feature é criada.

### Um addon já instalado agora é atualizado

Uma pasta que já tinha o addon contava como "sabe acionar NR", então aplicar pulava a cópia e o
jogo ficava travado no build que chegou primeiro. Ficar uma versão atrás sem meio de avançar é pior
que não ter a função — a 3.3.4 corrigiu um frame totalmente preto em HDR.

A comparação é byte a byte, não por tamanho, e o build anterior fica como `.renodx-bak`.

## v1.25.0

### Instalar um mod apagava o addon neural — em todos os jogos

A regra "um addon RenoDX por pasta" varria `renodx-*.addon*`. Isso não é "um mod de jogo por
pasta", que é o conflito real — dois mods do mesmo jogo brigam pelos mesmos shaders. Ela pegava
junto:

- **os addons companheiros**, que existem justamente para ficar AO LADO do mod do jogo. O
  instalador da comunidade do DLSS 5 deposita o mod do jogo e o addon neural lado a lado, e essa é
  a configuração documentada dele. Instalar um mod apagava o addon neural naquele jogo — e uma
  atualização em lote apagava em todos de uma vez;
- **arquivos `.bak`**, porque `.addon*` casa com `.addon64.bak` também. Um backup do qual uma troca
  depende para ser desfeita era removido por uma instalação que não tinha nada a ver com ele.

Medido no log desta máquina: uma sequência de instalação às 15:45 removeu o addon neural de cinco
jogos (Black Myth, Stellar Blade, PRAGMATA, Red Dead Redemption 2, S.T.A.L.K.E.R. 2), o
`renodx-ue-extended` de dois, e um backup.

Agora a varredura só considera arquivo que termina exatamente em `.addon64`/`.addon32` (com ou sem
`.disabled`), e pula os companheiros conhecidos — neural/dlss5, dlssfix, ue-extended, fpslimiter.
Casados por prefixo, porque esses builds são renomeados a cada versão.

## v1.24.0

### Corrigir escrevia na pasta errada e o diagnóstico continuava vermelho

Um jogo Unreal carrega **dois** conjuntos Streamline: um em `Binaries\Win64`, ao lado do
executável, e outro em `Engine\Plugins\Runtime\Nvidia\Streamline\...\Win64`. A busca pelo destino
parava no primeiro interposer que encontrasse — que costuma ser justamente o que já estava bom. O
conjunto realmente quebrado ficava intocado, e clicar em Corrigir não mudava nada, sem nada no log
dizendo por quê.

Medido no Black Myth: `Binaries\Win64` inteiro em 2.13.0.0, e a pasta do Engine com três arquivos
em 2.13.0.0 e cinco em 2.7.4.0. Agora todas as pastas com interposer são destino, e o log lista
quais são.

O pulo de "arquivo já idêntico" também passou a comparar versão, não só tamanho — duas builds do
mesmo plugin podem ter o mesmo tamanho, e pular ali deixaria exatamente o arquivo divergente que
se veio consertar.

### A mensagem de conjunto incoerente não dizia qual pasta

Ela comparava os dois conjuntos do jogo como se fossem um, e o resultado listava o **mesmo nome de
arquivo nas duas versões** — verdadeiro e inútil, porque não dizia onde mexer.

O que quebra o jogo é a incoerência DENTRO de um conjunto: o interposer e os plugins que ele
carrega têm que vir do mesmo build. A verificação passou a ser por pasta, e a mensagem nomeia a
pasta.

## v1.23.0

### O launcher instala o addon da comunidade, não um nosso

O build próprio foi tentado num jogo e **não é distribuível**: o Black Myth abre em tela preta com
ele. O log mostra onde para — os dois ganchos são instalados, a linha seguinte nunca vem:

    hook: exports do NGX enganchados
    streamline: interposer enganchado
    [fim]

Nenhuma captura, nenhum `init:`. Trava antes de qualquer trabalho por frame, quando só os ganchos
rodaram. E numa execução anterior, com o mesmo código de gancho, o jogo abriu — o que faz disso uma
corrida, não um defeito determinístico: os hooks são instalados de dentro do callback de present,
com `MH_EnableHook(MH_ALL_HOOKS)`, enquanto a thread de render já está dentro de
`slEvaluateFeature`/`slSetTag`. Sobrescrever os primeiros bytes de uma função que outra thread está
executando trava assim.

Um addon que não faz nada com segurança vale mais que um nosso que trava. O launcher passa a buscar
o build da comunidade (`renodx-dlss5-v2.5`), e o binário próprio saiu do instalador.

O arquivo não é assinado por ninguém — não há certificado para conferir — então ele é **fixado por
hash de conteúdo**. Isso é mais forte que uma URL: o host pode mudar, a release pode ser
re-tagueada, e os bytes ainda têm que ser os que isto foi testado contra, ou nada é instalado.

Uma cópia já presente na máquina continua tendo precedência: quem seguiu as instruções do Discord
pode ter uma mais nova que a que sabemos buscar.

## v1.22.0

### O addon agora engancha o Streamline, não só o NGX

Um jogo que usa Streamline chama `slEvaluateFeature`, e o Streamline é quem chama o NGX lá dentro.
Quando essa chamada interna não passa pelo módulo que enganchamos, o gancho de NGX nunca dispara e
o addon fica calado num jogo que tem DLSS rodando na tela. Enganchar o nível do jogo cobre esse
caso — e é a diferença entre "roda em alguns" e "roda nos que têm DLSS".

`slSetTag` / `slSetTagForFrame` / `slEvaluateFeature`, com o SDK público do Streamline vendorizado
em `external/streamline` (o que importa é o LAYOUT das structs que o jogo passa; escrever uma
versão "mínima" à mão é como um ponteiro dentro de `sl::ResourceTag` acaba apontando para o lugar
errado).

A deduplicação com o NGX não é heurística de tempo: o gancho de NGX dispara DENTRO da chamada real
do Streamline, então zerar uma flag antes e lê-la depois responde exatamente "o NGX deu conta desta
avaliação".

De brinde, as tags trazem o que os parâmetros do NGX não trazem: o **estado D3D12** de cada buffer
e a região válida de cada um. Pelo lado do NGX isso só dava para supor, e as barreiras eram
emitidas a partir de um estado adivinhado.

### A rede pode fazer o upscale ela mesma

A feature aceita ler numa resolução e escrever noutra. Ligado `NREnableUpscaling`, ela lê na
resolução de RENDER — a mesma grade em que profundidade e motion vectors já vivem, sem reamostrar
nada — e escreve na resolução final. Desligado, continua 1:1 sobre a saída pronta do DLSS.

Isso obrigou a separar o que era uma coisa só: "a resolução da rede" ora significava a grade de
leitura, ora a de escrita, e a composição amostrava a errada. Agora são três grades explícitas.

### Estado visível e botão de reiniciar

Cada elo quebrado produz o mesmo sintoma de fora — nada acontece. O overlay agora diz em qual o
addon está: `ESPERANDO O NGX`, `ESPERANDO O DLSS DO JOGO`, `PARADO / FALHOU`, `ATIVO`. E tem um
botão que solta o travamento de falha.

Sem ele, uma recusa do runtime era definitiva até fechar o jogo: o travamento existe para não
martelar uma criação que já falhou a cada frame, e o efeito colateral era que qualquer ajuste que
consertasse a situação só valia no próximo processo.

### Convenção de profundidade e escala de movimento no controle do usuário

`NRDepthMode` força profundidade normal ou invertida em vez de confiar na flag do jogo, e
`NRMVecScaleX/Y` multiplicam a escala de motion vector calculada. Os dois existem para o mesmo tipo
de defeito: quando a declaração do jogo está errada, o filtro produz um resultado errado **sem
falhar em nada** — não há erro para ler, só imagem estranha ou borrão em movimento.

O eixo invertido que o jogo declara (`DLSS.Indicator.Invert.X/Y.Axis`) passou a ser lido também.

## v1.21.0

### Ligar upscaling quebrava a imagem

O filtro neural le quatro buffers, e a feature e criada com UM tamanho — todos os quatro tem que
estar nessa grade. Cor e saida vem DEPOIS do upscale do DLSS; profundidade e motion vectors vem
ANTES, na resolucao de render. Enquanto o jogo roda em DLAA as duas resolucoes sao a mesma e nada
aparece. Ligar Qualidade, Equilibrado ou Desempenho faz a rede ler dois buffers com a grade
errada, e a imagem inteira vira lixo.

O addon agora traz profundidade e motion vectors para a grade da rede antes de avaliar, e ajusta a
escala dos motion vectors pela razao entre as duas — um vetor de movimento so significa alguma
coisa junto com a escala que o converte em pixels. Amostragem por ponto de proposito: media entre
o que esta perto e o que esta longe e uma superficie que nao existe.

O mesmo defeito pela outra ponta: baixar "resolucao da rede" em SDR entregava a cena em resolucao
CHEIA para uma feature criada menor, porque o pass que muda a grade da cor so rodava em HDR. Ele
roda sempre agora.

### A superfície de trabalho passou a ser FP16

O retrato que entra no modelo e a resposta que sai dele viviam em 8 bits por canal. O domínio
continua sendo o retrato SDR que o modelo espera — o que muda é a precisão com que ele é guardado.
A composição final é uma **razão** entre a resposta e o retrato, e razão entre dois números de 8
bits quantiza muito antes do frame em resolução cheia precisar. O addon de referência declara a
mesma escolha nas próprias strings ("FP16 working surface").

Fica atrás de uma chave (`NRFp16Surface`, ligada por padrão) porque isso é a leitura de uma string
do binário deles, não um contrato documentado. Se este runtime recusar formato float nessas
entradas o filtro simplesmente para de rodar — e nesse caso a saída é desligar a chave, sem
recompilar nada. O overlay diz onde olhar: se "Avaliações" parar de subir, é isto.

### Trocar a resolucao do jogo derrubava o frame seguinte

As texturas do addon sao recriadas quando a resolucao muda, e eram soltas na hora. Como o pass e
gravado na command list do JOGO, que so executa depois, a GPU ficava lendo memoria ja devolvida.
Textura e feature agora ficam alguns frames em quarentena antes de serem liberadas de fato.

### Os controles do launcher nao chegavam no addon

O launcher grava `[RenoDX.DLSS5]`; o addon lia `[RENODX-NEURAL]`. Nenhum dos dois lados errava
sozinho — eles simplesmente nao se falavam. A chave mestra so parecia funcionar porque o padrao
interno do addon ja era ligado, e nenhum slider tinha efeito nenhum.

`[RenoDX.DLSS5]` passa a ser a secao canonica dos dois lados, com os mesmos nomes de chave que o
addon de referencia usa — quem configurou por la nao precisa reconfigurar aqui. A secao antiga
continua sendo lida.

### O launcher dizia "desligado" para um jogo que estava ligado

A leitura do estado escolhia a secao a partir de qual ARQUIVO de addon estava na pasta, e so
conhecia o nome do nosso. Num jogo rodando o build da comunidade (`renodx-dlss5.addon64`) ela caia
na secao errada, nao achava nada e reportava desligado — com o ini do jogo dizendo
`NeuralUplift=1` na cara. Agora a chave e lida de onde ela vive, seja qual for o addon que a
dirige, e um addon generico ja instalado na pasta conta como capaz de acionar o filtro.

### O instalador automatico comecava com um passo manual

O addon generico so chegava na biblioteca por importacao manual, e nada no app oferecia essa
importacao. Sem ele, `Offerable` dava falso e o cartao nunca aparecia — justamente no caso para o
qual ele existe: um jogo sem mod RenoDX proprio. O launcher agora carrega o addon dentro de si e o
instala na biblioteca sozinho. Uma copia que o usuario importou na mao nao e sobrescrita.

### Parametros que o runtime aceita e o addon nao mandava

Os subrects estavam com nomes que nao existem (`SubrectBaseX` em vez de
`DLSSNR.ColorSubrectBaseX`). `Set()` de um nome desconhecido nao falha — so nao chega em lugar
nenhum, entao a regiao valida de cada buffer nunca era declarada. Os quatro conjuntos, as
dimensoes de entrada/saida e a correcao de UI agora sao declarados.

### O addon era carregado tarde demais em metade dos jogos

Vários jogos sobem o SDK do DLSS **antes** de criar o device D3D — o interposer do Streamline
carrega na abertura do processo. Quando o ReShade carrega um addon do jeito normal, na criação do
device, o NGX já está de pé e os ganchos do addon chegam atrasados. O ReShade 6.8 tem uma chave
para isso, `[ADDON] LoadFromDllMain`, que manda o proxy carregar o addon do próprio `DllMain`.

O launcher nunca escrevia essa chave. O instalador do build da comunidade tem um passo de INI
inteiro que existe só para isso, e a falha tem até número lá: "erro 225". Medido nos jogos desta
máquina: nenhum dos dois que já rodam o filtro tinha a chave.

Aplicar agora acrescenta o addon à lista, preservando o que já estava nela — `renodx-dlssfix` vive
na mesma chave. Remover tira só o nome do nosso.

### O runtime neural agora tem de onde vir

A biblioteca só podia ser preenchida com cópias que já estavam nesta máquina, e o motivo estava
escrito no próprio código: puxar um binário da NVIDIA de "algum espelho" não é coisa que um
instalador faça pelo usuário. Esse raciocínio vale para um espelho qualquer — não vale para um
índice curado e versionado que este launcher **já baixa e já confia** para configuração por jogo.

O projeto RHI mantém um `dlss_manifest.json` com uma entrada `dlssnr` apontando para o runtime
310.8.0. Quem não tinha nenhum jogo que distribui o arquivo ficava travado num bloqueio sem saída:
sair para caçar um DLL de 158 MB. Era o único passo manual que sobrava.

O que torna isso seguro não é a origem: é que todo arquivo que sai dali passa pela mesma
verificação de Authenticode da NVIDIA que qualquer runtime que este launcher grava. Espelho
adulterado vira recusa, não uma DLL trocada dentro de um jogo. As URLs do índice também são
recusadas se apontarem para fora do repositório do próprio projeto — uma entrada de manifesto é
dado, não instrução.

### O addon da comunidade que você já tem passa na frente do nosso

Quem seguiu as instruções do Discord já tem o build em `%LocalAppData%\RHI\Custom\Addons`, ou
dentro de um jogo configurado à mão. É um build mais novo e mais testado que o embutido, e mandar
o usuário procurar um arquivo que ele já tem é um passo manual como qualquer outro. A varredura
aceita qualquer nome — o build é renomeado a cada versão (`renodx-dlss5-v2.5.addon64`) — e decide
pelos bytes, não pelo nome.

### Comando `neural` na linha de comando

    RenoDXLauncher.exe neural <jogo>

Le a cadeia elo a elo sem gravar nada. Todo elo quebrado da o mesmo sintoma — o jogo abre e nada
acontece — entao "nao funciona" nunca diz qual peca falta. Foi lendo essa saida contra o ini de um
jogo real que os dois defeitos de deteccao acima apareceram, e e ela que mostra a carga
antecipada faltando.

### O indice RHI decide onde nao mexer

`dlssSkipGames` do manifesto do RHI passa a ser respeitado: nesses titulos o launcher nao oferece
troca de runtime nem neural. E uma lista mantida contra relato real, que vale mais do que qualquer
heuristica local.

## v1.20.0

### Corrigir agora refaz a cadeia inteira, nao so as DLLs

Cada elo quebrado produz o MESMO sintoma: o jogo abre e nada acontece. Runtime errado, ReShade
ausente, addon que nao sabe fazer neural, chave desligada — de fora sao indistinguiveis. Consertar
so os runtimes deixava metade do problema de pe, e quem clicou em Corrigir ficava sem saber por que
continuava sem funcionar.

O botao agora percorre a cadeia toda: conjunto de runtimes, ReShade (instala se faltar), addon
capaz de acionar o neural, e a chave. Cada elo e verificado antes de ser tocado, entao rodar de
novo num jogo saudavel nao mexe em nada.

Verificado quebrando os quatro elos de uma vez num jogo — ReShade removido, addon removido, chave
zerada — e rodando o comando: a cadeia voltou inteira.

### Comando `fix` na linha de comando

    RenoDXLauncher.exe fix <jogo>

Espelha o botao Corrigir e imprime o que foi refeito elo a elo. Como todos os defeitos se parecem
de fora, poder ler qual elo estava quebrado e o que separa diagnostico de chute.


## v1.19.0

### O conjunto era gravado pela metade: faltava o plugin que da acesso ao neural

A gravacao do conjunto Streamline substituia apenas os arquivos que o jogo JA TINHA. Nenhum jogo
lancado traz `sl.dlss_nr.dll` — o plugin de Neural Rendering do Streamline so existe no pacote
pre-release — entao ele nunca chegava ao jogo. O resultado era um conjunto "atualizado" que
continuava sem ter por onde expor a feature. `sl.dlss.dll` e `sl.nis.dll` faltavam pelo mesmo
motivo em varios titulos.

Agora grava o conjunto INTEIRO da origem: os oito plugins do Streamline, os runtimes NGX e o
runtime neural, existindo antes no jogo ou nao. E grava nos DOIS destinos, porque sao dois
carregadores diferentes — o jogo carrega os plugins de onde vive o interposer, e o addon neural
procura o runtime ao lado do executavel.

Arquivo ja identico e pulado, para nao recopiar os 158 MB do runtime neural a cada clique. Os
termos de licenca que a NVIDIA distribui com os binarios acompanham a copia.

Verificado removendo `sl.dlss_nr.dll` e `sl.nis.dll` de um jogo e rodando o comando: os dois
voltaram e o conjunto ficou coerente.

### Comando `dlss-set` na linha de comando

    RenoDXLauncher.exe dlss-set <jogo> [pasta-de-origem]

E a operacao que mais precisa ser verificavel: escreve varios arquivos em pastas de sistema. Poder
rodar e conferir o resultado sem abrir a interface e o que permite provar que o conjunto ficou
completo.

## v1.18.0

### Nada mais fica trocado sem o usuario saber

Trocar um runtime nao aparece em lugar nenhum ate o jogo abrir errado, e quem mexeu em varios nao
tem como lembrar quais. Isso deixava a ferramenta pela metade: ela sabe exatamente onde mexeu.

Na abertura, o launcher varre os jogos detectados e mostra uma faixa no topo com os que estao com
runtime trocado, e um botao Restaurar tudo que devolve todos de uma vez. Arquivo em uso nao e
apagado nem silenciado - volta na lista com o nome do jogo, para fechar e repetir.

A varredura tambem recolhe backup cujo conteudo e identico ao arquivo atual. Sem isso o jogo
aparecia como "alterado" para sempre, mesmo depois de restaurado.

### Build pre-release nao entra mais como "atualizacao"

O pacote que traz o `nvngx_dlssnr.dll` e um drop que a NVIDIA nao lancou, e os outros runtimes
dentro dele sao da mesma leva. Eles tem numero de versao MAIOR, entao a regra "maior e melhor" os
escolhia como se fossem release - e o jogo recebia um runtime nunca publicado nem testado com ele.

Foi assim que o Black Myth Wukong, com o resto em 310.7.129, acabou com um Super Resolution 310.8.0
ao lado, e passou a travar.

A deteccao usa o vizinho: uma pasta que contenha o `nvngx_dlssnr.dll` e um drop pre-release
inteiro. O runtime continua servindo para o filtro neural, que e para o que ele existe, mas nao e
mais oferecido como atualizacao de um jogo.

## v1.17.0

### O launcher agora CONSERTA um conjunto de runtimes quebrado

Detectar e mandar a pessoa resolver na pasta nao serve para quem usa um launcher. O cartao de DLSS
passou a mostrar um botao Corrigir quando encontra um estado comprovadamente quebrado.

O que ele considera quebrado, sem precisar adivinhar qual versao o jogo aceita:

- plugins do Streamline de builds diferentes lado a lado. Eles saem juntos do mesmo SDK, entao
  versoes divergentes significam que alguem trocou parte do conjunto - e e assim que o jogo trava
  na abertura;
- arquivo no lugar de um runtime que nao e assinado pela NVIDIA.

Os dois sao defeito com certeza, venham deste launcher, do DLSS Swapper ou de uma troca manual.

O conserto aplica o conjunto Streamline COMPLETO da biblioteca - todos os `sl.*` mais o
`nvngx_dlssg.dll`, do mesmo build. Nao acerta so a peca divergente: trocar peca solta e
exatamente o que produziu o defeito. E nao volta para a versao antiga do jogo, porque quem chega
ali quer o conjunto novo funcionando; para voltar ao original existe o botao Restaurar, separado.

Verificado reproduzindo o estado quebrado: Streamline 2.7.3 com um plugin 2.13.0, detectado, e
depois do conserto tudo coerente em 2.13.0 com backup dos arquivos substituidos.

### A biblioteca guarda conjuntos, nao pecas soltas

Para poder consertar era preciso uma referencia COMPLETA. A varredura agora reconhece uma pasta que
tenha o conjunto inteiro na mesma versao e guarda a pasta toda. Um conjunto incoerente e recusado
como referencia - copiar isso para a biblioteca espalharia o defeito em vez de curar.

## v1.16.1

### Atualizar DLSS podia travar o jogo - o Frame Generation nao pode ser trocado sozinho

A 1.15.0 trocava os tres runtimes NGX igualmente. Isso esta certo para Super Resolution e Ray
Reconstruction, que o jogo alcanca por NGX e que respondem o mesmo contrato numa build nova.
Esta errado para Frame Generation: o `nvngx_dlssg.dll` e dirigido pelo interposer do Streamline
que o jogo carrega, e os dois sao versionados como par.

Medido nesta maquina: os jogos trazem Streamline 2.7.x com nvngx_dlssg 310.7.129, e o conjunto novo
e Streamline 2.13.0 com 310.8.0. Trocar so a metade nvngx deixava um interposer 2.7 chamando um
runtime 310.8 - e isso trava na abertura.

Agora "Atualizar" mexe apenas em Super Resolution e Ray Reconstruction, e o cartao diz por que o
Frame Generation ficou de fora, em vez de lista-lo e nao fazer nada com ele.

### Frame Generation ganhou um caminho proprio, com o conjunto inteiro

Quando existe uma pasta com um conjunto casado (os runtimes e os `sl.*` que a NVIDIA distribui
juntos), da para atualizar o Frame Generation trocando tudo de uma vez. A verificacao acontece
ANTES de qualquer escrita: se faltar um arquivo do conjunto, ou se algum nao for assinado pela
NVIDIA, nada e tocado - uma troca parcial e pior que nenhuma, porque e exatamente o desencontro que
trava o jogo.

### Reverter agora devolve o conjunto inteiro

`Restore` so procurava backup de `nvngx_dls*`. Com os `sl.*` no jogo, restaurar metade
reproduziria o mesmo desencontro de versao que a reversao existe para desfazer.

### Cada troca vai para o log

Nao havia registro por arquivo. Depois que um jogo comeca a travar, sem isso nao ha como saber o
que foi trocado, quando, nem por qual ferramenta - e nesta maquina o DLSS Swapper estava ativo no
mesmo minuto, o que tornou a causa impossivel de provar.

## v1.16.0

### Pasta adicionada a mao podia ser reconhecida como o jogo ERRADO

O corte do sufixo de grupo de release era `-[A-Za-z0-9]+$`: qualquer coisa depois de um hifen. Isso
transformava **`Dishonored-2` em `Dishonored`**, que casava com o jogo anterior da serie â€” o
resultado exato que `MatchService.FindMatch` foi escrito para impedir ("Dishonored 2 must never
match Dishonored"). Pelo mesmo caminho, `Spider-Man` virava `Spider`, `Half-Life` virava `Half`,
`Call-of-Duty` virava `Call-of` e `Gears of War 0-3` virava `Gears of War 0`.

O que separa um grupo de uma palavra do titulo e a caixa: grupos sao all-caps (`CODEX`, `FLT`,
`RELOADED`) ou tem maiuscula interna (`InsaneRamZes`); palavra de titulo depois de hifen e apenas
capitalizada. Numeral romano fica de fora tambem, senao `Control-III` viraria `Control`. Verificado
contra doze casos, os oito legitimos preservados e os quatro sufixos de grupo removidos.

### Pasta sem mod no catalogo agora mostra um nome legivel

Um jogo que o catalogo nao conhece continua util â€” ReShade e o addon neural generico nao dependem
de mod proprio â€” mas aparecia com o nome cru da pasta: `Mortal.Shell.II-InsaneRamZes`, ou pior,
`Retail`. Agora o nome vem do `ProductName` do executavel, o unico candidato escrito pelo
desenvolvedor e nao por quem empacotou a pasta; sem ele, o nome da pasta sem as decoracoes, com
ponto e underscore virando espaco.

## v1.15.1

### A chave que liga o neural estava indo para a secao errada

O addon generico distribuido pela biblioteca passou a ser o build da comunidade, que guarda a
configuracao em `[RenoDX.DLSS5]` sob `NeuralUplift`. O launcher escrevia em `[RENODX-NEURAL]
Enabled`, a secao do build anterior â€” o addon era instalado e ficava desligado, que e exatamente
a forma de "liguei e nao aconteceu nada".

`Remove` agora zera as duas secoes: uma chave de habilitacao esquecida nao pode religar a feature
sozinha se o usuario trocar de build depois.

### Os sliders de NR tambem gravavam no lugar errado

`SettingDef.Section` sempre foi rotulo de agrupamento na interface, nao secao de arquivo â€” todo
valor ia para a secao de preset do mod. Para os controles do neural isso significava gravar
`NRIntensity` num bloco que o addon nao le. `SettingDef` ganhou `IniSection`, e os controles do
neural apontam para o bloco proprio dele.

## v1.15.0

### O launcher atualiza os runtimes de DLSS do jogo

Um jogo roda para sempre a versao de DLSS com que foi lancado, a menos que o estudio publique um
patch. Os runtimes sao drop-in â€” mesmos exports, mesmo contrato NGX â€” entao trocar o arquivo e a
atualizacao inteira, e e a correcao padrao para os artefatos de uma build antiga de Super Resolution.

O cartao aparece em qualquer jogo que carregue um runtime NGX e mostra o que ele tem hoje e para
onde subiria. Nesta maquina, por exemplo: Stellar Blade em 310.1.0, Black Myth Wukong e RE Requiem
em 310.7.129, com 310.8.0 disponivel.

A biblioteca se enche das copias que ja estao na maquina â€” todo jogo com DLSS carrega um runtime
assinado, e o mais novo entre eles serve para o mais antigo. **Nao busca na rede**: puxar binario da
NVIDIA de um espelho qualquer nao e algo que o launcher deva fazer no lugar do usuario.

Duas regras tornam a troca reversivel e segura:

- o arquivo do estudio e copiado para `.renodx-bak` antes de qualquer escrita, e nunca sobrescrito
  depois â€” um segundo "Atualizar" nao pode transformar a nossa copia anterior no "original";
- nada e instalado sem estar assinado pela NVIDIA com digest integro. Um runtime adulterado na pasta
  de um jogo tem exatamente o formato de um ataque, e o arquivo vem de onde o usuario o tinha.

So atualiza, nunca rebaixa: um jogo com build mais nova que a biblioteca fica como esta.

### O cartao do DLSSFIX tambem faltava nos jogos que podiam usa-lo

Mesmo defeito da deteccao do neural, no `DlssFixService`: varredura com `MaxRecursionDepth = 4`
enquanto a Unreal guarda o runtime oito niveis abaixo. E pior ali, porque `ShouldOffer` so oferece
o fix para mods Unreal/Unity â€” ou seja, o cartao faltava exatamente nos titulos para os quais ele
existe. Verificado nesta maquina: com o limite em 10, Stellar Blade e Black Myth Wukong passam a
ser detectados; com 4, nenhum dos dois.

## v1.14.0

### O cartao do neural faltava na maioria dos jogos que podiam usa-lo

A deteccao de DLSS varria a pasta do jogo com `MaxRecursionDepth = 4`. A Unreal guarda o runtime em
`Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\ThirdParty\Win64` â€” **oito** niveis abaixo. O resultado
e que `HasDlss` dava falso em praticamente todo jogo UE, e como UE e a maior parte dos titulos com
DLSS, o cartao simplesmente nao aparecia. Medido nesta maquina: Stellar Blade e Black Myth Wukong,
os dois com o DLSS a profundidade 8, os dois invisiveis. O limite subiu para 10.

### O launcher acha o runtime sozinho

O `nvngx_dlssnr.dll` nao vem em driver nem em SDK publico, entao a unica fonte sao as copias que ja
estao na maquina â€” e ate agora o launcher exigia que o usuario achasse esse arquivo de 158 MB pelo
Explorer e importasse na mao. Agora ele varre Downloads, Area de Trabalho e as pastas dos jogos
detectados, valida o tamanho e importa a primeira copia boa.

De proposito nao busca na rede: o arquivo nao e distribuido publicamente, e puxar um binario da
NVIDIA de um espelho qualquer nao e algo que o launcher deva fazer no lugar do usuario.

### Desligar o neural podia nao desligar nada

`Remove` apagava os arquivos dentro de um `try` que so registrava a falha no log. Com o arquivo em
uso â€” jogo aberto, por exemplo â€” a exclusao falhava, a interface voltava a dizer "desligado", e a
feature continuava viva no jogo. Agora a falha e reportada, dizendo qual arquivo ficou preso e o que
fazer.

## v1.13.1

### Ligar o neural instala o ReShade sozinho

O addon generico e um addon de ReShade: sem esse host nao existe nada que o carregue. Num jogo sem
mod RenoDX â€” justamente o caso que a 1.13.0 passou a atender â€” o ReShade normalmente nao esta la, e
o cartao bloqueava pedindo que o usuario resolvesse por fora. O passo manual no meio de um botao de
"ligar" e o tipo de coisa que faz a feature parecer quebrada.

Agora aplicar o neural instala o ReShade antes, pelo mesmo caminho que o fluxo de mod ja usa, e so
entao copia o addon e o runtime. `GenericBlocker` deixou de listar "ReShade ausente": reportar como
bloqueio impediria exatamente a acao que o corrige.

## v1.13.0

### Neural render em qualquer jogo com DLSS, nao so nos que tem mod proprio

O cartao de Neural Uplift so aparecia quando o mod RenoDX **do proprio jogo** ja sabia dirigir o
DLSSNR â€” o launcher decidia isso escaneando o `.addon64` instalado atras do parametro
`DLSSNR.Output`. Na pratica isso limitava a feature aos poucos titulos com build artesanal, e a
pergunta que todo mundo fazia ("da para ligar no meu jogo?") tinha quase sempre a mesma resposta.

Agora o launcher guarda um **addon generico** na biblioteca, ao lado do runtime, e o instala em
qualquer jogo que tenha DLSS â€” inclusive nos que nao tem mod RenoDX nenhum. O addon generico
engancha os exports do NGX que o jogo ja chama, entao nao precisa de nada do mod do jogo.

Onde ele roda importa: o pass neural entra **inline, na command list do proprio jogo, logo depois
da saida do DLSS** e antes do pos-processamento e da UI. Compor no `present` em cima do backbuffer
â€” o caminho obvio â€” sobrescreve o frame ja finalizado e apaga a HUD junto.

`Detect` deixou de exigir um addon instalado: `Offerable` agora e "tem DLSS **e** (o mod do jogo
sabe fazer **ou** o addon generico esta na biblioteca)". Quando falta alguma peca, `GenericBlocker`
diz qual â€” addon fora da biblioteca, ou ReShade nao instalado no jogo (o addon generico e um addon
de ReShade; sem esse host nao ha o que o carregue).

`Remove` agora tira os dois arquivos, runtime e addon. Deixar o addon para tras manteria o hook do
NGX ativo num jogo em que a feature acabou de ser desligada.

`IsApplied` le a secao certa conforme o caminho: o addon generico tem o proprio bloco de
configuracao (`[RENODX-NEURAL] Enabled`), e ler so a chave de preset reportaria "desligado" em todo
jogo servido por ele.

## v1.12.0

### O download passou a ser um instalador

O release agora traz `RenoDXLauncher-<versao>-setup.exe`, feito com Inno Setup. Ele instala em
`Program Files` (ou so para voce, se preferir na primeira tela), aparece em Aplicativos e
Recursos, e atualiza por cima com o launcher aberto sem falhar com "arquivo em uso". Desinstalar
nao apaga suas configuracoes: ele pergunta, e o padrao e manter.

O motivo de trocar: zip extraido no Downloads e um `.exe` solto rodando de la e o pior caminho
possivel para um binario sem assinatura. Varias versoes do 7-Zip nem propagam o Zone.Identifier
para os arquivos extraidos, entao o Windows trata o resultado como arquivo local e nenhuma
reputacao e acumulada. O zip continua publicado para quem prefere portatil.

O `[Run]` que abre o app no fim usa `runasoriginaluser`. Sem essa flag, instalar como
administrador abriria o launcher elevado, e ele gravaria config, perfil de nits e cache no
`%LocalAppData%` do administrador â€” na abertura seguinte, normal, tudo apareceria vazio.

### O ReShade e verificado por assinatura antes de ser usado

A validacao do ReShade baixado era `ProductName.Contains("ReShade")`. `ProductName` e campo de
recurso do PE, editavel por qualquer um â€” na pratica nao validava nada.

Agora, antes de extrair, o launcher confere via `WinVerifyTrust` que o instalador esta assinado
pelo certificado do autor do ReShade e que o conteudo esta integro. Falhou, o download e
descartado.

O que torna isso suficiente: o ZIP anexado ao instalador fica antes da tabela de certificado do
PE, e o Authenticode faz digest de tudo menos dela â€” trocar um byte dentro do ZIP muda o status
para `HashMismatch`. Validar o instalador prova as DLLs de dentro dele.

E fixado o certificado, nao o hash do arquivo. Hash muda a cada versao, e uma versao nova sem
hash cadastrado quebraria a instalacao para todo mundo; o certificado do ReShade vale ate 2039.
O pino foi calibrado com o 6.7.3 e validou o 6.8.0 sem alteracao.

### Idiomas

A interface agora fala portugues do Brasil e ingles, seguindo o idioma do Windows, com troca
manual dentro do app. Os textos ficam em `src/Localization/strings.json`, com os idiomas lado a
lado; `python tools/gen_resx.py` regera os `.resx`. Adicionar um idioma nao exige mexer em codigo.

Chave sem traducao cai no portugues, entao traducao parcial ja funciona.

### Forma do binario

- `app.manifest` proprio, declarando `supportedOS`. Sem ele o Windows trata o executavel como
  legado e aplica shims de compatibilidade, que funcionam injetando DLL no processo.
- `createdump.exe` sai do publish. O runtime pack traz esse utilitario de dump de memoria da
  Microsoft; o launcher nunca o chama.
- `PublishSingleFile`, `PublishTrimmed` e `PublishReadyToRun` travados em `false`, com o motivo
  comentado no csproj. Ja eram o padrao â€” estao escritos para que uma otimizacao de startup
  bem-intencionada no futuro nao reabra o problema.
- O `.pdb` saiu do instalador e passou a ir anexado ao release.
- Nada mais e escrito em `%TEMP%`. A varredura do Battle.net copiava o `product.db` para la com
  nome aleatorio; foi para `%LocalAppData%\RenoDXLauncher\cache`.
- O `User-Agent` deixou de comecar com `Mozilla/5.0`. O `Referer` do reshade.me fica: e exigido
  pelo servidor.

### Verificacao

`tools/av-selfcheck.ps1` roda no CI a cada push e antes de cada release: forma do publish,
executavel inesperado no payload, metadata de PE, Authenticode, entropia de secao, manifesto,
varredura do Defender, VirusTotal, e hashes.

Dois testes fazem trabalho de verdade: um instala silenciosamente numa pasta temporaria, roda o
binario instalado e desinstala conferindo que nao sobrou nada; o outro varre o codigo-fonte
atras de API de injecao ou persistencia (`WriteProcessMemory`, `CreateRemoteThread`,
`SetWindowsHookEx`, servico, tarefa agendada) e reprova o build se alguma aparecer.

O que nao pode ser verificado sai como `SKIP`, nunca `PASS`.

### Release

`publish` -> assina o `RenoDXLauncher.exe` -> monta o instalador -> assina o instalador. O
instalador precisa ser montado depois do exe assinado, senao o binario que fica no disco do
usuario â€” o que o antivirus dele escaneia todo dia â€” seria o nao assinado.

O Inno Setup e baixado no CI com versao e SHA-256 fixados, e o `dotnet publish` recebe a versao
da tag, para o PE nao divergir do que o instalador anuncia.

## v1.11.2

### Pasta adicionada a mao agora e reconhecida pelo jogo, nao pelo nome da pasta

Um jogo instalado a mao quase nunca esta numa pasta com o nome do jogo. O app usava o nome da
pasta escolhida e parava por ai â€” entao uma pasta chamada `Retail` virava um jogo chamado
"Retail", que nao casa com nada no catalogo. Subir um nivel tambem nao resolvia: pasta baixada
costuma vir com decoracao (`-Grupo`, `[Repack]`, `v1.2.3`) que nenhum titulo de catalogo tem.

Agora o app tenta varios nomes, do mais fraco para o mais forte:

1. o nome da pasta;
2. o nome do pai, subindo enquanto a pasta tiver nome de layout (`Retail`, `Binaries`, `Win64`,
   `bin`, `Content`...);
3. os dois sem as decoracoes de release;
4. **o nome do executavel** â€” que quem escolhe e o desenvolvedor, nao quem empacotou a pasta.

O primeiro que o catalogo reconhecer ganha, e o jogo passa a aparecer com o nome do catalogo em
vez de "Retail". O nome completo e sempre tentado primeiro, entao "Half-Life" continua sendo
Half-Life.

Exemplo real: `...\007.First.Light-InsaneRamZes\Retail` â€” o executavel se chama
`007FirstLight.exe`, que normaliza exatamente para o titulo do catalogo.

### Novo comando `add`

```
RenoDXLauncher.exe add "C:\caminho\da\pasta"
```

Registra a pasta e ja diz o que ele reconheceu: nome do jogo, mod, mantenedor, se tem download
direto e qual `.exe` vai receber o ReShade. Quando nao reconhece, lista os nomes que tentou â€”
que e a informacao de que voce precisa para saber por que falhou.

## v1.11.1

### Revisao adversarial da v1.11.0: 18 defeitos, quase todos meus

A v1.11.0 abriu muita fonte de texto nova e o risco inverteu: em vez de faltar informacao, passou
a aparecer informacao errada. Uma revisao adversarial em 5 frentes achou 18 defeitos confirmados.
Todos consertados aqui, e os 13 casos viraram teste.

**A etiqueta de lugar mentia.** Ela dizia NO JOGO ou OVERLAY RENODX por regex simples, e errava
em tres formas medidas em notas reais:

- Nota com **dois passos em lugares diferentes** ganhava uma etiqueta so, e o ramo "no jogo" era
  testado primeiro. "Disable in-game HDR. `B8G8R8A8_TYPELESS` `Output Size`" saia como NO JOGO â€”
  e o segundo passo, que e o que faz o mod funcionar, e no overlay. Aconteceu em 22 jogos.
- **Negacao invertia o sentido.** "In-game HDR settings are disabled by RenoDX, adjust brightness
  in the mod" era rotulado NO JOGO, ou seja, mandava a pessoa exatamente para o menu que o mod
  tinha acabado de desligar.
- **Narracao virava instrucao.** "...everything is fine once loaded in game" acendia NO JOGO.

Agora a etiqueta julga **clausula por clausula**, exige um verbo de acao junto do marcador de
lugar, inverte quando o lugar esta negado, e **cala quando as clausulas discordam**. Etiqueta
errada e pior que etiqueta nenhuma: manda a pessoa no menu errado, ela nao ve efeito e conclui
que o mod nao funciona.

### Quatro defeitos no parser do codigo dos mods

- **O regex de campo disparava dentro do texto.** O ponto em "MAX. INTENSITY" era lido como inicio
  de um campo novo e cortava a nota ali. O Hitman perdia **85%** da instrucao â€” justamente a
  tabela de calibracao.
- **Blocos comentados eram lidos.** O mod do BMW mantem um `Setting` antigo dentro de `/* */`, e o
  app publicava tres status contraditorios, incluindo "Updating Engine.ini failed".
- **`\r` nao era desfeito.** A nota do S.T.A.L.K.E.R. 2 aparecia com um `\r` cru no fim de cada
  linha.
- **Uma palavra apagava o bloco inteiro.** Se a instrucao citasse "discord" em qualquer linha, o
  bloco todo sumia. O Atelier Yumia perdia o aviso *"NVIDIA GPUs only â€” AMD/Intel are unsupported"*.
  Agora so a linha social e removida, e bloco que o autor marcou como `Instructions` nunca e
  descartado.

### O app afirmava coisas que nao fez

- O texto do indice sobre o **Max Payne 3** dizia que a versao do ReShade *"has been automatically
  set in Overrides (RS Channel)"* â€” isso e a interface de OUTRO aplicativo. Copiado ao pe da letra,
  virava mentira na boca deste launcher. Frases que descrevem a UI alheia agora sao removidas.
- E dizia "este mod nao e distribuido pelo snapshot automatico" com o proprio botao de instalar
  ativo do lado. Agora so aparece quando realmente nao ha download direto.

### Tela

- **Os pre-requisitos subiram para cima do botao de instalar.** Estavam abaixo da dobra: dava para
  clicar em instalar sem nunca ver que o jogo exige outra versao do ReShade.
- O bloco de codigo tinha uma barra de rolagem propria que **engolia a roda do mouse** e travava a
  rolagem do modal inteiro. Removida; o texto quebra linha em vez de ser cortado.
- Prosa nao vai mais para o bloco monoespacado (era cortada no meio da palavra).
- O dedup ignorava simbolos e engolia o preset "Vanilla+ SDR" por causa do "Vanilla SDR".

Numeros depois da limpeza: **201 mods** com instrucao do autor e **179 presets** â€” menos blocos
que na v1.11.0 (433 contra 540), porque o que saiu era link, credito e carimbo de build.

## v1.11.0

### As notas do mod estavam quase todas sendo perdidas

Voce reclamou que nem todas as notas carregavam e que as que carregavam vinham cortadas. Medindo,
o buraco era maior do que parecia:

- **241 dos 269 mods dedicados (89,6%) nao tinham nota nenhuma.** A nota so nascia do *tooltip* de
  um link na wiki, e so 28 linhas tem esse tooltip. Nos outros a secao "NOTAS DO MOD" nem era
  desenhada.
- **68 linhas de instrucao da wiki nunca eram lidas.** Sao blocos de aviso que ficam FORA das
  tabelas, e o parser so olhava linhas de tabela. E justamente ali que esta a unica explicacao de
  como aplicar um Upgrade: *o slider fica escondido ate voce trocar Settings Mode de Simple para
  Advanced, e depois o jogo precisa reiniciar.* Isso vale para ~570 jogos.
- **O texto que o proprio autor do mod escreveu era jogado fora.** Cada mod declara blocos de
  texto/botao no codigo com as instrucoes para o jogador. O app descartava todos. Por isso o
  **DOOM: The Dark Ages** aparecia como "o autor deixou os valores fixos" quando o autor tinha
  escrito o procedimento inteiro: *HDR ligado no jogo, Game Brightness 1.0, contraste 0.50, Paper
  White = HDR Mid Point x 10.*
- **O manifesto do indice lia 5 de 64 chaves.** O seu **Max Payne 3** exige **ReShade 6.4.1** e um
  mod separado do Nexus â€” o app instalava a 6.7.3 e nao dizia nada.
- **Links eram apagados.** Nota que dizia "veja aqui" chegava sem o destino.
- **A seta sumia.** O filtro de simbolos apagava tudo entre U+2190 e U+2BFF, incluindo o `->`, que
  e o operador das instrucoes de menu.

### O que aparece agora

A secao virou **COMO AJUSTAR ESTE JOGO**, e cada instrucao diz **onde** se mexe â€” uma etiqueta
`NO JOGO`, `OVERLAY RENODX (Home)` ou `ANTES DE INSTALAR`. Instrucao sem lugar e meia instrucao.

Reunidas, em ordem: o que o autor do mod escreveu, a linha do jogo na wiki, o indice curado
(avisos de instalacao, versao de ReShade exigida, download externo) e, recolhidas num bloco a
parte, as **regras do motor** (Unreal/Unity) â€” que sao longas e iguais em centenas de jogos.

Blocos de configuracao (`.ini`, argumentos de linha de comando) aparecem em fonte monoespacada e
dao para copiar. Links viraram links de verdade.

Numeros: **228 dos 344 mods** agora trazem instrucao escrita pelo autor (eram zero), **147
presets** calibrados por eles ficaram visiveis, e **nenhum** mod dedicado fica mais com o painel
vazio.

## v1.10.2

### O tModLoader tinha ficado sem alvo (regressÃ£o da v1.10.1)

A v1.10.1 passou a descartar `.exe` dentro de pastas de terceiro (`EpicOnlineServices`,
`EasyAntiCheat`, `redist`, `dotnet`...). SÃ³ que o Ã­ndice do RenoDX manda instalar o tModLoader
**exatamente dentro de `<jogo>\dotnet`** â€” Ã© o `dotnet.exe` que renderiza, porque o jogo Ã© uma
DLL. Ou seja: o Ãºnico `.exe` que importava era jogado fora antes de ser pontuado, e a lista de
candidatos saÃ­a **vazia**. Sem lista, o combo da janela fica vazio e nÃ£o dÃ¡ nem para escolher na
mÃ£o.

Agora a pasta curada do Ã­ndice Ã© **imune** ao filtro de pastas de terceiro e vale mais que
qualquer heurÃ­stica de nome â€” ela Ã© dado conferido Ã  mÃ£o, o resto Ã© palpite. Ela tambÃ©m Ã© lida
direto, sem depender da varredura recursiva (que para em 5 nÃ­veis e ignora links), e vale para o
runtime aninhado (`dotnet\6.0.0\`), que muda de lugar entre atualizaÃ§Ãµes do jogo.

### Nenhum filtro pode devolver lista vazia

Se todos os filtros rejeitarem tudo, o app agora devolve os `.exe` que existem, ordenados por
tamanho. Um primeiro item errado que o usuÃ¡rio corrige Ã© melhor que um combo vazio. Isso jÃ¡
aparece na prÃ¡tica no Rockstar Social Club, cujos dois executÃ¡veis sÃ£o de serviÃ§o.

Os quatro casos viraram teste de regressÃ£o, incluindo o primeiro teste que o app jÃ¡ teve para a
pasta curada do Ã­ndice.

## v1.10.1

### Escolha do .exe do jogo reescrita

Space Marine 2 aparecia como "32 bits" e travava a instalaÃ§Ã£o. O culpado era o app: ele
ordenava os `.exe` por tamanho, e o jogo traz um **instalador do Epic Online Services de
126 MB (32-bit)** ao lado do binÃ¡rio real de 81 MB. Puxando esse fio saÃ­ram mais trÃªs defeitos
do mesmo lugar:

- **A lista de nomes de stub nunca funcionou.** O `RegexOptions.IgnoreCase` fazia `[A-Z]`
  casar com minÃºsculas, entÃ£o o nome era quebrado letra por letra e nenhuma palavra da lista
  batia. `crash_reporter.exe` passava como candidato havia meses.
- **O atalho da loja entrava na frente sem ser avaliado.** A Steam abre Stellar Blade pelo
  `crs-handler.exe` (1 MB) e Max Payne 3 pelo `PlayMaxPayne3.exe` (0,4 MB) â€” os dois sÃ£o
  atalhos que relanÃ§am o binÃ¡rio de verdade. Agora o palpite da loja vale pontos, nÃ£o a vaga.
- **Preferir 64-bit nÃ£o pode ser regra dura.** Max Payne 3 Ã© um jogo 32-bit cujo atalho Ã©
  64-bit; a regra dura escolhia o atalho.

Agora existe **um** ranking sÃ³, e o critÃ©rio principal Ã© o certo: **o `.exe` que importa uma API
grÃ¡fica** (`d3d*`, `dxgi`, `opengl32`, `vulkan-1`). Ã‰ o que separa os dois casos com folga â€”
Max Payne 3 importa `d3dcompiler_43.dll` e o atalho nÃ£o importa nada. Depois vÃªm o sufixo
`-Win64-Shipping`, a pasta curada do Ã­ndice, a semelhanÃ§a com o nome do jogo, 64-bit, o palpite
da loja e, por Ãºltimo, o tamanho. Pastas de terceiros (`EpicOnlineServices`, `EasyAntiCheat`,
`redist`, ...) ficam fora.

`crash` e `report` saÃ­ram da lista de palavras de stub â€” Crash Bandicoot Ã© um jogo. Esses casos
passaram a ser tratados por nomes compostos (`crashreport`, `crashhandler`), que nÃ£o tÃªm como
disparar num tÃ­tulo de verdade.

Os 22 jogos da biblioteca de teste agora escolhem o binÃ¡rio certo, e os quatro casos viraram
teste de regressÃ£o.

### Novo comando `exe`

```
RenoDXLauncher.exe exe "space marine"
```

Mostra os `.exe` candidatos na ordem em que o app escolheria, com bits e tamanho â€” dÃ¡ para
conferir o alvo sem abrir a janela.

## v1.10.0

### Foto do autor do mod

O crÃ©dito agora mostra a **foto real do autor no GitHub** (com a inicial como reserva quando nÃ£o
dÃ¡ para descobrir), e clicar no nome abre o perfil dele.

O catÃ¡logo sÃ³ traz nomes de exibiÃ§Ã£o ("Musa", "OopyDoopy (Jon)"), nunca o usuÃ¡rio do GitHub â€” mas
a URL do addon aponta para o fork que constrÃ³i o mod, que Ã© a conta do autor. O mapa Ã© derivado
disso, do prÃ³prio catÃ¡logo. Com um cuidado: mods construÃ­dos no repositÃ³rio principal apontariam
todos para o ShortFuse, entÃ£o nesses casos o app prefere nÃ£o mostrar foto nenhuma a mostrar
**o rosto da pessoa errada**.

### Selo de estabilidade

Ao lado da loja e do estado agora aparece o selo que a wiki do RenoDX usa: **âœ“ EstÃ¡vel** em verde
ou **âš  InstÃ¡vel** em Ã¢mbar (mod marcado como em construÃ§Ã£o).

## v1.9.0

### CorreÃ§Ã£o de DLSS Frame Generation

Quando o RenoDX converte um jogo de SDR para HDR, o jogo continua dizendo ao DLSS que os buffers
sÃ£o SDR â€” e o Frame Generation interpola com a matemÃ¡tica errada, o que aparece como **piscadas**
e artefatos nos quadros gerados. O addon oficial `renodx-dlssfix` corrige isso.

O launcher agora oferece essa correÃ§Ã£o **em um botÃ£o**, e sÃ³ quando ela faz sentido: mod genÃ©rico
(o caminho que converte SDRâ†’HDR) **e** o jogo tendo o runtime de Frame Generation (`nvngx_dlssg.dll`
ou `sl.interposer.dll`) na pasta. Em jogos de HDR nativo ele nem aparece â€” ali a correÃ§Ã£o seria
inÃºtil, e aplicÃ¡-la Ã s cegas mentiria para o DLSS na direÃ§Ã£o oposta.

Ele baixa o addon, encontra as DLLs sozinho e escreve o `[RENODX-DLSSFIX]` no `ReShade.ini`
preservando addons jÃ¡ listados. ReversÃ­vel pelo mesmo botÃ£o.

## v1.8.0

### â€œAinda nÃ£o tenho a lista de configuraÃ§Ãµes deste modâ€

Essa mensagem estava **errada** em vÃ¡rios casos. Havia dois problemas diferentes:

1. **Mods sem opÃ§Ãµes ajustÃ¡veis** (DOOM: The Dark Ages, DMC5, AC Valhalla e outros 30) â€” o autor
   deixou os valores fixos e o mod sÃ³ troca shaders. O app dizia que "nÃ£o conhecia" o mod, quando
   na verdade nÃ£o hÃ¡ nada para ajustar. Agora ele diz isso com todas as letras.
2. **CatÃ¡logo desatualizado** â€” o manifesto era um retrato do cÃ³digo do renodx no dia do build.
   Foi regenerado (**344 mods**, era 294) e, quando aparecer um mod publicado *depois* desta
   versÃ£o, o launcher agora **lÃª as opÃ§Ãµes direto do cÃ³digo-fonte do mod** no repositÃ³rio do
   maintainer, em vez de desistir.

## v1.7.0

### CrÃ©dito do autor do mod em destaque

Quem faz o mod agora tem lugar de honra no topo do diÃ¡logo: um cartÃ£o com a inicial em
destaque, o rÃ³tulo **CRIADO POR** e o nome do maintainer â€” em vez da linha apagada
"Mod por X" que passava despercebida.

### HistÃ³rico de versÃµes do mod

BotÃ£o **HistÃ³rico** ao lado do autor: abre a lista de **todas as versÃµes daquele mod, com data
e autor de cada alteraÃ§Ã£o**.

Cada mod do RenoDX Ã© uma pasta no repositÃ³rio do maintainer (`src/games/<slug>`), entÃ£o os
commits que tocam aquela pasta *sÃ£o* o changelog do mod â€” nÃ£o existe outra lista publicada. O
launcher descobre o repositÃ³rio certo pela URL do addon (o mod pode ser mantido num fork) e
consulta a API do GitHub, guardando o resultado em cache por 12 horas porque consultas anÃ´nimas
sÃ£o limitadas a 60 por hora. HÃ¡ tambÃ©m **Ver no GitHub** para o histÃ³rico completo.

## v1.6.0

### Fonte prÃ³pria

O app agora **embute a [Inter](https://rsms.me/inter/)** (SIL OFL 1.1) em vez de depender de qual
variante da Segoe cada Windows tem instalada. Resultado: a interface fica igual em qualquer
mÃ¡quina, com letras mais legÃ­veis e espaÃ§amento consistente.

### Ãcone do app

Ãcone prÃ³prio (`tools/gen_icon.py`, gerado por cÃ³digo): uma **faixa de luminÃ¢ncia** que vai da
sombra profunda ao estouro de branco, com um ponto de brilho no extremo claro â€” que Ã© exatamente
o que um tone mapper HDR controla â€” sob o "R" do RenoDX. Sai em 7 tamanhos (16 a 256 px), aparece
no executÃ¡vel, na janela e na barra de tarefas.

### Os dois botÃµes principais

- **Instalar / Atualizar mod**: gradiente quente, brilho suave por trÃ¡s e resposta ao clique.
- **Ativar/Desativar** virou um **cartÃ£o de estado**: mostra um Ã­cone de energia que fica verde
  quando o mod estÃ¡ ativo, com o rÃ³tulo dizendo em que estado ele estÃ¡ e o que o clique vai fazer â€”
  em vez de um botÃ£o que sÃ³ dizia "Desativar".

## v1.5.0

### Modal do jogo

Clicar num jogo agora abre um **diÃ¡logo centralizado** (no lugar do painel lateral), no espÃ­rito
do DLSS Swapper mas com as opÃ§Ãµes do RenoDX:

- **Capa grande** Ã  esquerda, com a marca da loja, botÃ£o **Jogar** e a pasta de instalaÃ§Ã£o.
- Ã€ direita: executÃ¡vel, instalar/atualizar, ativar/desativar, veredito do ReShade.log,
  recomendaÃ§Ãµes, notas â€” e as **configuraÃ§Ãµes do mod em duas colunas**, aproveitando a largura.
- Barra inferior com abrir pasta, pÃ¡gina do mod e remover; **Fechar**, **Esc** ou clique fora
  fecham o diÃ¡logo.
- **Jogar** abre pela loja quando ela Ã© conhecida (`steam://rungameid/...`), preservando overlay
  e saves na nuvem; senÃ£o executa o exe escolhido.

## v1.4.0

### Aviso de atualizaÃ§Ã£o

Quando um mod tem build mais nova, agora Ã© impossÃ­vel nÃ£o ver:

- **Selo Ã¢mbar â€œATUALIZARâ€** na capa do jogo, com ponto de status Ã¢mbar embaixo.
- **CartÃ£o de aviso** no painel do jogo, explicando que as configuraÃ§Ãµes sÃ£o preservadas, com
  botÃ£o **â€œAtualizar agoraâ€** ali mesmo.
- **BotÃ£o Ã¢mbar â€œAtualizar todos (N)â€** na barra superior, que sÃ³ aparece quando hÃ¡ pendÃªncias.

### Visual

- **Cards no estilo biblioteca**: a capa preenche o cartÃ£o inteiro, com gradiente para o tÃ­tulo
  ficar legÃ­vel sobre qualquer arte; marca da loja no canto; leve zoom ao passar o mouse.
- **Capas que faltavam agora carregam**: a Steam moderna guarda a arte em
  `librarycache/<appid>/<hash>/library_capsule.jpg` (o cÃ³digo sÃ³ olhava o formato antigo), e os
  jogos de **Xbox/Game Pass** tÃªm imagens prÃ³prias na pasta, declaradas no `MicrosoftGame.config`.
- **Painel de detalhes com cabeÃ§alho ilustrado** (capa esmaecida atrÃ¡s do tÃ­tulo).
- Barra superior com logo, botÃµes com Ã­cone, estado vazio ilustrado quando nada casa com o filtro.
- Notas vindas de fontes externas passam por um filtro que remove sÃ­mbolos que o Windows
  renderiza como quadrados.
- BotÃµes agora tÃªm **nomes de acessibilidade** (leitores de tela e automaÃ§Ã£o de teste).

## v1.3.0

### Visual refeito

- **Ãcones vetoriais em tudo.** Os emojis viravam quadrados/glifos errados dependendo da fonte
  do Windows. Agora sÃ£o vetores: as marcas oficiais das lojas (Steam, Epic, GOG, Xbox, EA,
  Battle.net, Rockstar, Ubisoft) vÃªm do [simple-icons](https://simple-icons.org) (CC0) e os
  glifos de interface sÃ£o paths estilo Material Symbols.
- **Tipografia e espaÃ§amento** revistos: hierarquia de tÃ­tulos, rÃ³tulos de seÃ§Ã£o, entrelinha
  legÃ­vel, cantos e cores consistentes.
- **Controles prÃ³prios**: slider com trilho/alÃ§a desenhados, combo com sombra, campo de busca
  com Ã­cone e placeholder, barra de rolagem fina, tooltip no tema.
- **DiÃ¡logos no tema do app** (antes eram os do Windows, brancos e destoando), com Ã­cone e cor
  por tipo â€” aviso, perigo, pergunta.
- Selo de estado do jogo agora tem ponto colorido e contraste correto em cada situaÃ§Ã£o.

### Linha de comando

O launcher agora roda **headless**: `list`, `check`, `verify`, `settings`, `set`, `profile`,
`install`, `enable`, `disable` e `doctor`. Serve para automatizar, diagnosticar e reportar bugs
sem abrir a janela. `set` mostra sempre o arquivo-alvo e o antesâ†’depois, e aceita `--dry-run`;
instalar por CLI aborta se houver anti-cheat.

## v1.2.0

### âœ¨ AtualizaÃ§Ã£o de mods

Os mods RenoDX sÃ£o atualizados continuamente pelos autores. Agora o launcher acompanha isso:

- **"Ver atualizaÃ§Ãµes"** checa **todos** os mods instalados de uma vez (em paralelo) e marca na
  grade quem tem build nova, com o selo ciano **ATUALIZAÃ‡ÃƒO**.
- **"Atualizar todos (N)"** baixa as versÃµes novas em lote. **Suas configuraÃ§Ãµes sÃ£o preservadas** â€”
  sÃ³ o arquivo do mod Ã© trocado; o `ReShade.ini` fica intacto.
- A detecÃ§Ã£o compara o **ETag** da build exata que vocÃª instalou com a do servidor (registrado em
  `installed.json`), em vez de adivinhar por tamanho/data. Para mods instalados Ã  mÃ£o, cai no
  mÃ©todo antigo automaticamente.

### ðŸ” DiagnÃ³stico melhor

- Novo veredito **"ReShade sem suporte a add-ons"**: quando o jogo roda com um ReShade da build
  normal (sem add-on), o mod fica inerte para sempre e nada indicava isso. Agora o launcher detecta
  pelo log e explica que basta clicar em *Instalar / Atualizar mod* para trocar pela build certa.

## v1.1.0

Auditoria adversarial multi-agente (49 achados verificados). CorreÃ§Ãµes crÃ­ticas de seguranÃ§a
e de qualidade do HDR, mais duas funcionalidades novas.

### â›” CorreÃ§Ãµes crÃ­ticas

- **Aviso de anti-cheat por detecÃ§Ã£o real.** Um novo scanner procura EasyAntiCheat / BattlEye /
  Vanguard **nos arquivos do jogo** e exige confirmaÃ§Ã£o explÃ­cita antes de instalar. Antes o aviso
  sÃ³ existia se a nota da wiki mencionasse o assunto â€” jogos como *The Outlast Trials*, *Remnant 2*
  e *Ready or Not* instalavam sem nenhum alerta (risco de banimento de conta).
- **Sliders de nits nÃ£o sÃ£o mais cortados.** 26 configuraÃ§Ãµes em 14 jogos (Monster Hunter: World,
  Valheim, Dragon's Dogma 2, Stellar Bladeâ€¦) nÃ£o trazem valor mÃ¡ximo no manifesto; o slider limitava
  a 100 e **gravava `ToneMapPeakNits=100` no ReShade.ini** â€” o oposto do objetivo do app.
- **Xbox / Game Pass instalava na pasta errada.** O `gamelaunchhelper.exe` liderava a lista de
  executÃ¡veis, entÃ£o o ReShade ia para um diretÃ³rio onde o jogo nunca o carregava (a interface
  dizia "instalado" e nada acontecia).
- **Jogos com mod apareciam como "sem mod".** O pareamento agora usa o **Steam AppID**, recuperando
  Resident Evil 4, Silent Hill 2, Resident Evil 2/3/7 e Dying Light 2.

### ðŸ”§ Outras correÃ§Ãµes

- Grade nÃ£o perde mais capas e selos quando uma instalaÃ§Ã£o acontece durante o carregamento.
- `config.json` gravado de forma atÃ´mica (o perfil de nits e os executÃ¡veis fixados nÃ£o se perdem
  mais em silÃªncio); cÃ³pia `.corrupt` preservada se houver falha de leitura.
- ExecutÃ¡veis legÃ­timos nÃ£o sÃ£o mais descartados por conterem "crash", "eac" etc. no nome.
- Ativar/Desativar/Remover ressincronizam o estado quando falham â€” o selo parou de mentir.
- Painel de configuraÃ§Ãµes vazio agora **explica o motivo** em vez de ficar mudo.
- InstalaÃ§Ã£o que falha depois do ReShade entrar faz **rollback** da DLL recÃ©m-copiada.
- DetecÃ§Ã£o de "jogo aberto" prioriza o caminho real do processo (menos falso positivo).

### âœ¨ Novidades

- **VerificaÃ§Ã£o de carregamento**: o launcher lÃª o `ReShade.log` e informa se o mod **realmente
  carregou** na Ãºltima vez que o jogo rodou â€” confirmado, falhou, build sem suporte a add-ons, ou
  nÃ£o carregado.
- **Aviso de versÃ£o mais nova** do mod disponÃ­vel no servidor.

### ðŸ§ª Qualidade

Bateria de testes ampliada de 33 para 56 verificaÃ§Ãµes, com regressÃ£o dedicada para cada bug crÃ­tico.

---

## v1.0.0

Primeira versÃ£o. DetecÃ§Ã£o de jogos em 8 lojas + varredura de pastas, catÃ¡logo RenoDX ao vivo
(~900 entradas), instalaÃ§Ã£o do ReShade + add-on em um clique, ativar/desativar por jogo, editor
de configuraÃ§Ãµes (nits, tone mapper, color grading) gravando direto no `ReShade.ini`, perfil do
monitor, cartÃµes de recomendaÃ§Ã£o por jogo e guia de HDR embutido.



