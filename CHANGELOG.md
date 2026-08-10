# Changelog

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
