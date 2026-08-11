# Changelog

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
