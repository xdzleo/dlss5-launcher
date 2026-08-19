# Changelog

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
`%LocalAppData%` do administrador — na abertura seguinte, normal, tudo apareceria vazio.

### O ReShade e verificado por assinatura antes de ser usado

A validacao do ReShade baixado era `ProductName.Contains("ReShade")`. `ProductName` e campo de
recurso do PE, editavel por qualquer um — na pratica nao validava nada.

Agora, antes de extrair, o launcher confere via `WinVerifyTrust` que o instalador esta assinado
pelo certificado do autor do ReShade e que o conteudo esta integro. Falhou, o download e
descartado.

O que torna isso suficiente: o ZIP anexado ao instalador fica antes da tabela de certificado do
PE, e o Authenticode faz digest de tudo menos dela — trocar um byte dentro do ZIP muda o status
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
  comentado no csproj. Ja eram o padrao — estao escritos para que uma otimizacao de startup
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
usuario — o que o antivirus dele escaneia todo dia — seria o nao assinado.

O Inno Setup e baixado no CI com versao e SHA-256 fixados, e o `dotnet publish` recebe a versao
da tag, para o PE nao divergir do que o instalador anuncia.

## v1.11.2

### Pasta adicionada a mao agora e reconhecida pelo jogo, nao pelo nome da pasta

Um jogo instalado a mao quase nunca esta numa pasta com o nome do jogo. O app usava o nome da
pasta escolhida e parava por ai — entao uma pasta chamada `Retail` virava um jogo chamado
"Retail", que nao casa com nada no catalogo. Subir um nivel tambem nao resolvia: pasta baixada
costuma vir com decoracao (`-Grupo`, `[Repack]`, `v1.2.3`) que nenhum titulo de catalogo tem.

Agora o app tenta varios nomes, do mais fraco para o mais forte:

1. o nome da pasta;
2. o nome do pai, subindo enquanto a pasta tiver nome de layout (`Retail`, `Binaries`, `Win64`,
   `bin`, `Content`...);
3. os dois sem as decoracoes de release;
4. **o nome do executavel** — que quem escolhe e o desenvolvedor, nao quem empacotou a pasta.

O primeiro que o catalogo reconhecer ganha, e o jogo passa a aparecer com o nome do catalogo em
vez de "Retail". O nome completo e sempre tentado primeiro, entao "Half-Life" continua sendo
Half-Life.

Exemplo real: `...\007.First.Light-InsaneRamZes\Retail` — o executavel se chama
`007FirstLight.exe`, que normaliza exatamente para o titulo do catalogo.

### Novo comando `add`

```
RenoDXLauncher.exe add "C:\caminho\da\pasta"
```

Registra a pasta e ja diz o que ele reconheceu: nome do jogo, mod, mantenedor, se tem download
direto e qual `.exe` vai receber o ReShade. Quando nao reconhece, lista os nomes que tentou —
que e a informacao de que voce precisa para saber por que falhou.

## v1.11.1

### Revisao adversarial da v1.11.0: 18 defeitos, quase todos meus

A v1.11.0 abriu muita fonte de texto nova e o risco inverteu: em vez de faltar informacao, passou
a aparecer informacao errada. Uma revisao adversarial em 5 frentes achou 18 defeitos confirmados.
Todos consertados aqui, e os 13 casos viraram teste.

**A etiqueta de lugar mentia.** Ela dizia NO JOGO ou OVERLAY RENODX por regex simples, e errava
em tres formas medidas em notas reais:

- Nota com **dois passos em lugares diferentes** ganhava uma etiqueta so, e o ramo "no jogo" era
  testado primeiro. "Disable in-game HDR. `B8G8R8A8_TYPELESS` `Output Size`" saia como NO JOGO —
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
  de um campo novo e cortava a nota ali. O Hitman perdia **85%** da instrucao — justamente a
  tabela de calibracao.
- **Blocos comentados eram lidos.** O mod do BMW mantem um `Setting` antigo dentro de `/* */`, e o
  app publicava tres status contraditorios, incluindo "Updating Engine.ini failed".
- **`\r` nao era desfeito.** A nota do S.T.A.L.K.E.R. 2 aparecia com um `\r` cru no fim de cada
  linha.
- **Uma palavra apagava o bloco inteiro.** Se a instrucao citasse "discord" em qualquer linha, o
  bloco todo sumia. O Atelier Yumia perdia o aviso *"NVIDIA GPUs only — AMD/Intel are unsupported"*.
  Agora so a linha social e removida, e bloco que o autor marcou como `Instructions` nunca e
  descartado.

### O app afirmava coisas que nao fez

- O texto do indice sobre o **Max Payne 3** dizia que a versao do ReShade *"has been automatically
  set in Overrides (RS Channel)"* — isso e a interface de OUTRO aplicativo. Copiado ao pe da letra,
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

Numeros depois da limpeza: **201 mods** com instrucao do autor e **179 presets** — menos blocos
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
  mod separado do Nexus — o app instalava a 6.7.3 e nao dizia nada.
- **Links eram apagados.** Nota que dizia "veja aqui" chegava sem o destino.
- **A seta sumia.** O filtro de simbolos apagava tudo entre U+2190 e U+2BFF, incluindo o `->`, que
  e o operador das instrucoes de menu.

### O que aparece agora

A secao virou **COMO AJUSTAR ESTE JOGO**, e cada instrucao diz **onde** se mexe — uma etiqueta
`NO JOGO`, `OVERLAY RENODX (Home)` ou `ANTES DE INSTALAR`. Instrucao sem lugar e meia instrucao.

Reunidas, em ordem: o que o autor do mod escreveu, a linha do jogo na wiki, o indice curado
(avisos de instalacao, versao de ReShade exigida, download externo) e, recolhidas num bloco a
parte, as **regras do motor** (Unreal/Unity) — que sao longas e iguais em centenas de jogos.

Blocos de configuracao (`.ini`, argumentos de linha de comando) aparecem em fonte monoespacada e
dao para copiar. Links viraram links de verdade.

Numeros: **228 dos 344 mods** agora trazem instrucao escrita pelo autor (eram zero), **147
presets** calibrados por eles ficaram visiveis, e **nenhum** mod dedicado fica mais com o painel
vazio.

## v1.10.2

### O tModLoader tinha ficado sem alvo (regressão da v1.10.1)

A v1.10.1 passou a descartar `.exe` dentro de pastas de terceiro (`EpicOnlineServices`,
`EasyAntiCheat`, `redist`, `dotnet`...). Só que o índice do RenoDX manda instalar o tModLoader
**exatamente dentro de `<jogo>\dotnet`** — é o `dotnet.exe` que renderiza, porque o jogo é uma
DLL. Ou seja: o único `.exe` que importava era jogado fora antes de ser pontuado, e a lista de
candidatos saía **vazia**. Sem lista, o combo da janela fica vazio e não dá nem para escolher na
mão.

Agora a pasta curada do índice é **imune** ao filtro de pastas de terceiro e vale mais que
qualquer heurística de nome — ela é dado conferido à mão, o resto é palpite. Ela também é lida
direto, sem depender da varredura recursiva (que para em 5 níveis e ignora links), e vale para o
runtime aninhado (`dotnet\6.0.0\`), que muda de lugar entre atualizações do jogo.

### Nenhum filtro pode devolver lista vazia

Se todos os filtros rejeitarem tudo, o app agora devolve os `.exe` que existem, ordenados por
tamanho. Um primeiro item errado que o usuário corrige é melhor que um combo vazio. Isso já
aparece na prática no Rockstar Social Club, cujos dois executáveis são de serviço.

Os quatro casos viraram teste de regressão, incluindo o primeiro teste que o app já teve para a
pasta curada do índice.

## v1.10.1

### Escolha do .exe do jogo reescrita

Space Marine 2 aparecia como "32 bits" e travava a instalação. O culpado era o app: ele
ordenava os `.exe` por tamanho, e o jogo traz um **instalador do Epic Online Services de
126 MB (32-bit)** ao lado do binário real de 81 MB. Puxando esse fio saíram mais três defeitos
do mesmo lugar:

- **A lista de nomes de stub nunca funcionou.** O `RegexOptions.IgnoreCase` fazia `[A-Z]`
  casar com minúsculas, então o nome era quebrado letra por letra e nenhuma palavra da lista
  batia. `crash_reporter.exe` passava como candidato havia meses.
- **O atalho da loja entrava na frente sem ser avaliado.** A Steam abre Stellar Blade pelo
  `crs-handler.exe` (1 MB) e Max Payne 3 pelo `PlayMaxPayne3.exe` (0,4 MB) — os dois são
  atalhos que relançam o binário de verdade. Agora o palpite da loja vale pontos, não a vaga.
- **Preferir 64-bit não pode ser regra dura.** Max Payne 3 é um jogo 32-bit cujo atalho é
  64-bit; a regra dura escolhia o atalho.

Agora existe **um** ranking só, e o critério principal é o certo: **o `.exe` que importa uma API
gráfica** (`d3d*`, `dxgi`, `opengl32`, `vulkan-1`). É o que separa os dois casos com folga —
Max Payne 3 importa `d3dcompiler_43.dll` e o atalho não importa nada. Depois vêm o sufixo
`-Win64-Shipping`, a pasta curada do índice, a semelhança com o nome do jogo, 64-bit, o palpite
da loja e, por último, o tamanho. Pastas de terceiros (`EpicOnlineServices`, `EasyAntiCheat`,
`redist`, ...) ficam fora.

`crash` e `report` saíram da lista de palavras de stub — Crash Bandicoot é um jogo. Esses casos
passaram a ser tratados por nomes compostos (`crashreport`, `crashhandler`), que não têm como
disparar num título de verdade.

Os 22 jogos da biblioteca de teste agora escolhem o binário certo, e os quatro casos viraram
teste de regressão.

### Novo comando `exe`

```
RenoDXLauncher.exe exe "space marine"
```

Mostra os `.exe` candidatos na ordem em que o app escolheria, com bits e tamanho — dá para
conferir o alvo sem abrir a janela.

## v1.10.0

### Foto do autor do mod

O crédito agora mostra a **foto real do autor no GitHub** (com a inicial como reserva quando não
dá para descobrir), e clicar no nome abre o perfil dele.

O catálogo só traz nomes de exibição ("Musa", "OopyDoopy (Jon)"), nunca o usuário do GitHub — mas
a URL do addon aponta para o fork que constrói o mod, que é a conta do autor. O mapa é derivado
disso, do próprio catálogo. Com um cuidado: mods construídos no repositório principal apontariam
todos para o ShortFuse, então nesses casos o app prefere não mostrar foto nenhuma a mostrar
**o rosto da pessoa errada**.

### Selo de estabilidade

Ao lado da loja e do estado agora aparece o selo que a wiki do RenoDX usa: **✓ Estável** em verde
ou **⚠ Instável** em âmbar (mod marcado como em construção).

## v1.9.0

### Correção de DLSS Frame Generation

Quando o RenoDX converte um jogo de SDR para HDR, o jogo continua dizendo ao DLSS que os buffers
são SDR — e o Frame Generation interpola com a matemática errada, o que aparece como **piscadas**
e artefatos nos quadros gerados. O addon oficial `renodx-dlssfix` corrige isso.

O launcher agora oferece essa correção **em um botão**, e só quando ela faz sentido: mod genérico
(o caminho que converte SDR→HDR) **e** o jogo tendo o runtime de Frame Generation (`nvngx_dlssg.dll`
ou `sl.interposer.dll`) na pasta. Em jogos de HDR nativo ele nem aparece — ali a correção seria
inútil, e aplicá-la às cegas mentiria para o DLSS na direção oposta.

Ele baixa o addon, encontra as DLLs sozinho e escreve o `[RENODX-DLSSFIX]` no `ReShade.ini`
preservando addons já listados. Reversível pelo mesmo botão.

## v1.8.0

### “Ainda não tenho a lista de configurações deste mod”

Essa mensagem estava **errada** em vários casos. Havia dois problemas diferentes:

1. **Mods sem opções ajustáveis** (DOOM: The Dark Ages, DMC5, AC Valhalla e outros 30) — o autor
   deixou os valores fixos e o mod só troca shaders. O app dizia que "não conhecia" o mod, quando
   na verdade não há nada para ajustar. Agora ele diz isso com todas as letras.
2. **Catálogo desatualizado** — o manifesto era um retrato do código do renodx no dia do build.
   Foi regenerado (**344 mods**, era 294) e, quando aparecer um mod publicado *depois* desta
   versão, o launcher agora **lê as opções direto do código-fonte do mod** no repositório do
   maintainer, em vez de desistir.

## v1.7.0

### Crédito do autor do mod em destaque

Quem faz o mod agora tem lugar de honra no topo do diálogo: um cartão com a inicial em
destaque, o rótulo **CRIADO POR** e o nome do maintainer — em vez da linha apagada
"Mod por X" que passava despercebida.

### Histórico de versões do mod

Botão **Histórico** ao lado do autor: abre a lista de **todas as versões daquele mod, com data
e autor de cada alteração**.

Cada mod do RenoDX é uma pasta no repositório do maintainer (`src/games/<slug>`), então os
commits que tocam aquela pasta *são* o changelog do mod — não existe outra lista publicada. O
launcher descobre o repositório certo pela URL do addon (o mod pode ser mantido num fork) e
consulta a API do GitHub, guardando o resultado em cache por 12 horas porque consultas anônimas
são limitadas a 60 por hora. Há também **Ver no GitHub** para o histórico completo.

## v1.6.0

### Fonte própria

O app agora **embute a [Inter](https://rsms.me/inter/)** (SIL OFL 1.1) em vez de depender de qual
variante da Segoe cada Windows tem instalada. Resultado: a interface fica igual em qualquer
máquina, com letras mais legíveis e espaçamento consistente.

### Ícone do app

Ícone próprio (`tools/gen_icon.py`, gerado por código): uma **faixa de luminância** que vai da
sombra profunda ao estouro de branco, com um ponto de brilho no extremo claro — que é exatamente
o que um tone mapper HDR controla — sob o "R" do RenoDX. Sai em 7 tamanhos (16 a 256 px), aparece
no executável, na janela e na barra de tarefas.

### Os dois botões principais

- **Instalar / Atualizar mod**: gradiente quente, brilho suave por trás e resposta ao clique.
- **Ativar/Desativar** virou um **cartão de estado**: mostra um ícone de energia que fica verde
  quando o mod está ativo, com o rótulo dizendo em que estado ele está e o que o clique vai fazer —
  em vez de um botão que só dizia "Desativar".

## v1.5.0

### Modal do jogo

Clicar num jogo agora abre um **diálogo centralizado** (no lugar do painel lateral), no espírito
do DLSS Swapper mas com as opções do RenoDX:

- **Capa grande** à esquerda, com a marca da loja, botão **Jogar** e a pasta de instalação.
- À direita: executável, instalar/atualizar, ativar/desativar, veredito do ReShade.log,
  recomendações, notas — e as **configurações do mod em duas colunas**, aproveitando a largura.
- Barra inferior com abrir pasta, página do mod e remover; **Fechar**, **Esc** ou clique fora
  fecham o diálogo.
- **Jogar** abre pela loja quando ela é conhecida (`steam://rungameid/...`), preservando overlay
  e saves na nuvem; senão executa o exe escolhido.

## v1.4.0

### Aviso de atualização

Quando um mod tem build mais nova, agora é impossível não ver:

- **Selo âmbar “ATUALIZAR”** na capa do jogo, com ponto de status âmbar embaixo.
- **Cartão de aviso** no painel do jogo, explicando que as configurações são preservadas, com
  botão **“Atualizar agora”** ali mesmo.
- **Botão âmbar “Atualizar todos (N)”** na barra superior, que só aparece quando há pendências.

### Visual

- **Cards no estilo biblioteca**: a capa preenche o cartão inteiro, com gradiente para o título
  ficar legível sobre qualquer arte; marca da loja no canto; leve zoom ao passar o mouse.
- **Capas que faltavam agora carregam**: a Steam moderna guarda a arte em
  `librarycache/<appid>/<hash>/library_capsule.jpg` (o código só olhava o formato antigo), e os
  jogos de **Xbox/Game Pass** têm imagens próprias na pasta, declaradas no `MicrosoftGame.config`.
- **Painel de detalhes com cabeçalho ilustrado** (capa esmaecida atrás do título).
- Barra superior com logo, botões com ícone, estado vazio ilustrado quando nada casa com o filtro.
- Notas vindas de fontes externas passam por um filtro que remove símbolos que o Windows
  renderiza como quadrados.
- Botões agora têm **nomes de acessibilidade** (leitores de tela e automação de teste).

## v1.3.0

### Visual refeito

- **Ícones vetoriais em tudo.** Os emojis viravam quadrados/glifos errados dependendo da fonte
  do Windows. Agora são vetores: as marcas oficiais das lojas (Steam, Epic, GOG, Xbox, EA,
  Battle.net, Rockstar, Ubisoft) vêm do [simple-icons](https://simple-icons.org) (CC0) e os
  glifos de interface são paths estilo Material Symbols.
- **Tipografia e espaçamento** revistos: hierarquia de títulos, rótulos de seção, entrelinha
  legível, cantos e cores consistentes.
- **Controles próprios**: slider com trilho/alça desenhados, combo com sombra, campo de busca
  com ícone e placeholder, barra de rolagem fina, tooltip no tema.
- **Diálogos no tema do app** (antes eram os do Windows, brancos e destoando), com ícone e cor
  por tipo — aviso, perigo, pergunta.
- Selo de estado do jogo agora tem ponto colorido e contraste correto em cada situação.

### Linha de comando

O launcher agora roda **headless**: `list`, `check`, `verify`, `settings`, `set`, `profile`,
`install`, `enable`, `disable` e `doctor`. Serve para automatizar, diagnosticar e reportar bugs
sem abrir a janela. `set` mostra sempre o arquivo-alvo e o antes→depois, e aceita `--dry-run`;
instalar por CLI aborta se houver anti-cheat.

## v1.2.0

### ✨ Atualização de mods

Os mods RenoDX são atualizados continuamente pelos autores. Agora o launcher acompanha isso:

- **"Ver atualizações"** checa **todos** os mods instalados de uma vez (em paralelo) e marca na
  grade quem tem build nova, com o selo ciano **ATUALIZAÇÃO**.
- **"Atualizar todos (N)"** baixa as versões novas em lote. **Suas configurações são preservadas** —
  só o arquivo do mod é trocado; o `ReShade.ini` fica intacto.
- A detecção compara o **ETag** da build exata que você instalou com a do servidor (registrado em
  `installed.json`), em vez de adivinhar por tamanho/data. Para mods instalados à mão, cai no
  método antigo automaticamente.

### 🔍 Diagnóstico melhor

- Novo veredito **"ReShade sem suporte a add-ons"**: quando o jogo roda com um ReShade da build
  normal (sem add-on), o mod fica inerte para sempre e nada indicava isso. Agora o launcher detecta
  pelo log e explica que basta clicar em *Instalar / Atualizar mod* para trocar pela build certa.

## v1.1.0

Auditoria adversarial multi-agente (49 achados verificados). Correções críticas de segurança
e de qualidade do HDR, mais duas funcionalidades novas.

### ⛔ Correções críticas

- **Aviso de anti-cheat por detecção real.** Um novo scanner procura EasyAntiCheat / BattlEye /
  Vanguard **nos arquivos do jogo** e exige confirmação explícita antes de instalar. Antes o aviso
  só existia se a nota da wiki mencionasse o assunto — jogos como *The Outlast Trials*, *Remnant 2*
  e *Ready or Not* instalavam sem nenhum alerta (risco de banimento de conta).
- **Sliders de nits não são mais cortados.** 26 configurações em 14 jogos (Monster Hunter: World,
  Valheim, Dragon's Dogma 2, Stellar Blade…) não trazem valor máximo no manifesto; o slider limitava
  a 100 e **gravava `ToneMapPeakNits=100` no ReShade.ini** — o oposto do objetivo do app.
- **Xbox / Game Pass instalava na pasta errada.** O `gamelaunchhelper.exe` liderava a lista de
  executáveis, então o ReShade ia para um diretório onde o jogo nunca o carregava (a interface
  dizia "instalado" e nada acontecia).
- **Jogos com mod apareciam como "sem mod".** O pareamento agora usa o **Steam AppID**, recuperando
  Resident Evil 4, Silent Hill 2, Resident Evil 2/3/7 e Dying Light 2.

### 🔧 Outras correções

- Grade não perde mais capas e selos quando uma instalação acontece durante o carregamento.
- `config.json` gravado de forma atômica (o perfil de nits e os executáveis fixados não se perdem
  mais em silêncio); cópia `.corrupt` preservada se houver falha de leitura.
- Executáveis legítimos não são mais descartados por conterem "crash", "eac" etc. no nome.
- Ativar/Desativar/Remover ressincronizam o estado quando falham — o selo parou de mentir.
- Painel de configurações vazio agora **explica o motivo** em vez de ficar mudo.
- Instalação que falha depois do ReShade entrar faz **rollback** da DLL recém-copiada.
- Detecção de "jogo aberto" prioriza o caminho real do processo (menos falso positivo).

### ✨ Novidades

- **Verificação de carregamento**: o launcher lê o `ReShade.log` e informa se o mod **realmente
  carregou** na última vez que o jogo rodou — confirmado, falhou, build sem suporte a add-ons, ou
  não carregado.
- **Aviso de versão mais nova** do mod disponível no servidor.

### 🧪 Qualidade

Bateria de testes ampliada de 33 para 56 verificações, com regressão dedicada para cada bug crítico.

---

## v1.0.0

Primeira versão. Detecção de jogos em 8 lojas + varredura de pastas, catálogo RenoDX ao vivo
(~900 entradas), instalação do ReShade + add-on em um clique, ativar/desativar por jogo, editor
de configurações (nits, tone mapper, color grading) gravando direto no `ReShade.ini`, perfil do
monitor, cartões de recomendação por jogo e guia de HDR embutido.
