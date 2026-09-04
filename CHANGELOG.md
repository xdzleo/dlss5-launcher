# Changelog

## v1.94.0

O launcher agora mostra as pastas que **sobraram de jogos desinstalados** — e oferece apagá-las.

### Por que elas existem

Desinstalar um jogo pela loja apaga os arquivos **dela**. Tudo o que o launcher pôs ali — runtimes,
add-ons, o proxy do ReShade, os backups — não é dela, então fica; e como a pasta não esvazia, a
própria Steam a deixa de pé. O resultado é uma pasta com centenas de megabytes e nenhum jogo
dentro, que ninguém vai procurar porque ninguém sabe que ela existe.

Nesta máquina eram **7 pastas, 2,43 GB**:

| pasta | ocupa |
|---|---|
| S.T.A.L.K.E.R. 2 Heart of Chornobyl | 636 MB |
| Baldurs Gate 3 | 604 MB |
| Call of Duty Modern Warfare III | 378 MB |
| Gears5 | 227 MB |
| SunsetOverdrive | 227 MB |
| Yakuza Like a Dragon | 227 MB |
| Lies of P | 193 MB |

Quase tudo isso é o mesmo runtime neural de 159 MB, repetido uma vez por pasta.

### Como aparece

A pasta entra na grade como qualquer jogo, com a capa que ela tinha, e um selo vermelho **SOBRA**.
Ao abrir, o cartão diz quantos MB ficaram e traz um botão **Apagar a pasta**, que pergunta e mostra
o caminho inteiro antes de fazer qualquer coisa.

O resto do painel sai: sem interruptor, sem recomendação, sem instruções, sem botão de jogar, e as
duas bolinhas de estado apagam. Os arquivos até estão lá — foi o launcher que os pôs — mas dizer
"DLSS 5 ligado" numa pasta sem jogo é afirmar que algo funciona quando não há o que rodar.

### O critério é conservador de propósito

O que está em jogo é apagar arquivo, então a pasta só é sobra quando tem marca **nossa** e
**nenhum executável**, em nenhuma subpasta até quatro níveis. Um `.exe` qualquer já a salva — jogo
que a loja esqueceu de registrar, port, repack. O pior erro possível aqui seria oferecer apagar a
pasta de um jogo que você ainda joga.

Só bibliotecas Steam por enquanto: é a única loja em que a pasta sobrevive à desinstalação com nome
legível e num lugar previsível. As outras ou apagam a pasta inteira ou usam identificador no lugar
do nome.


## v1.93.0

O interruptor ficou **duas vezes mais rápido**, e parou de fazer a coluna de cartões piscar.

### Instalar: 987 ms → 490 ms

Medido do clique até a tela estar certa de novo, com o diálogo de confirmação descontado (o tempo
em que a pergunta fica na tela é a pessoa pensando, não o programa trabalhando).

| | antes | agora |
|---|---|---|
| Escolher o nome do proxy | 466 ms | **1 ms** |
| Varredura de anti-cheat | ~400 ms | **1 ms** |
| Baixar/validar o addon | 248 ms | 248 ms |
| Reler a pasta | 178 ms | 155 ms |
| ReShade | 39 ms | 37 ms |
| **Total** | **987 ms** | **490 ms** |

Desinstalar caiu de 320 ms para 205 ms.

**O nome do proxy custava quatro leituras do executável inteiro.** Para decidir se o ReShade entra
como `dxgi.dll`, `d3d9.dll` ou `opengl32.dll`, o launcher procura no binário os nomes das APIs
modernas — porque a tabela de importação mente por omissão em jogo que carrega o D3D12 por
`LoadLibrary`, como o Cyberpunk 2077. A busca tem cache, mas **a chave inclui a lista de textos
procurados**: perguntar por um nome de cada vez criava quatro entradas e varria o arquivo quatro
vezes. Os quatro numa chamada só custam uma leitura.

**E o resto virou pré-aquecimento.** A varredura de anti-cheat e a leitura do executável agora
acontecem quando o cartão do jogo abre, junto das leituras que já estavam rolando ali — sem
ninguém esperando na frente da tela. Se você nunca clicar em instalar, custou uma thread de disco
ociosa.

O que sobrou é o que não dá para acelerar sem perder garantia: 248 ms validando o addon contra o
servidor (é o que impede o jogo de receber um build velho) e 155 ms relendo a pasta que acabou de
ser escrita.

### A coluna de cartões parou de sumir e voltar

A cada clique no interruptor, `GamesView.Refresh()` reconstrói a grade, a ListBox perde o
`SelectedItem` e escreve **nulo** em `Selected` pelo binding. O setter não sabia distinguir isso
de "o usuário fechou o jogo": limpava o detalhe inteiro, a coluna sumia — e voltava um instante
depois, quando a linha seguinte devolvia a seleção e o detalhe era relido do zero.

Instalar e desinstalar de fato mudam o que a grade deve mostrar, então o refresh continua. O que
saiu foi a conclusão errada tirada do nulo que ele produz.

## v1.92.1

O release da v1.92.0 não publicou, e o motivo não era o app.

A bateria de verificação reprova quando o Windows Defender acusa algo. No runner do GitHub o
serviço se declara ligado e o `MpCmdRun` mesmo assim devolve `CmdTool: Failed with hr =
0x800106ba` — o serviço recusa o pedido. O teste lia qualquer saída diferente de zero como
detecção e reprovava o release inteiro por um antivírus que nunca chegou a olhar o arquivo.

"Não consegui varrer" não é "achei alguma coisa". Reprovar sem veredito é pior do que não ter o
teste: ele deixa de significar "está limpo" e passa a significar "a infraestrutura funcionou
hoje". Agora esse caso é registrado como não executado, com o motivo — e o VirusTotal, que cobre
o Defender junto com outros 70 motores, continua sendo o portão que vale.

Os outros dez testes passaram na v1.92.0: sem executável inesperado no payload, sem packer,
manifest com `asInvoker`, instalação e desinstalação silenciosas verificadas, e a varredura do
código-fonte sem nenhuma API de injeção ou persistência.

## v1.92.0

O addon novo ficava instalado e desligado — em jogo antigo, que é justamente onde o Feeder
trabalha.

### A chave de ligar mudou de nome outra vez

Lendo a tabela de strings de um build de setembro de 2026 (2.520.576 bytes), as chaves dele são
`DirectNeuralRendering`, `DirectNeuralRenderingStatus`, `...Intensity`, `...HookPoint` e mais uma
dúzia. **Não existe** `DirectNeuralRenderingEnabled` — que era a única que o launcher escrevia.

O resultado é o pior possível: o arquivo na pasta, o `ReShade.ini` dizendo `=1` numa chave que o
addon não lê, a instalação terminando limpa, e nada acontecendo dentro do jogo.

Agora o launcher escreve as três: a curta, a com sufixo e a do esquema antigo. Não dá para saber
a versão do addon pelo arquivo (o campo de versão do PE vem lixo em alguns builds), então
escrevem-se todas — uma chave a mais num ini que aquele build não lê não custa nada; uma a menos
custa o recurso inteiro.

### E os jogos já instalados são consertados sozinhos

Reafirmar o interruptor só olhava para chave **zerada**. Um jogo instalado por uma versão
anterior fica com a chave daquela época e sem as outras: nada zerado, nada a corrigir, e o addon
novo continuava sem ligar. A regra agora é — se o interruptor está ligado por qualquer chave
conhecida e outra está ausente, escreve todas. Ausência de todas continua sendo motivo para não
tocar: aí o ini não é nosso.

Também: a varredura parava no primeiro `ReShade.ini` que consertava. Jogo de 32 bits tem **dois**
que contam, o da raiz e o do `host64`, e o segundo ficava como estava.

### `addon <arquivo>` na linha de comando

O mesmo caminho do botão "Atualizar…" dos Ajustes: guarda o build na biblioteca, leva a todos os
jogos que já têm o addon, e reafirma as chaves de ligar. Os builds circulam por fora com nomes
que mudam, e "qual deles ficou instalado?" é uma pergunta que se faz tarde demais — quando o jogo
abre e não acontece nada.

## v1.91.0

Vidro de verdade, moldura verde, e o degrau da barra de brilho.

### As superfícies ficaram translúcidas

Elas já eram um pouco, e só o suficiente para pegar o tom do halo de fundo. Agora mostram o que
há **atrás**: a grade de capas por trás do cartão do jogo, o painel por trás da janela de ajustes.
É isso que faz a pilha ter profundidade, em vez de ser uma tela colada em cima da outra.

A janela de ajustes precisou de um passo a mais: ela tinha fundo opaco próprio, então a
translucidez do painel morria contra ele. A janela agora é transparente e quem pinta é o painel.

O piso é a legibilidade — abaixo de uns 70% o texto começa a disputar com a capa do jogo.

### A moldura do jogo aberto virou verde

Laranja é a cor de "atenção aqui" no resto do app: atualização pendente, aviso, alerta. O jogo
que você abriu não é um alerta.

### O degrau na barra de brilho

Havia um segundo retângulo, mais alto e translúcido, para simular o brilho da barra. Ele era
recortado no mesmo ponto que o preenchimento, mas sobrava acima e abaixo do polegar — e o corte
reto virava um degrau visível ao lado dele. O preenchimento sozinho termina onde o polegar está,
que é quem esconde a emenda.

## v1.90.0

O selo laranja de **MOD DISPONÍVEL / SEM MOD** saiu do cabeçalho do jogo.

Ele respondia, em laranja e caixa alta, uma pergunta que o cartão do RenoDX HDR logo abaixo
responde melhor: existe mod, e ele está valendo? Lá há um interruptor e uma palavra; ali havia só
um aviso competindo com o nome do jogo.

Sobrou o que informa: a loja, a **API do jogo** e a estabilidade do mod segundo a wiki.

Com o selo, foram embora o `BadgeText` que o alimentava e as seis chaves de texto dele. O `Badge`
continua — é ele que decide a bolinha do RenoDX HDR no cartão da grade.

## v1.89.0

Faxina no cartão do jogo, e a barra de brilho deixou de ser uma barra laranja.

### As instruções foram para trás do clique que já existia

O cartão "ANTES DE INSTALAR" era o primeiro que aparecia ao abrir um jogo — antes do
interruptor, e muitas vezes dizendo apenas "leia a página do NexusMods". Instrução de leitura não
disputa espaço com o controle que decide o que acontece na pasta. Ele mora agora dentro de
**INSTRUÇÕES RENODX HDR**, junto do resto.

### O crédito virou uma linha, e a foto saiu

Quem fez o mod tinha um cartão próprio no topo, com foto do GitHub e um círculo com a inicial —
do tamanho do cartão que decide se o mod entra, e acima dele. O crédito importa; o retrato não.
Agora é uma linha dentro do próprio cartão do mod: *Criado por Fulano*, com o histórico ao lado.

### A API do jogo, no lugar do "SEM MOD"

Dizer o que o jogo **não** tem não é informação — o cartão logo abaixo já diz isso com um
interruptor. A API o jogo tem sempre, e é ela que explica por que a rota escolhida foi aquela.
Sai do mesmo lugar que já inspecionou o executável, então não custa uma segunda leitura do PE.

### A barra de brilho virou uma rampa de luz

Uma faixa laranja de tamanho variável não diz nada sobre luz. O que diz é a cor mudando ao longo
do caminho: azul frio no escuro, ciano no meio, ouro depois, branco estourado no fim — a rampa de
um corpo aquecido, que é como o olho já aprendeu a ler brilho.

Ela é pintada **uma vez ao longo de toda a barra**, e o preenchido apenas revela até onde você
chegou. A cor num ponto é sempre a mesma, então arrastar é ver o campo se abrir, e não uma barra
crescendo. O caminho todo fica visível, apagado: dá para ver aonde se pode chegar antes de
chegar.

Pintar a rampa dentro do preenchimento seria o erro óbvio — a cor mudaria sozinha conforme ele
cresce, e o azul viraria ouro no mesmo ponto da tela.

## v1.88.0

O cartão do jogo passou a **vir** em vez de aparecer pronto.

### Ele cresce de onde você clicou

Uma superfície que aparece inteira no primeiro quadro é correta e não diz nada. Movendo a origem
da escala para o lado em que o clique caiu, o painel vem **do cartão** — que é a ligação que a
Apple faz e que explica, sem uma palavra, de onde aquilo saiu.

A fração é medida na janela, e não dentro do painel: o painel tem largura máxima e fica
centralizado, então a maioria dos cartões cai fora dele e uma fração interna grudaria em 0 ou 1.
Os limites de 0,15 e 0,85 existem porque origem no canto exato lê como painel deslizando de fora,
e não crescendo.

### E assenta em vez de saltar

Escala de 0,965 a 1, não de zero: superfície que nasce de um ponto lê como truque; superfície que
já está quase no lugar lê como material chegando. A curva passa de 1 por um triz e volta — é o
assentamento.

O fundo escurece um pouco mais devagar (170 ms) do que o painel aparece (130 ms), então o painel
chega primeiro e o resto da tela recua atrás dele, em vez de tudo piscar junto.

Sair não é a entrada ao contrário: 110 ms, sem overshoot e sem crescer de lugar nenhum. Quem
fecha já decidiu, e esperar pela saída é esperar por nada.

### O que continua igual

Os cartões de dentro entram todos de uma vez, como antes — a entrada anima o painel, não o
conteúdo. E nada disso atrasa a interação: a leitura de disco do detalhe já corria em paralelo.

Um detalhe que quase passou: quem clica noutro jogo com o modal ainda saindo reabre o painel no
meio da saída. Recolher ao fim da animação sem checar isso deixaria a tela vazia com o modal
"aberto".

## v1.87.0

A roda passou a empurrar em vez de mandar. E uma correção do que a v1.84.0 anunciou.

### Primeiro, o desmentido

A v1.84.0 disse que as animações passaram a rodar na taxa do monitor, 360 Hz. **Não passaram.**
Medido dentro do app, com a rolagem acontecendo: o tique chega a cada 15,5 ms com o
`Timeline.DesiredFrameRate` sobrescrito e a cada 15,9 ms sem ele. Os mesmos ~64 Hz. A chamada
saiu do código: algo que não muda nada mensurável, com um log afirmando que muda, é pior que
nada.

### A roda agora empurra

Um tween por clique é uma sequência de arranques: cada clique começava uma curva nova em
velocidade cheia por cima de outra que estava desacelerando. Num giro seguido, isso é um trem de
solavancos no ritmo dos cliques — e é a diferença para arrastar a barra, que é **um** movimento
contínuo. Não era taxa de quadros; era a forma do movimento.

Agora o clique não diz para onde ir, ele empurra: a velocidade soma, decai por exponencial
(τ = 110 ms) e a posição é a integral dela. Não há curva para reiniciar, então girar depressa
acelera de verdade.

Com uma diferença deliberada em relação à física: **inverter o sentido mata o embalo em vez de
descontar dele**. Cinco cliques para baixo deixam a velocidade em 9000 px/s; um clique para cima
tiraria 2000 e sobrariam 7000 ainda descendo — a tela continuaria descendo depois de você mandar
subir. Nenhuma rolagem de sistema se comporta assim: girar para o outro lado é um cancelamento.

### O que não deu para medir, e por quê

Não consigo aferir taxa de quadros a partir daqui. Nesta sessão os dois relógios — composição e
animação — entregam ~102 ms, e isso contradiz o relato de que arrastar a barra é liso: se o app
compusesse a 10 Hz, a barra seria igualmente picotada. A janela roda sem ninguém olhando para
ela, e o Windows estrangula a composição nesse caso. O que dá para medir daqui é o custo do
passo, e ele é 0,1 ms — o app está ocioso entre os quadros.

Por isso esta versão não promete número nenhum de quadros. O que ela muda é a forma do movimento.

## v1.86.0

Descer rápido e subir logo em seguida: a tela continuava descendo. E a rolagem se cancelava
sozinha no meio do movimento. As duas coisas eram da roda do mouse, e as duas eram minhas.

### Inverter o sentido partia do lugar errado

Somar cliques só vale enquanto eles vão para o mesmo lado. Cinco cliques para baixo deixam o
alvo muito abaixo do que a tela mostra — o movimento ainda está a caminho dele. O clique para
cima partia **desse alvo** e tirava uma fileira dele: continuava sendo um destino lá embaixo. A
tela seguia descendo depois de a pessoa ter mandado subir, e só muitos cliques depois é que o
sentido virava.

Agora, invertendo o sentido, a conta parte de onde o olho está. Medido: cinco cliques descendo,
e um clique subindo no meio do movimento para em 23,32% — exatamente um passo acima dos 33,45%
onde a tela estava naquele instante.

### E a animação se cancelava sozinha

A v1.85.0 escutava `ScrollChanged` para descobrir se a rolagem tinha mudado por outro caminho,
comparando o deslocamento novo com o último valor empurrado. Duas coisas erradas nisso, e as
duas aparecem justamente quando se rola rápido:

A animação tiqueia a 360 Hz e o `ScrollChanged` chega por passagem de layout — vários empurrões
cabem entre dois eventos. O evento então trazia um deslocamento atrasado em relação ao último
empurrão, a comparação dava "não fomos nós", e o movimento se interrompia no meio.

Pior: esse cancelamento acontecia dentro do callback da propriedade que estava sendo animada.
Mexer na animação ali é reentrância, e o resultado não é previsível.

Quem avisa agora é o evento `Scroll` da barra, que dispara só quando a **pessoa** mexe na barra
— nunca por mudança programática. Era o único caso que precisava mesmo ser interrompido.

## v1.85.0

Quatro defeitos da rolagem que entrou na v1.84.0. Todos meus, e três deles só apareceram medindo.

### Girar rápido perdia caminho

O pior. Para saber de onde parte o próximo clique, o código comparava o último valor empurrado
com o deslocamento atual — e essa comparação tem uma corrida: `ScrollToVerticalOffset` **não muda
`VerticalOffset` na hora**, o valor só aparece depois do próximo arranjo. Uma roda girada nesse
intervalo lia os dois diferentes, concluía "alguém mexeu por fora" e recomeçava do zero.

O sintoma era exatamente o de girar rápido: parte do caminho sumia. Agora quem responde é um
sinalizador explícito de "há uma animação nossa correndo". Medido: cinco cliques a cada 10 ms
andam 50,63% da lista, exatamente cinco vezes um clique.

### A animação brigava com a barra de rolagem

Arrastar a barra enquanto a animação corria deixava os dois puxando o mesmo deslocamento para
lados diferentes — na tela, a barra escapando da mão. O mesmo valia para as setas do teclado e
para a lista rolando sozinha até o jogo selecionado.

Agora, quando a rolagem muda por outro caminho, a animação é solta onde está. Medido: barra
arrastada para 80% no meio de um movimento fica em 80%, e a roda seguinte parte de lá.

### Passo grande demais dentro do cartão

210 px são uma fileira na grade e quase um cartão inteiro dentro do modal, que é bem menor. O
passo passou a ter teto de meia tela do que está rolando — ele pertence à área, não ao mouse.

### E uma saída que sujava o estado

Quando não havia para onde rolar (fim da lista), o código anotava o alvo novo e só então saía —
deixando o alvo apontando para um lugar aonde nenhuma animação ia, e a animação em curso, ao
terminar, não fazia a própria limpeza.

## v1.84.0

A rolagem anda em vez de saltar, e as animações passaram a tiquear na taxa do seu monitor.

### As animações a 360 Hz, e não a 60

O WPF amostra animação a 60 Hz por padrão. Num monitor de 240 ou 360 Hz isso significa que cada
valor novo é repetido por quatro ou seis quadros: a tela desenha 360 vezes por segundo e o
movimento continua sendo o de 60. Não é sensação — é a taxa em que os valores mudam.

Agora a taxa vem do monitor, lida na abertura (`animacoes a 360 Hz (taxa do monitor)` no log).
Não é um número fixo: fixar 240 gastaria bateria numa tela de 60 e deixaria dinheiro na mesa numa
de 360.

### A roda do mouse ganhou quadros no meio

A roda no WPF não anima nada: cada clique salta três linhas de uma vez e para. O que faltava não
era taxa de quadros — era **interpolação**: não existia quadro nenhum entre o antes e o depois
para o monitor mostrar.

Agora o clique vira um alvo e o deslocamento caminha até ele com desaceleração em 190 ms. Os
cliques se somam: medido, três cliques rápidos andam 30,37% da lista, exatamente o triplo de um
clique — girar rápido estica o alvo em vez de reiniciar o movimento.

Vale na grade de jogos, no cartão do jogo e nas Configurações.

### Três defeitos que a medição achou no caminho

**Voltava ao topo no fim.** A animação usava `FillBehavior.Stop`, e no fim a propriedade volta ao
valor base — onde a rolagem estava quando o movimento começou. Medido: 10,13% aos 188 ms, 0% aos
220 ms.

**O alvo ficava velho.** Quando um clique substitui a animação em andamento, o `Completed` da
animação trocada nunca dispara — então o alvo nunca era limpo, e um clique dado minutos depois
partia de um número que não tinha mais nada a ver com a tela.

**A rolagem tem outros donos.** Arrastar a barra, o teclado e o próprio `ScrollIntoView` que a
lista faz ao selecionar um jogo mexem no deslocamento sem passar por aqui. Agora isso é
detectado, e o alvo antigo é descartado.

E o touchpad: a distância passou a seguir o tamanho do giro, e não só o sentido. Com o sinal
apenas, um toque leve de touchpad andava uma fileira inteira.

## v1.83.0

Escolher uma release passou a valer nos **jogos**, e a escolha passou a durar.

### Instalar chega até o jogo

O que carrega dentro do jogo é o arquivo que está na pasta dele, não o da biblioteca. Escolher
uma release e ver a versão mudar só no cartão seria a tela dizendo uma coisa e o jogo fazendo
outra — e é assim que se descobre, tarde, que a versão que quebrou continua rodando.

Instalar uma release agora leva a versão a todos os jogos que já têm o Feeder (28 nesta máquina),
e **Voltar** também: é justamente quando um jogo quebrou que se aperta esse botão.

### E a escolha não é mais desfeita sozinha

Instalar a v0.11.0-beta.2 pela lista funcionava, e quarenta segundos depois a checagem automática
reinstalava a versão padrão por cima. Uma escolha que o próprio programa desfaz é pior do que não
ter a lista.

A release escolhida fica guardada na configuração, e a busca passou a respeitar a ordem: a
release pedida agora, a que você escolheu antes, e só então o padrão fixado.

Junto do binário anterior vai a **tag** que estava valendo. Sem ela, voltar devolveria os
arquivos certos e deixaria a configuração apontando para a release nova — e a próxima checagem
traria de volta exatamente a versão de que se acabou de fugir.

## v1.82.0

As Configurações passaram a dizer **qual versão** de cada peça está instalada, e a deixar você
escolher outra.

### A versão exata, em todos os cartões

Antes: "versão desconhecida", "na biblioteca", "158 MB". Nenhuma dessas responde à pergunta que
se faz ali — *qual versão está instalada?* — e foi essa resposta faltando na tela que fez a caça
ao Feeder que derrubava o jogo demorar o que demorou: para saber o que havia na pasta, foi
preciso abrir o log do próprio add-on.

Agora:

| | |
|---|---|
| Addon do DLSS 5 | `v4.7 · 0.2026.0828.0517` |
| Ponte DX11 | `1.4.8.0` |
| DLSS5 Feeder | `0.12.0.0` |
| Runtime neural | `310.8.0.0 · 158 MB` |

Duas versões convivem no addon e as duas aparecem: a que ele escreve sobre si (`v4.7`) e a do
executável (`0.2026.0828.0517`). São coisas diferentes — a primeira é como o autor fala da
build, a segunda é o que distingue duas builds do mesmo `v4.7`. O "versão desconhecida" vinha de
uma expressão que exigia três números; o addon escreve dois.

### A lista de releases, e o botão de voltar

A Ponte e o Feeder ganharam a lista de releases do repositório, da mais nova para a mais antiga,
com **Instalar**. Os betas aparecem na lista — escondê-los seria decidir por você o que pode
instalar — mas nunca vêm pré-selecionados: o que já vem escolhido é a versão fixada, ou a
primeira estável. Com "a primeira da lista" pré-escolhida, um clique bastaria para pousar um
beta na máquina, que é o acidente que a v1.80.0 acabou de consertar.

O Feeder ganhou cartão próprio, com **Voltar** ao lado, que devolve a versão anterior guardada.

Na linha de comando, o mesmo caminho: `feeder --versao v0.11.0-beta.2`.

## v1.81.0

A versão do Feeder deixou de ser "a que estiver no topo do repositório no dia".

### O padrão é a versão testada, e ela está escrita no código

O launcher instala a **v0.12.0** — a que foi rodada num jogo e entregou quadros. Não é a mais
nova estável; é a que se sabe que funciona.

Recusar beta pelo nome da tag (v1.80.0) resolve o caso em que o defeito vem rotulado. Não
resolve o outro: uma release estável pode quebrar igual, e o launcher não tem como saber antes
de alguém jogar. Por isso a versão padrão passou a ser uma decisão tomada no código, com o
motivo escrito ao lado dela, em vez de uma consequência da data.

### E agora existe caminho de volta

Antes de sobrescrever a biblioteca, a versão que está lá vai para o lado. Se a nova quebrar um
jogo, uma linha desfaz:

```bash
RenoDXLauncher.exe feeder --voltar
```

Na noite em que a 0.12.1-beta.2 derrubou o Saints Row, voltar exigiu descobrir qual era a versão
de antes, achar o release dela e baixar o zip à mão. Guardar a anterior custa 400 KB.

O comando novo também mostra o estado, sem mexer em nada:

```
Feeder na biblioteca: 0.12.0.0
Padrão que este launcher instala: v0.12.0 — a versão que foi rodada num jogo e entregou quadros.
Guardada, para o caso de a nova quebrar um jogo: 0.12.0.0  (feeder --voltar)
```

E `feeder --novo` busca a estável mais recente para quem quiser, avisando que está saindo do
padrão testado. Os jogos só mudam na instalação seguinte — o comando diz isso também.

## v1.80.0

O launcher parou de instalar beta. Era ele que estava derrubando o jogo.

### O que acontecia

Hoje de manhã o launcher atualizou o DLSS5-Feeder da **v0.12.0** para a **v0.12.1-beta.2**, e a
partir daí o Saints Row The Third morria dois segundos depois do primeiro quadro:

```
[feed] ##### DEVICE REMOVED at checkpoint "ExecuteCommandLists" (reason 0x887A0005) #####
### CRASH RECORDED ###  exception 0xE06D7363 ... last doing: preparing work
```

Medido nos dois sentidos, no mesmo jogo e na mesma noite: com a **0.12.1-beta.2** o processo
morre em 4 segundos com `0xC0000409`; com a **0.12.0** ele passa dos 80 segundos entregando
quadros, sem uma remoção de device sequer. E sem a nossa cadeia na pasta o jogo também vive —
ou seja, o crash era nosso.

### Por que um beta desceu como se fosse estável

O `releases/latest` do GitHub promete devolver só release estável, e cumpre — desde que quem
publicou marque a caixa "This is a pre-release". A v0.12.1-beta.2 foi publicada sem essa marca,
virou a "latest" do repositório, e o launcher a adotou confiando na promessa.

Agora o **nome** também conta: uma tag com `-beta`, `-alpha`, `-rc`, `-preview`, `-dev`,
`-snapshot` ou `-nightly` não é adotada, e o launcher volta na lista de releases até achar a
estável mais recente. Não é adivinhação — esse sufixo é convenção de versionamento semântico, e
quem o escreve está dizendo exatamente isso.

Vale para todo componente que vem de release do GitHub, não só para o Feeder.

### Se você instalou hoje

Reinstale o DLSS 5 nos jogos: a biblioteca volta para a 0.12.0 e o beta sai das pastas.

## v1.79.0

Desfaz a regra que a 1.78.0 trouxe. Ela foi escrita a partir de uma falha que aconteceu uma vez
e não voltou a acontecer, e a medição depois mostrou que a conclusão estava errada.

### A Ponte volta a ser só para jogo DirectX 11 com DLSS próprio

O argumento da 1.78.0 era: o pass neural precisa de um device D3D12, um jogo de DirectX 11 não
tem, logo todo jogo DX11 precisa da Ponte. A primeira metade está certa. A conclusão não —
porque na rota do Feeder esse device **já existe**, e quem o cria é o próprio Feeder. Está no
log dele, em qualquer jogo dessa rota:

```
[feed] Color 2560x1440 R16G16B16A16_FLOAT via D3D12->D3D11
[feed] Output: D3D12->D3D11 path failed 0x80070057, trying the other direction
```

Ele importa as texturas do D3D11 do jogo para o D3D12 dele. A Ponte ali não acrescenta nada.

Medido no Saints Row The Third — DirectX 11, sem DLSS, o jogo que motivou a mudança: **sem a
Ponte na pasta** ele cria a feature 18 e entrega quadros, em duas execuções seguidas; com a
Ponte, também. A falha que serviu de prova (`0xbad00002` e o device removido logo depois) não
voltou a aparecer em nenhuma execução, com Ponte ou sem ela. Uma ocorrência sem repetição não
sustenta uma regra que põe um arquivo em toda pasta DirectX 11.

A Ponte serve ao caso oposto: jogo que tem DLSS **próprio** e roda em DirectX 11, onde não há
Feeder nenhum criando device.

### Ponte + Feeder volta ao aviso, agora dizendo a verdade

Voltou a ser reportado, mas como informação e não como bloqueio, porque está medido que os dois
convivem: o jogo roda com ambos na pasta. O que há ali é sobra — a Ponte não tem função na rota
do Feeder — e a próxima instalação a remove. O texto diz isso, em vez de mandar reinstalar algo
que está funcionando.

Chamar de bloqueio o que não bloqueia é pior do que não avisar: manda a pessoa mexer no que
está certo.

## v1.78.0

Uma correção que devolve o DLSS 5 a dez jogos de DirectX 11, e a interface refeita como sistema
em vez de trinta cores escritas na mão.

### A Ponte segue a API do jogo, e não o DLSS próprio dele

O botão de consertar apagava o pass neural em jogo DX11 sem DLSS. A regra dizia que a Ponte e o
Feeder eram excludentes, o conserto tirava a Ponte, e dentro do jogo o log passava a dizer:

```
ERROR [DLSS 5 Neural Rendering] feature 18 create failed with 0xbad00002
[feed] ##### DEVICE REMOVED at "ExecuteCommandLists" (0x887A0005) #####
```

As duas peças respondem perguntas diferentes. O **Feeder fabrica** os dados que o jogo não
entrega — cor, profundidade, vetores de movimento. A **Ponte dá** ao pass um device D3D12 onde
rodar. Num jogo de DirectX 11 esse device não existe, tenha ele DLSS ou não, e era só isso que a
condição antiga confundia ao exigir DLSS nativo para instalar a Ponte.

Então: a rota da Ponte passou a seguir a API; Ponte + Feeder deixou de ser conflito; e a leitura
da cadeia passou a cobrar **as duas** quando a rota pede as duas — ela vinha dizendo `ok` com o
pass morto dentro do jogo, que foi por onde o defeito escapou da auditoria.

Quem já instalou: reinstale o DLSS 5 nos jogos de DirectX 11 sem DLSS — INSIDE, Shadow of Mordor,
Sekiro, Sonic Frontiers e afins. A tela agora mostra o elo `DX11 bridge` faltando neles.

### Os ícones viraram um conjunto só

Havia meio conjunto preenchido e meio vazado, com pesos diferentes — e "Atualizar lista" e
"Procurar atualizações" eram **a mesma seta circular**: dois botões distintos com o mesmo desenho.

Agora são glifos de traço no mesmo grid de 24 com o mesmo peso, que é a regra que a Apple põe
acima de qualquer outra para ícone de interface: mesmo tamanho, mesmo nível de detalhe, mesma
espessura, conversando com o peso do texto ao lado.

A barra tinha cinco pilhas contornadas em fila, todas do mesmo peso — uma parede de botões em que
nenhum era mais importante que outro. Viraram símbolos sem moldura em dois grupos, o que mexe na
lista e o que abre outra janela, com a moldura aparecendo só sob o ponteiro.

### Superfícies de vidro, e não trinta cores soltas

Cada cartão trazia o próprio par de cores no código, com raio e espaçamento próprios. São seis
estilos agora, e o que faz uma superfície parecer vidro não é transparência: é a luz vindo de
cima — uma linha clara na borda superior que se apaga na inferior. Junto, a barra de título
escura e o fundo Mica do Windows 11.

### O RenoDX HDR ganhou selo OPCIONAL

Ele é o primeiro cartão da coluna e traz o nome do launcher, então lia como *o* recurso — quem só
queria DLSS 5 ficava achando que teria de ligar o HDR antes. São add-ons independentes, cada um
com seu interruptor.

### Miudezas

Dois textos que saíam cortados na borda — o aviso da ponte DX11 e o do runtime da comunidade —
porque texto que quebra linha dentro de painel horizontal recebe largura infinita e nunca quebra.

## v1.77.0

Uma varredura que audita a biblioteca inteira, e três defeitos que ela encontrou — dois deles
no próprio launcher, acusando de quebrado um jogo que funcionava.

### `auditoria`: os 54 jogos, um por um

"Está tudo funcionando?" não se responde clicando em 54 cartões, e abrir 54 jogos não cabe num
dia. O que dá para responder sem abrir nada é o que a tela já responde por jogo: os elos estão
todos no lugar, e há algo na pasta disputando espaço.

O comando novo faz isso pela **mesma leitura que a janela usa**. Para tanto, a leitura da cadeia
saiu da view model para um serviço próprio: uma segunda implementação discordaria da tela em
algum caso, e o ponto do comando é justamente não discordar. Ele só lê — ao final lista os
comandos de conserto com o jogo na frente, porque mexer em dezesseis pastas sem ninguém ter
pedido não é auditar.

### A pasta do usuário aparecia como jogo

`C:\Documents and Settings` é uma junction que volta para o perfil. `users` estava na lista de
pastas a pular; ela não. A varredura desceu por ali, achou um `.exe` três níveis abaixo, em
Downloads, e ofereceu a pasta inteira do usuário como se fosse um jogo — e o launcher chegou a
instalar DLSS 5 lá dentro.

Agora o perfil é recusado por **caminho**, e não por nome: ele se chama como a pessoa se chama,
e nome nenhum numa lista pega isso.

### Dois vermelhos falsos em jogo de 32 bits

Os dois na mesma rota: aquela em que o pass roda no `host64\`, e não no processo do jogo.

**Ray Reconstruction era cobrado na raiz.** Nessa rota o launcher copia para o host o runtime
neural e o Super Resolution — RR não, de propósito. Medir a raiz só encontrava sobra de
instalação antiga, e o elo ficava vermelho para sempre num jogo certo.

**E "desatualizado" era reportado como "ausente".** O `dlss5-feed.addon32` de uma versão
anterior do launcher não bate com o tamanho da biblioteca nem com o do embutido de hoje, e a
checagem de integridade concluía que o Feeder não estava lá. O que aquela checagem existe para
pegar é arquivo truncado, e disso quem dá conta é o cabeçalho PE.

Quatro jogos voltaram a aparecer inteiros: Hitman: Blood Money, Hitman: Absolution, Bully e
Saints Row 2 — todos rodando DLSS 5 enquanto a tela os dava por quebrados. Os quatro casos
viraram teste, inclusive os dois inversos: o elo continua vermelho onde a falta é de verdade.

## v1.76.0

Faxina na tela do jogo: um cartão a menos, um recurso com o nome certo, e as instruções legíveis.

### O MFG virou "patch", é experimental, e só aparece na RTX 40

O nome importa: não é um recurso do launcher, é **alteração de código da NVIDIA em memória**. Vai
quebrar com atualização de driver ou de jogo, e agora o cartão diz isso com um selo
**EXPERIMENTAL** ao lado do título, antes de você ligar — e não numa nota depois.

E ele **sumiu da RTX 50**. Aquela placa já faz Multi Frame Generation de fábrica até 4x, pelo menu
do próprio jogo e pelo app da NVIDIA. O que o patch acrescentaria ali são 5x e 6x, que o próprio
autor chama de experimentais: não é motivo para um cartão a mais na tela de quem já tem o recurso
funcionando. A linha de comando ainda explica o porquê, para quem for procurar.

Continua aparecendo em placa que não alcança **se já estiver instalado** — trocar de placa não
pode deixar o recurso ligado sem caminho de volta.

### O cartão de runtimes de DLSS saiu

Ele oferecia atualizar ou restaurar os runtimes do jogo, que é o que o DLSS Swapper faz, e não é
o que este launcher é. Pior: o botão de restaurar desfaz o que a instalação do DLSS 5 pôs, então
dois controles do mesmo painel puxavam para lados opostos.

Havia ainda uma promessa falsa: o bloqueio do MFG mandava "atualize os runtimes de DLSS acima", e
aquele botão **nunca trocou Frame Generation** — só Super Resolution e Ray Reconstruction, por
decisão antiga e documentada (o `nvngx_dlssg.dll` anda em conjunto com o Streamline do jogo). O
texto agora diz a verdade.

O aviso de topo de "runtime trocado em N jogos", com o botão de devolver todos, continua.

### Instruções do mod: parágrafo é parágrafo, lista é lista

As notas do RenoDX vêm de uma wiki, escritas em markdown de rascunho — uma frase, às vezes um
`IMPORTANT:` na frente, e itens com hífen. Tudo isso chegava na tela como um parágrafo corrido,
com os hífens no meio das frases.

Agora o texto é quebrado no que ele já é: o parágrafo separado dos itens, cada item com marcador
fora da margem, e o `IMPORTANT:` como selo no cabeçalho em vez de ocupar o começo da primeira
frase. O cabeçalho some inteiro quando não há nada nele, em vez de deixar um vão no topo de toda
nota sem título.

### O cartão de conflitos parou de mentir o nome

Um achado sobre a **pasta** — "a ponte e o Feeder estão os dois aqui" — mostrava o nome da pasta
na coluna do arquivo, então a lista dizia `Control`, como se existisse um arquivo com esse nome
atrapalhando. Agora diz o que de fato está em conflito:
`dlss5-dx11-bridge.addon64 + dlss5-feed.addon64`.

E o botão "afastar os que bloqueiam" só aparece quando há algo que ele consegue afastar. Antes
aparecia com qualquer bloqueio, inclusive nos que se resolvem reinstalando — e ali ele não fazia
nada.

## v1.75.0

Jogo com **DLSS 1.0** deixa de ser recusado, e o runtime neural passa a ser encontrado sem rede.

### Final Fantasy XV, e todo jogo preso na primeira geração do DLSS

O caminho do Feeder precisa de um `nvngx_dlss.dll` moderno para ter uma feature de Super
Resolution sobre a qual rodar o passe. O FFXV traz a geração 1.0, que é **outra API e ocupa o
mesmo arquivo**. Os dois não cabem no mesmo jogo — e o launcher decidia sozinho pelo "não": o
runtime ficava intocado, a cadeia ficava incompleta, e a tela não dizia que havia uma escolha.

A recusa estava fundamentada numa falha real e medida: o jogo morre ao terminar de carregar um
save. Mas ela só acontece porque **o jogo** chama a API 1.0. Com o DLSS desligado nas opções dele,
a chamada não acontece e o arquivo moderno serve só ao Feeder.

Então virou pergunta, com a instrução junto. Um modal antes de instalar, como já acontece com
anti-cheat, e `--trocar-dlss1` na linha de comando. O original vai para `.renodx-bak`: desligar o
DLSS 5 devolve o DLSS 1.0 do jogo. E o primeiro passo manual ao terminar é o que evita a falha —
*NO JOGO: desligue o DLSS nas opções gráficas*.

Verificado no FFXV desta máquina: 3.600 quadros entregues a 2560x1421, motion vectors 100%
não-nulos (média 38,4 px), profundidade com variância real, NGX evaluate em 0,71 ms, 105 fps.

### O runtime neural sem depender de rede

A busca automática passa a olhar primeiro as pastas das **outras ferramentas de DLSS instaladas**
na máquina, antes de Downloads, Desktop e pastas de jogo. Várias delas embarcam esse runtime de
165 MB; quem já tem uma não precisa baixar de novo, e sem internet é a diferença entre funcionar
e não funcionar.

E a busca ficou correta, não só mais ampla: ela só aceita uma cópia que traga os **kernels desta
placa**, lidos com o leitor de fatbin do próprio launcher. Antes pegava a primeira cópia de
tamanho plausível — o mesmo buraco que a 1.72 fechou na busca pela rede e que continuava aberto na
busca em disco. Um runtime sem os kernels certos instala inteiro e não roda nada.

### `instalado <jogo>`: o que o launcher pôs, e o que devolve

Lista, a partir do disco, tudo que a instalação colocou na pasta, os originais guardados que
voltam ao desligar, a chave de carga antecipada e qual proxy o ReShade ocupa.

É de propósito uma leitura do disco e não um manifesto gravado na instalação: um manifesto
descreve o que o instalador *achou* que fez, e envelhece quando alguém renomeia um arquivo,
atualiza o jogo por cima ou passa outra ferramenta ali. A lista montada dos marcadores que a
própria desinstalação consulta não pode divergir da realidade.

## v1.74.0

**Multi Frame Generation acima do teto de fábrica**, com interruptor por jogo, em qualquer jogo
com Streamline — não só no Cyberpunk 2077.

### O que trava o MFG na RTX 40

O DLSS 4 gera até três quadros por quadro renderizado (4x), e a NVIDIA reserva tudo acima de 2x
à série RTX 50. O limite não é do silício: são duas comparações em código, e dá para ler as duas.

| arquivo | instrução | o que ela faz |
|---|---|---|
| `nvngx_dlssg.dll` | `test dl,dl` / `je` | pergunta "este dispositivo pode MFG?" e, no não, pula o trecho que liga os modos acima de 2x |
| `sl.dlss_g.dll` | `mov edx,5` / `cmp ecx,edx` / `cmovb edx,ecx` | trava o número de quadros gerados no menor entre o pedido e o teto |

Neutralizar as duas **na memória do processo** (nunca no arquivo em disco) destrava 2x até 6x.
Numa RTX 40 é a diferença entre ter e não ter o recurso; numa RTX 50 o teto sobe de 4x para 6x.

Destravar sozinho não basta na RTX 40: a Ada tem um defeito de compactação no meio do intervalo,
e com mais de um quadro gerado as amostras colapsam para o centro em vez de ocupar as posições
temporais pedidas — mais FPS no contador, nenhuma fluidez a mais. A correção D157 reescreve em
memória o programa temporal do slot 9 pelo que a Blackwell usa, e só se aplica com a placa
confirmada como Ada pela capacidade de computo (8.9) via CUDA. Não confirmando, tudo volta ao 2x
nativo em vez de entregar quadros errados.

### Como usar

Um cartão novo no modal do jogo, com interruptor e os multiplicadores de 2x a 6x. Com o recurso
ligado, trocar o multiplicador reescreve o arquivo que o add-on vigia — **vale com o jogo aberto**,
sem reinstalar nada. Pela linha de comando:

```
RenoDXLauncher.exe mfg "hogwarts legacy" --x 4
RenoDXLauncher.exe mfg "cyberpunk" --check
RenoDXLauncher.exe mfg "control" --off
```

O cartão diz o que o patch muda **naquela placa**, avisa quando a escolha entra em território
experimental (5x e 6x, assim chamados pelo autor do patch), e relata o que a última sessão do jogo
de fato fez — número lido do próprio add-on, de dentro do jogo, e não deduzido de ter copiado
arquivos.

### O binário é nosso, e o fonte está no repositório

`native/mfg/` traz o fonte C++ com a licença MIT preservada. É um fork de
[dashdogy/RTX40MFG-Unlock](https://github.com/dashdogy/RTX40MFG-Unlock), que entrega um plugin
`.asi` do Cyber Engine Tweaks — só do Cyberpunk 2077. O patch em si nunca foi específico daquele
jogo: ele mira o Streamline. Quatro mudanças trocam o carregador e nada da lógica de patch:

1. **Add-on do ReShade** em vez de `.asi`. O ReShade é o carregador que este launcher já instala
   em todo jogo, e o `LoadFromDllMain` dele entra antes de o jogo criar a feature de Frame
   Generation — que é o momento de que este patch precisa.
2. **Arquivos de controle com nome próprio** (`renodx-mfg.json`, `renodx-mfg-status.json`), lidos
   ao lado do módulo. O upstream cai num `config.json` na raiz do jogo, nome comum demais para se
   escrever nele às cegas.
3. **Ganchos procurados em todo módulo carregado**, e repetidos por um minuto. O upstream só olha
   a tabela de importação do executável: no Cyberpunk basta, em jogo Unreal quem importa é um
   plugin que carrega depois.
4. **Confirmação da placa sem depender de gancho**: não havendo confirmação em 2 s, o CUDA é
   consultado direto — uma placa CUDA, e a capacidade de computo dela decide.

### Jogos soltos: repack, port portátil, cópia feita à mão

A varredura de disco exigia que o **nome da pasta constasse no catálogo**, e esse portão era um
erro de premissa: o catálogo diz quais jogos têm mod de HDR do RenoDX, não quais jogos existem.
DLSS 5, ReShade e o add-on neural genérico funcionam em jogo que o catálogo nunca ouviu falar — e
era justamente o repack, cuja pasta se chama `Mortal.Shell.II-InsaneRamZes`, que nunca casava. O
jogo estava no disco e o launcher fingia não ver.

No lugar do portão, a pergunta certa: **esta pasta tem um executável que parece um jogo?** Quem
responde é a lista que já separava `CrashBandicoot.exe` de `crashreport.exe`, agora consultável
pela varredura. E quem dá o nome é o resolvedor que já existia para pastas adicionadas à mão, que
tira "Mortal Shell II" daquela pasta pelo `ProductName` do executável.

Uma pasta também passa a ser reconhecida como biblioteca **pelo conteúdo**, e não só por se
chamar `Games`: dois ou mais filhos que parecem jogo, e ao menos metade deles. Então
`D:\MinhaColecao\<jogos>` funciona.

Medido nesta máquina contra o [DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper), que
já fazia isso: antes ele achava três pastas soltas que o launcher não achava; agora os dois acham
as mesmas, e o launcher continua cobrindo cinco lojas que ele não varre (Xbox, Ubisoft, EA,
Battle.net e Rockstar).

### Recusas honestas

O cartão não some quando não dá: ele explica. Medido nesta máquina:

| jogo | runtime de FG | resposta |
|---|---|---|
| Hogwarts Legacy | 310.8.0 | disponível |
| Control | 310.8.0 | disponível |
| Cyberpunk 2077 | 310.1.0 | recusado, com o caminho para resolver (atualizar os runtimes) |
| A Plague Tale: Requiem | 3.1.1 | recusado |

Placa abaixo de Ada é recusada dizendo por quê: DLSS Frame Generation não existe em RTX 30 ou
anterior, e não há o que destravar.

### O que foi verificado, e o que não foi

Verificado nesta máquina (RTX 5090), no Hogwarts Legacy, lendo o log do próprio add-on:

- os dois patches entram: `Streamline maximum: patched RVA 0x59519` e
  `NGX device support: patched RVA 0x61484`;
- o jogo passa a listar `[STREAMLINE_DLSSG_MODE_X5]` e `X6` na opção de Frame Generation — sem
  tradução, porque são modos que não deveriam existir e a localização do jogo não tem texto
  para eles;
- a confirmação de placa roda e **recusa corretamente**:
  `D157 adapter verification (cuda): capability=12.0 verified=0`.

Não verificado: o comportamento em hardware Ada. Esta máquina não tem uma RTX 40, então a
correção D157 nunca chega a se aplicar aqui — o que dá para provar é que o portão que a governa é
alcançado e responde certo, recusando 12.0 onde aceitaria 8.9.

Em jogo cujo Streamline é carregado por plugin (Hogwarts Legacy é um), o launcher eleva o teto mas
não comanda o valor; quem escolhe passa a ser o menu do próprio jogo, que agora lista x5 e x6. O
cartão diz isso depois da primeira sessão, em vez de deixar a pessoa concluir que o número da tela
foi ignorado.

## v1.73.0

Correções de interface: cards da grade, modal do jogo, troca de idioma e controles.

### Cards da grade

- A capa saía **quadrada por cima dos cantos arredondados** do card (e da capa grande do
  modal): o `Border` do WPF arredonda só o que ele mesmo pinta e não recorta os filhos. A capa e
  o escurecido do título agora têm o próprio raio, concêntrico com a moldura.
- O aviso "nenhum jogo encontrado" **piscava durante a abertura**, por cima da barra de
  progresso, antes de a varredura olhar o disco. Só aparece com a carga terminada.
- Largura mínima da janela subiu de 1100 para 1240 px: abaixo disso a barra de cima
  transbordava e o botão do guia era cortado (em português, mais ainda).
- O card se apresentava ao leitor de tela (e à automação) como
  `RenoDXLauncher.ViewModels.GameItemVm`; agora se apresenta pelo nome do jogo.

### Modal do jogo

- **Modal em branco**: com o filtro "instalados" ativo, desligar o mod HDR reavaliava o filtro,
  o card sumia de baixo do modal, a lista perdia a seleção e o modal ficava aberto sem título,
  sem capa e com os botões mortos. O jogo aberto no modal nunca sai mais da grade; ela se acerta
  ao fechar.
- O mesmo caso pela outra ponta: recarregar a lista com o modal aberto esvaziava a grade e
  deixava o modal apontando para um jogo que não existe mais. O modal fecha antes da troca.
- A pastilha de gravidade dos conflitos mostrava o nome cru do enum ("Bloqueio", "Aviso",
  "Info") em qualquer idioma, inclusive em inglês.
- O bloco monoespaçado das notas (trecho de .ini, argumento de linha de comando) ganhava uma
  segunda moldura: o template da caixa de texto fixava 1 px de borda e ignorava o
  `BorderThickness=0` pedido.
- O botão de Reparo encolhia em direção ao canto superior esquerdo ao ser pressionado
  (`CenterX/CenterY` em pixels no lugar de uma origem relativa).

### Troca de idioma

- Trocar o idioma **zerava o filtro da grade em silêncio**: a combo recebia a lista nova, não
  achava o item de antes, ficava em branco e devolvia -1. O índice é preservado, e -1 nunca é
  aceito.
- Textos que ficavam no idioma antigo até reiniciar: o botão da correção de FG, "Atualizar N",
  o aviso de versão nova do launcher, o cabeçalho das notas do motor, os elos da cadeia, o
  resumo de conflitos, o motivo do bloqueio, as notas e o tooltip/crédito do mantenedor. A tela
  de detalhe é relida na troca.

### Controles

- `CheckBox` e `Expander` vinham com o tema claro do Windows (caixa branca, seta num círculo
  branco), brilhando no painel escuro. Ganharam o mesmo desenho dos demais controles.

### Diagnóstico

- Erros de binding do WPF, que antes só apareciam na janela de Output do depurador, vão para o
  log do launcher (`binding: ...`). Um `{Binding}` quebrado deixa um pedaço da tela vazio sem
  exceção nenhuma; agora deixa rastro.

## v1.72.0

DLSS 5 em **toda RTX**, e o add-on **dentro do launcher** — sem baixar nada, sem colocar arquivo
na mão.

### Por que só funcionava em RTX 50

O runtime neural é uma biblioteca CUDA: o código de GPU vai embutido em registros `fatbin`, um
por arquitetura. Lendo esses registros dentro dos arquivos (`tests/ChainProbe --sm`, ~70 ms num
arquivo de 165 MB), a resposta aparece inteira:

| build | kernels que traz | placas |
|---|---|---|
| `310.8.0` — o da NVIDIA, assinado | `sm_120` | **só RTX 50** |
| `310.8.0-RTX40` | `sm_89`, `sm_120` | RTX 40 e 50 |
| `310.8.SF` / `310.8.SF-v2` | `sm_75`, `86`, `89`, `120` | RTX 20, 30, 40 e 50 |

Numa RTX 20/30/40, o build da NVIDIA instala inteiro e **não roda**: não há kernel para a placa,
e ninguém reporta isso — nem o add-on, nem o jogo, nem o log. Cadeia verde, tela igual.

O launcher já escolhia um `.SF` fora da série 50 desde a 1.59, mas **a tela nunca chegava lá**:
`CheckNeuralAsync` só buscava o runtime quando a placa era Blackwell, então numa RTX 30 o cartão
mostrava "falta o nvngx_dlssnr.dll" com o botão que resolveria isso desabilitado. O CLI já não
tinha essa trava — o que explica o sintoma ser "não funciona na interface".

### O que passou a acontecer

- **A escolha é por arquitetura, não por "é Blackwell?"**: `sm_120` recebe o build assinado da
  NVIDIA, `sm_89` o build com kernels de Ada, `sm_86` e `sm_75` os universais. O
  `310.8.0-RTX40` entrou junto — ele existe como release do RHI desde 30/08 e não está no
  manifesto que o launcher lê, então uma RTX 40 ficava com o caminho FP16, mais caro do que
  precisava.
- **O arquivo tem a palavra final.** Depois de baixar, o launcher lê os `fatbin` e, se não houver
  kernel para a placa, passa para o próximo candidato em vez de instalar 158 MB que nunca rodam.
  A ordem por geração só decide a fila.
- **Um runtime que já esteja na biblioteca e não sirva** vira um bloqueio com o motivo escrito —
  "este build atende sm_120; a sua placa é RTX 40" — em vez de silêncio. A leitura fica em cache
  ao lado do arquivo, então não custa nada nas telas seguintes.
- **A trava de Blackwell saiu da interface**: qualquer RTX busca o runtime sozinha.
- O texto de recusa dizia "precisa de uma RTX série 50". Agora diz o que é verdade: precisa de
  tensor core, o que toda RTX tem.

### O add-on vem junto

O add-on neural (build 4.70, 1,7 MB) agora é **embutido no executável** e sai dele direto para a
biblioteca, conferido por SHA-256. Ninguém precisa procurar arquivo em Discord nem largar nada em
pasta nenhuma.

Isso também consertou uma falha que já estava em produção: a URL fixada de onde ele era baixado
(`zhubaohi/FF7R-DLSS5`) **passou a responder 404** quando aquele release trocou de arquivo. Numa
máquina limpa a instalação inteira parava em "falta o renodx-neural.addon64 na biblioteca" — um
instalador de um clique cujo primeiro passo virava "vá achar um DLL". Uma cópia mais nova que o
usuário tenha continua ganhando da embutida.

### Outras duas URLs mortas

- **Feeder**: desde a v0.8.0 o projeto publica um ZIP, e as quatro URLs de arquivo solto que o
  launcher usava respondiam 404 — todo jogo sem DLSS ficava sem rota em máquina limpa. Agora vem
  do pacote da última release (hoje 0.12.0), com as peças todas da mesma versão: o add-on de 32
  bits e o processo auxiliar falam um protocolo entre si, e baixá-los de releases diferentes os
  deixaria incompatíveis.
- **Ponte DX11**: o projeto virou `NIGos/dlss5-bridge` e o asset foi renomeado junto; o log desta
  máquina tem dezenas de "atualizar ponte: 404". Agora o asset é resolvido pela página de release
  por padrão de nome, o que sobrevive ao próximo rename. Voltou a baixar (v1.4.7, 465 KB).

### Verificado

Reinstalação do zero com a biblioteca apagada: o add-on saiu do executável (hash confere), o
Feeder veio do pacote 0.12.0, a ponte baixou, e o *Just Cause 2* rodou com o passe neural
avaliando. A escolha por placa está coberta no SmokeTest; o que **não** dá para provar nesta
máquina é o passe rodando numa RTX 20/30/40 — aqui só há uma RTX 5090. O que está provado é que
cada placa recebe um build que contém os kernels dela, lido do arquivo.

## v1.71.0

Uma caçada a bugs no código inteiro: 62 defeitos confirmados por revisão adversarial (cada achado
passou por um cético independente antes de virar tarefa), todos corrigidos, e o SmokeTest passando
inteiro pela primeira vez em vários releases.

### Desinstalar desinstala

O `Remove` só conhecia a pasta do jogo. Num jogo de 32 bits, desligar o DLSS 5 deixava exatamente o
que roda: o `dlss5-feed.addon32` no jogo e o `host64\` inteiro — host, ReShade, addon neural, 271 MB
de runtimes e um `ReShade.ini` com o interruptor em 1. O `IsApplied` lê esse ini, então o botão
voltava para "ligado" e o host subia junto com o jogo como se nada tivesse acontecido. O Just Cause
2 desta máquina ficou assim depois de um desligar, e foi o que fez o bug aparecer.

Agora o `Remove` espelha o `IsApplied`: zera o interruptor do `host64\ReShade.ini` primeiro (se a
pasta resistir, o ini já diz desligado), apaga `host64\` inteiro (tudo ali é nosso) e o addon32,
tira o `renodx-dlss5.addon64` renomeado que tem o marcador `.renodx-ours` e sua entrada no
`LoadFromDllMain` — um build da comunidade sem marcador continua onde está — e devolve o que os
tradutores guardaram: os cinco DLLs do DXVK 1.10.3 saem e o `dxgi.dll.pre-dxvk` (o ReShade que o
instalador tirou do caminho) volta; o DXVK de D3D9 e o dgVoodoo saem com seus `.renodx-bak`; o
OptiScaler sai só quando fomos nós que o pusemos. Antes, `RemoveD3d10` e `OptiScalerService.Remove`
não eram chamados por ninguém.

### Instalar não deixa a pasta pela metade

- O DXVK é baixado ANTES de o dgVoodoo sair e de o proxy do ReShade ser guardado. Uma falha de
  download (offline) deixava a pasta sem ReShade e o resultado saía `[ok]`, porque a detecção lida
  antes ainda dizia "proxy presente".
- `Apply` não copia um segundo addon genérico quando `renodx-dlss5.addon64` já está na pasta.
  Reinstalar deixava `renodx-neural.addon64` ao lado e os dois no `LoadFromDllMain` — carga dupla
  e o 0xc0000005 que o próprio arquivo documentava.
- No caminho de 32 bits o addon de 64 não fica mais na raiz do jogo: era ele o "error code 193"
  que o ReShade logava em toda rota de 32 bits.
- Jogo Vulkan nativo de 32 bits recebe as metades com transporte Vulkan, e não o addon32 oficial
  que só fala D3D11.
- Um `renodx-dlss5.addon64` diferente do nosso é guardado como `.renodx-bak` antes de ser
  substituído; um `.pre-dxvk` existente nunca é sobrescrito (o intruso vai para `.pre-dxvk.2`).
- Downloads vão para um `.part` e só ganham o nome final quando o tamanho bate com o
  `Content-Length`: conexão que caía no meio deixava arquivo truncado que todo fetch seguinte
  aceitava como pronto (Feeder, OptiScaler, índice de runtimes).
- Na rota DXVK de D3D9 o ReShade recebe `RESHADE_DEPTH_INPUT_IS_REVERSED=0` e
  `DepthCopyBeforeClears`, como já recebia na rota dgVoodoo — motor pré-reversed-Z é o mesmo nos
  dois tradutores.

### A camada Vulkan tem um manifesto por bitness

O manifesto compartilhado era um só, mas o `library_path` tem bitness: instalar um jogo de 32 bits
reescrevia o JSON apontando para `ReShade32.dll`, e as duas chaves do registro (64 e WOW6432Node)
apontavam para o mesmo arquivo. Depois do Just Cause 2, DOOM Eternal e Baldur's Gate 3 ficaram sem
camada. Agora são `ReShade64.json` e `ReShade32.json`, cada um na sua chave; `IsRegistered` confere
o valor do registro, o arquivo e a DLL apontada (uma entrada pendurada conta como não registrada e
é refeita); o manifesto único é aposentado na primeira instalação, e a bitness irmã volta ao
registro quando a DLL dela já está na pasta compartilhada.

### Verificação de origem mais dura

- `IsGenuine` exige cadeia de certificado confiável, não só "subject contém NVIDIA Corporation".
- Um `nvngx_dlssnr.dll` vizinho só entra no conjunto Streamline da biblioteca se passar na
  verificação — antes um arquivo não verificado fazia `Repair` lançar para sempre.
- `Kind` e `Version` vindos do índice são sanitizados antes de virar segmento de caminho.
- `7zr.exe` fixado por versão, tamanho e SHA-256; o `.7z` do OptiScaler conferido pelo digest que
  o GitHub publica. A guarda de zip-slip compara caminhos completos, não prefixo de string.
- Um build `.SF` instalado pelo `FetchRuntimeAsync` grava `runtime.custom`, então a tela não o
  chama mais de "assinado pela NVIDIA".
- Arquivo baixado corrompido (índice, manifesto, zip) é apagado para a próxima tentativa baixar
  de novo, e o cache só é gravado depois de o corpo ser parseado.

### Linha de comando

`dlss5 <jogo>` respeita o tradutor escolhido na interface (antes jogava instalações dgVoodoo de
volta para o DXVK) e recusa jogo com anti-cheat como `install` já fazia (`--all` pula com
`[pulado]`). `fix` deixou de dizer "ReShade já presente" com o ReShade ausente e de sair com 0 em
bloqueio ou falha. `install` desfaz o proxy do ReShade que ele mesmo pôs quando o download do
addon falha — e só esse. `add` recusa pasta-depósito (Downloads, raiz de disco). Os textos do
`neural` e do `--check` acompanham RTX 20/30/40 e o dgVoodoo em D3D9 de 64 bits.

### Tela

O rollback de um download falho não apaga mais um proxy do ReShade que já existia antes. Instalar
e remover refazem a cadeia da pasta (o `Dlss5Ready` ficava com a leitura velha). Trocar de
executável enquanto o detalhe carrega, progresso de um jogo anterior escrevendo no status do
atual, e a leitura do `PinnedExes` fora da thread de UI: as quatro corridas fechadas com o token
de detalhe. Ajuste sem valor no ini deixa de nascer "sujo".

### Arquivos de terceiros nunca somem sem backup

`dgVoodoo.conf` e `dgVoodooCpl.exe` do usuário vão para `.renodx-bak` antes de serem escritos e
voltam no remove; o launcher marca o que é dele com `.renodx-ours` e não guarda o próprio
`D3D9.dll` como se fosse o original. O `OptiScaler.ini` que já existia volta no remove. `Afastar`
não apaga mais um arquivo já afastado (numera). O scanner de conflitos deixou de acusar o
OptiScaler que o próprio launcher instalou.

### Detecção e persistência

- Battle.net: só as pastas do cliente são ignoradas (`D:\Battle.net Games\Diablo IV` voltou).
- Capa: cache de "não achei" vencido volta a consultar; "Portal" não recebe a capa de "Portal 2".
- ExeHint da Epic com barra normal não duplica candidato. Entrada do índice sob slug compartilhado
  herda o status da wiki.
- Mod desativado pelo usuário continua desativado ao atualizar. Cache de settings vencido serve
  quando está sem rede. Falha de I/O ao ler o `config.json` não é mais tratada como corrupção (o
  próximo `Save` não grava padrões por cima). Cache de download compara ETag, não só tamanho.
  `verify` só reconhece addon `renodx-*` como o mod carregado. Remover um mod apaga o registro dele.
  "Enable HDR in Windows" não vira "ligue o HDR no jogo".
- `IsBlackwell` não lê "RTX 5000 Ada Generation" como Blackwell. `ReassertEnabled` lê os dois
  esquemas do ini.

### Segunda rodada: o que a verificação dos consertos ainda achou

Cada conserto passou por um cético independente lendo o diff contra o cenário original. Cinco
saíram parciais e doze regressões apareceram; todas fechadas:

- **Só o que é nosso sai.** O DXVK que o launcher instala ganha marcador (`d3d9.dll.renodx-ours`,
  `d3d10core.dll.renodx-ours`); um DXVK que o usuário trouxe por conta própria fica, na
  desinstalação e na troca de tradutor. Instalações do dgVoodoo e do OptiScaler feitas antes de
  existir marcador são reconhecidas pelo binário idêntico ao da biblioteca, e um `.renodx-bak`
  que é o nosso próprio binário não é "restaurado" (era assim que um wrapper sem conf sobrava).
  O marcador de um addon travado por antivírus não some antes do arquivo.
- **Backups nunca se sobrescrevem:** um segundo intruso num nome do D3D10 vai para `.pre-dxvk.3`,
  e o ReShade que já existia na pasta vai para `.renodx-bak` antes de o nosso entrar — e volta
  no remove.
- **O interruptor neural liga pelo instalador inteiro**, não pelo `Apply` solto: desligar tira
  tradutor, host64 e addon32, e religar precisa refazer tudo isso.
- **Camada Vulkan:** máquina que subiu de versão com o `ReShade.json` único de pé continua
  "registrada" numa reinstalação sem elevação (a migração fica para a próxima execução elevada),
  e a bitness irmã volta ao registro assim que a DLL dela está na pasta compartilhada.
- **Mensagens que dizem a verdade:** raiz de certificado ausente no Windows é dito como tal, e
  não como "não assinado"; uma pasta de origem cujo único runtime foi recusado não vira "pasta
  não encontrada"; `--dgvoodoo` num jogo D3D10 volta a avisar que foi ignorado.
- **CLI:** `dlss5 --all` grava só as escolhas de tradutor desta execução sobre um config
  recarregado, em vez de despejar um snapshot de minutos atrás por cima do que a interface
  salvou; raiz de disco é recusada como pasta-depósito.
- **Tela:** uma fixação de exe feita enquanto a varredura de fundo ainda roda não é sobrescrita
  pela detecção; instalar explicitamente um mod que estava desligado o religa (só a atualização
  em lote preserva o desligado).
- **Detecção:** "Final Fantasy VII Remake" volta a casar com "Intergrade" (a régua de algarismo
  romano olha o resto do nome, não o nome inteiro); o `InstallLocation` do Battle.net só vale
  como pasta do cliente quando é mesmo a pasta do cliente.
- **Config:** um `config.json` travado por 200 ms na abertura não desliga mais a persistência
  da sessão inteira — o `TrySave` tenta reler antes de gravar, e uma recusa fica registrada.
- O import de runtime pela tela deixa de passar 158 MB por `%TEMP%`; a guarda de zip-slip não
  rejeita mais tudo quando o jogo está na raiz de um disco.

### Testes

O SmokeTest ganhou "desinstalar desinstala" (layout de 32 bits completo, DXVK D3D10 sobre um
`dxgi.dll` ocupante, addon com marcador e um build da comunidade sem) e "Apply não duplica". O
teste do ETag falhava havia vários releases sem defeito no código: o servidor gera ETag de
mtime+tamanho, que o código ignora de propósito, e o teste passava `Size=1`; agora simula um build
antigo do jeito que o código reconhece. Todos os testes passam.

## v1.70.0

Direct3D 10 deixou de ser recusado. O Just Cause 2 — o jogo que motivou a recusa — roda DLSS 5.

### A API que nenhuma camada falava

Até a 1.69 um jogo Direct3D 10 recebia uma mensagem própria: "o passe do DLSS 5 não alcança". Era
verdade para cada peça da cadeia — o Feeder diz `D3D10 is not supported` em uma linha, o
dgVoodoo2 entra como `D3D9.dll` e nunca vê um `D3D10CreateDevice1`, e o addon de NR é x64. Foi o
Just Cause 2 que ensinou isso, do jeito caro: instalação inteira, coerente, e o jogo fechando ao
criar o device.

O que faltava era o mesmo movimento que já resolve o DX9: traduzir antes. O DXVK também traduz
D3D10 para Vulkan, e em Vulkan a cadeia inteira já funciona — camada Vulkan do ReShade, metades
de 32 bits com transporte Vulkan, host64. Um jogo D3D10 traduzido é, para o Feeder, um jogo
Vulkan.

### Por que a 1.10.3, e não a mais nova

A escolha foi medida no Just Cause 2, três vezes, com a mesma cadeia:

| DXVK | o que tem para D3D10 | resultado |
|---|---|---|
| 3.1 (atual) | `d3d10core.dll` + `d3d11.dll` + `dxgi.dll` | sem ReShade roda; com ReShade morre 3 s depois de abrir, com ou sem o Feeder |
| wrappers da 1.10.3 sobre o core da 3.1 | `d3d10.dll` + `d3d10_1.dll` antigos | sai limpo em 2 s |
| **1.10.3 inteiro** | os cinco arquivos | **roda, com o passe neural avaliando** |

O ReShade.log da primeira linha explica o resultado. Desde a 2.0 o DXVK não traz `d3d10.dll` nem
`d3d10_1.dll`, só a camada por baixo — então o jogo carrega os dois **do Windows**, e o ReShade
(que está no processo pela camada Vulkan) instala "delayed hooks" neles e envolve o device D3D10 do
DXVK num wrapper próprio. O processo cai logo depois, sem evento no Event Log. Na rota DX9 isso
nunca acontece: o `d3d9.dll` carregado é o local do DXVK, e o hook no do sistema fica "Delayed"
para sempre. A 1.10.3 é a última release com os dois wrappers próprios; com eles na pasta, o
Windows nunca entra, e só existe o runtime Vulkan.

Com o conjunto inteiro, no Just Cause 2 (32 bits, D3D10.1, RTX 5090, 2560x1440):

```
[feed32] effects: technique found, DLSS5_MV found, DLSS5_Depth found, DLSS5_MV_PROVIDER=3 (LumeniteFX Kernel) -> Lumenite_Kernel (enabled)
[feed32] host spawned (pid 8444)
[feed32] host connected (protocol v2)
[feed32] vk: conjunto pronto 2560x1440 color=28 output=28 (host ngx 0x00000001, DLSS)
[feed32] 600 frames: feed CPU 4.33 ms/frame | frame interval 6.08 ms (164.6 fps)
```

e no host64:

```
[DLSS 5 Neural Rendering] DLSS5 Generic: signed DLSSNR 310.8.0 D3D12 runtime initialized
[DLSS 5 Neural Rendering] DLSS5 Generic: inline feature 18 evaluation succeeded (count=60, NR input 2560x1440 (guides 2560x1440), output 2560x1440 [native])
```

### O que muda para quem instala

- A rota D3D10 baixa o DXVK 1.10.3 para uma subpasta própria da biblioteca (`dxvk\d3d10-1.10.3`);
  a rota DX9 continua na release atual. As duas não se misturam.
- Vão cinco arquivos para a pasta do jogo: `d3d10.dll`, `d3d10_1.dll`, `d3d10core.dll`,
  `d3d11.dll`, `dxgi.dll`. O que ocupava esses nomes é guardado como `.pre-dxvk` e volta ao
  remover. O `d3d9.dll` não vai: o Just Cause 2 o importa como fallback que nunca usa.
- O ReShade entra como camada Vulkan, nunca como proxy — o `dxgi.dll` agora é do DXVK.
- Não há escolha de tradutor: o dgVoodoo2 não cobre D3D10, então a tela mostra um aviso em vez
  dos dois botões, e `--dgvoodoo` é ignorado com uma linha dizendo por quê.
- A cadeia ganha o elo "Tradutor D3D10 (DXVK)"; o `--check` mostra `tradutor DX10`, e a API do
  executável sai como `DX10` em vez do `DX12` permissivo que a heurística dava antes.
- Aviso do que se perde: com o renderizador D3D10 traduzido, efeitos presos a ele somem das
  opções — no Just Cause 2, o Bokeh e a água por GPU (CUDA com interop D3D10). A PCGamingWiki
  documenta o mesmo; de quebra, é o Bokeh que derruba o FPS desse jogo em RTX 50.

### Conhecido, e não deste release

No processo de 32 bits o ReShade ainda tenta carregar o `renodx-dlss5.addon64` que a chave
`LoadFromDllMain` da pasta do jogo aponta, e falha com erro 193 (DLL de 64 bits num processo de
32). É inofensivo — o addon certo está no host64 — e é assim em toda rota de 32 bits, não só
nesta.

## v1.69.0

O DLSS 5 não ligava em RTX 40 — e a causa estava na última linha da verificação, depois de tudo
dar certo.

### O build que serve à sua placa era o único que a checagem recusava

O modelo original da NVIDIA traz kernels `sm_120` e roda só em Blackwell. Para RTX 20/30/40 existem
os rebuilds `.SF` do ShortFuse, publicados no mesmo manifesto do RHI que o launcher já consulta — e
desde a v1.59 o índice escolhe corretamente um deles quando a placa não é série 50.

Só que **patchear um binário invalida a assinatura Authenticode**. O `.SF-v2` volta como
`NotSigned`, e a última linha da instalação exigia assinatura da NVIDIA:

```
NeuralFor(false) → escolhe 310.8.SF-v2   ✓
baixa 111 MB                              ✓
IsGenuine → NotSigned → REJEITADO         ✗
```

O usuário via o download inteiro acontecer e, no fim, nenhum runtime — e a cadeia travada sem
explicação acionável.

A assinatura continua sendo a porta principal. O que mudou é que ela deixou de ser a **única**:
um rebuild da comunidade é aceito quando a origem é o repositório do RHI que o índice já fixa **e**
o SHA-256 bate com um valor conferido à mão e escrito no código. Um `.SF` fora dessa lista é
recusado com o nome da versão na mensagem — pedir uma atualização do launcher é melhor do que
aceitar 158 MB não assinados de procedência desconhecida.

Verificado nos três casos: o build correto passa, uma versão desconhecida é recusada, e uma origem
fora do RHI é recusada.

### E acertava a placa errada

A escolha por peso colocava o `.SF` em primeiro **também em Blackwell** — o oposto do que o método
diz fazer. Numa série 50 o build da NVIDIA é a referência: é ele que a placa foi feita para rodar,
e é o único assinado, o que evita depender de um hash fixado.

Isso significa que a busca de runtime estava quebrada para **todas** as placas numa instalação
limpa, e não só nas RTX 40 — quem já tinha o arquivo de antes não percebia.

| placa | build | verificação |
|---|---|---|
| RTX 50 | `310.8.0` | assinatura NVIDIA — `Valid` |
| RTX 20/30/40 | `310.8.SF-v2` | origem RHI + SHA-256 conferido |

### O CLI nem tentava

`dlss5 <jogo>` só buscava o runtime se a placa fosse Blackwell — resquício de quando só a série 50
era atendida. Numa RTX 40 ele reportava "sem runtime" para uma placa que roda o `.SF` perfeitamente.
Quem decide se a placa serve é a checagem de tensor core, não a arquitetura.

## v1.68.0

### Capa para jogo que não veio de loja nenhuma

Um repack numa pasta não tem appid, e a busca de capa desistia exatamente aí — o card ficava com as
iniciais num retângulo cinza. São justamente os jogos em que a capa mais ajuda: "Metal Gear Solid V
The Phantom Pain" numa pasta de repack é uma linha de texto longa, enquanto a capa se reconhece de
relance.

Agora o nome é resolvido num appid antes de buscar a arte, como o Playnite faz com os provedores de
metadados dele. O catálogo da Steam serve de índice mesmo para quem não comprou lá: quase todo jogo
de PC tem uma página, e a arte está num CDN público.

O nome da pasta passa por uma limpeza primeiro — `(2026)`, `[Portable by SeleZen]`, `-CODEX`,
`v1.0.3`, "Repack" — e a comparação é feita sobre o nome normalizado, sem pontuação, para que
"Marvels Spider-Man 2" case com "Marvel's Spider-Man 2". Exige que um nome contenha o outro:
aceita diferença de subtítulo e edição, e recusa um jogo vizinho da mesma franquia. Uma capa errada
é pior do que nenhuma, porque parece certa.

Quando os endereços antigos do CDN não respondem, a API da loja entra como segunda tentativa. A
Steam passou a guardar a arte sob um hash por asset, e só a API sabe qual é — sem ela, um título
ainda não lançado tem página, tem arte, e mesmo assim ficava sem capa.

Medido nos nomes reais das pastas:

| pasta | resultado |
|---|---|
| `Metal Gear Solid V The Phantom Pain` | appid 287700, capa 300×450 |
| `Marvels Spider-Man 2 (2023-2025)` | appid 2651280, capa 300×450 |
| `Mortal Shell II (2026)` | appid 2584270, header 460×215 (via API) |
| `LEGO Batman … [Portable by SeleZen]` | appid 2215200, header 460×215 (via API) |
| `WinBox`, `Social Club UI` | sem capa — corretamente |

A resposta é guardada em disco pelos dois lados. O acerto poupa a rede; o erro poupa mais, porque
um nome que não existe na Steam seria consultado a cada abertura, para sempre.

### Duas coisas que não eram jogos

**Rockstar Games Social Club** aparecia na grade com bolinha de DLSS 5 para instalar. A lista de
exclusão do scanner dizia `"Social Club"` e comparava por igualdade — a chave do registro chama-se
`Rockstar Games Social Club`, então nunca casava. Agora a comparação é por conteúdo, e `Steam`
entrou na lista pelo mesmo motivo (a Rockstar cria essa subchave para apontar a integração).

**WinBox** era pior: `C:\Users\Admin\Downloads` tinha sido adicionada como pasta de jogo. Sem um
nome de pasta utilizável, o resolvedor cai no `ProductName` de algum executável lá dentro — e
escolheu o de uma ferramenta de rede que por acaso estava baixada ali.

Uma pasta que guarda muitas coisas sem relação entre si não é um jogo. Downloads, Desktop,
Documentos, Program Files e a raiz de uma unidade passam a ser recusadas — na hora de adicionar,
com uma explicação, e não silenciosamente na varredura.

O filtro vive num lugar só. Quando ele estava só na interface, o WinBox sumiu da grade e continuou
aparecendo no `list` — a mesma divergência entre duas cópias que já tinha travado o interruptor no
Baldur's Gate 3.

## v1.67.0

A chave de tradutor da versão anterior estava errada de duas formas ao mesmo tempo, e uma delas
fazia a tela mentir sobre qual tradutor o jogo usa.

### A chave dizia uma coisa com a posição e outra com a cor

Com o DXVK ativo, o botão ia para a **direita** — e o rótulo do DXVK fica à **esquerda**. Quem
olhasse a posição lia "dgVoodoo2"; quem olhasse a cor lia "DXVK".

E o formato em si mentia: trilho verde de um lado, cinza do outro, é a semântica de ligado e
desligado. O dgVoodoo2 não é o DXVK desligado — é a outra opção, do mesmo nível.

Agora são dois botões lado a lado, com o escolhido preenchido. Não há posição para contradizer, e
nenhum dos dois parece a ausência do outro.

### E mostrava a preferência, não o tradutor em uso

Este era o problema de verdade. O controle lia a preferência salva e, quando não havia nenhuma,
recalculava um padrão — em vez de olhar o que está na pasta.

No Saints Row 2 isso significava a tela dizer **DXVK** enquanto o `d3d9.dll` no disco tinha 485 KB,
que é o do dgVoodoo2. Um controle que diz qual tradutor está em uso tem de ler o tradutor em uso; a
preferência só vale enquanto nada foi instalado ainda.

É a mesma regra que a cadeia já segue desde o Baldur's Gate 3: **instalado ganha de deduzido**.

### Trocar de jogo deixava o controle no estado do jogo anterior

Faltavam as notificações dos dois booleanos que pintam os botões. O campo mudava por baixo e a
tela não ficava sabendo.

## v1.66.0

O Hitman: Blood Money não mostrava o seletor de tradutor de DirectX 9 que os outros jogos D3D9
mostram — e o motivo estava no executável dele.

### Executáveis empacotados liam como "sem API gráfica"

`HitmanBloodMoney.exe` importa exatamente **uma** DLL: `kernel32.dll`. Isso não é um jogo sem API
gráfica — é a assinatura de um protetor de 2006 (SecuROM, SafeDisc) que remonta a tabela de imports
em tempo de execução. Nenhuma varredura estática acha D3D9 ali, nem no import nem nas strings,
porque o binário está comprimido.

O efeito era encadeado:

1. sem sinal de D3D9, `DgVoodooService.Applies` respondia não
2. o cartão do tradutor não aparecia, porque a condição dele é exatamente essa
3. e, numa instalação nova, `ReachesD3D12` responderia **sim** — o padrão permissivo do silêncio —
   tratando um jogo de 2006 como se alcançasse D3D12

Quando o próprio binário não pode falar, a pasta fala. Duas evidências, as duas fortes: `d3dx9_27.dll`
distribuído junto (a D3DX9 é a biblioteca auxiliar do D3D9 e de mais nada), e o `configure.exe` ao
lado, sem empacotamento, importando `d3d9.dll` de forma limpa — um utilitário que abre um device
D3D9 para enumerar adaptadores só existe num jogo D3D9.

A regra é estreita de propósito: só vale quando o executável está **empacotado** (tabela de imports
degenerada, uma ou duas DLLs). Fora disso, a leitura normal decide. Das 42 pastas testadas, os
únicos jogos detectados como D3D9 continuam sendo os genuinamente antigos de 32 bits — nenhum
título moderno foi arrastado junto.

### A cadeia não checava o tradutor

A tela podia dizer "instalado" sobre uma pasta sem tradutor nenhum, e nesse estado não há o que
enganchar: o ReShade em D3D9 puro para no Shader Model 3 e nenhum provedor de motion vectors
compila; e a API não tem handle compartilhado nem fence, que é por onde as texturas chegam ao
device D3D12 do passe.

Agora há um elo **Tradutor D3D9**, mostrado só nos jogos que precisam de um.

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



