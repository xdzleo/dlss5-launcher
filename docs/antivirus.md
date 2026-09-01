# Antivírus acusando o DLSS 5 Launcher

Esta página tem dois públicos: quem baixou e levou um susto, e quem mantém o projeto e
precisa resolver.

---

## Para quem baixou

**É falso-positivo.** Todo o código-fonte está neste repositório, o binário é compilado
pelo GitHub Actions (nunca na máquina de ninguém) e o hash de cada release sai publicado
no `SHA256SUMS.txt` junto dos arquivos.

Por que um antivírus desconfia de um programa honesto: o launcher faz, de propósito, três
coisas que também aparecem no comportamento de malware.

| O que ele faz | Por que parece ruim | Por que é legítimo |
|---|---|---|
| Baixa o ReShade do `reshade.me` e extrai a DLL de dentro do instalador | baixar binário da internet e usar sem rodar o instalador oficial | é o único jeito de instalar o ReShade sem o setup interativo — e o launcher **verifica a assinatura Authenticode do instalador contra o certificado do autor do ReShade antes de extrair qualquer coisa** (ver abaixo); a DLL é copiada byte a byte, sem modificação |
| Grava `dxgi.dll` / `d3d9.dll` / `opengl32.dll` dentro da pasta do jogo | gravar arquivo com nome de DLL do sistema no diretório de outro programa é a definição de *DLL side-loading* | é exatamente como todo mod gráfico de PC funciona há 15 anos — ReShade, ENB, SpecialK, dxvk |
| Lê a *import table* do `.exe` do jogo | analisar o executável de outro programa | é leitura pura de arquivo, para descobrir se o jogo usa D3D11, D3D12, Vulkan ou OpenGL e escolher a DLL certa |

E, do outro lado, o que ele **não** faz — e isso é verificável no código, não é promessa:

- não injeta código em processo nenhum (zero `WriteProcessMemory`, `CreateRemoteThread`,
  `SetWindowsHookEx`, `VirtualAllocEx`, `OpenProcess`);
- não instala serviço, driver, tarefa agendada, nem chave `Run` de inicialização;
- não se copia para lugar nenhum, não se auto-atualiza baixando executável;
- não escreve nada em `%TEMP%` — é regra do projeto; o que precisa de arquivo temporário
  usa `%LocalAppData%\RenoDXLauncher\cache`;
- não se disfarça de navegador: o `User-Agent` das requisições diz o que ele é e aponta
  para este repositório.

Os P/Invoke do projeto inteiro são cinco: `AttachConsole`, `AllocConsole` e `FreeConsole`
(para o modo linha de comando funcionar numa janela) e `WinVerifyTrust` — que é a API do
próprio Windows para **conferir assinatura digital**, usada exatamente para o item abaixo.

O CI verifica essa lista a cada push (`tools/av-selfcheck.ps1`, teste 11): se alguém
introduzir uma dessas chamadas, o build reprova.

### O ReShade é verificado por assinatura antes de ser usado

Este é o ponto que separa "baixa um executável da internet e usa o binário de dentro" —
que é a descrição literal de um *dropper* — de "instala um artefato de origem comprovada".

Antes de extrair qualquer coisa, o launcher confere que o `ReShade_Setup_X.Y.Z_Addon.exe`
baixado está **assinado pelo certificado do autor do ReShade** (`CN=ReShade`,
`E=info@reshade.me`) e que o conteúdo está **íntegro**. Se qualquer uma das duas coisas
falhar — assinatura ausente, arquivo modificado no caminho, ou outro signatário — o
download é descartado e nada é instalado.

Dois detalhes que fazem isso funcionar de verdade:

- **O ZIP que o instalador do ReShade carrega anexado está dentro da região assinada.** Ele
  fica antes da tabela de certificado do PE, e o Authenticode faz digest de tudo menos da
  própria tabela. Conferido na prática: trocar um único byte dentro do ZIP muda o status de
  "raiz não confiável" para `HashMismatch`. Logo, validar a assinatura do `setup.exe` prova
  também a integridade das DLLs extraídas de dentro dele.
- **O que é fixado é o certificado, não o hash do arquivo.** O hash muda a cada versão do
  ReShade e viraria manutenção eterna (e uma versão nova sem hash cadastrado quebraria a
  instalação); a identidade de quem assina não muda — o certificado vale até 2039. Na
  prática: o pino foi calibrado com o ReShade 6.7.3 e validou o 6.8.0 sem nenhuma alteração.

O certificado do ReShade é auto-assinado, então a cadeia não sobe até uma raiz confiável do
Windows — isso é esperado e **não** é sinal de adulteração. O código distingue as duas
coisas: `CERT_E_UNTRUSTEDROOT` (integridade OK, cadeia não confiável → aceito, com o
certificado conferido) versus `TRUST_E_BAD_DIGEST` (conteúdo alterado → recusado).

### O que fazer

1. **Confira o hash.** Baixe o `SHA256SUMS.txt` do release e compare:
   ```powershell
   Get-FileHash .\RenoDXLauncher-1.12.0-setup.exe -Algorithm SHA256
   ```
   Se bater, o arquivo é exatamente o que o GitHub Actions produziu.

2. **Libere no seu antivírus** (adicione a pasta de instalação à lista de exclusões), ou

3. **Compile você mesmo** — é uma linha:
   ```powershell
   dotnet publish src\RenoDXLauncher.csproj -c Release -r win-x64 --self-contained true -o app
   ```

4. **Reporte o falso-positivo ao fabricante do seu antivírus.** É o que efetivamente
   resolve, e leva poucos minutos — a tabela de canais está mais abaixo. Quanto mais gente
   reporta, mais rápido cai.

### Sobre o aviso do SmartScreen

O "O Windows protegeu o seu computador" é coisa diferente de antivírus: é reputação de
download. Um arquivo novo, baixado por pouca gente, sempre recebe o aviso — assinado ou
não. Ele some sozinho conforme o volume de downloads limpos cresce. Segundo a própria
Microsoft, **não existe** formulário para acelerar isso em máquina de consumidor.

---

## Para quem mantém

### Ordem de impacto (do maior para o menor)

1. **Assinatura de código.** É a variável dominante, e nada nesta lista substitui.
   Está em andamento pela [SignPath Foundation](https://signpath.org/) — ver
   [SIGNING-SETUP.md](SIGNING-SETUP.md). Uma assinatura válida é isenção explícita em
   várias regras heurísticas, e a reputação **acumula na identidade do certificado**, ou
   seja: transfere entre releases, ao contrário da reputação de hash.
   Não vale a pena pagar por EV — desde 2024 OV e EV constroem reputação de forma
   idêntica, e o SmartScreen não é mais pulado instantaneamente por EV.
2. **Instalador em vez de zip solto.** Ver a seção seguinte.
3. **Forma do binário.** Sem `PublishSingleFile`, sem `PublishTrimmed`, sem
   `PublishReadyToRun`, sem AOT — os quatro estão travados explicitamente no
   `src/RenoDXLauncher.csproj`, com o motivo de cada um comentado ao lado.
4. **Metadata e manifesto.** `VersionInfo` completo no `.exe` e no `setup.exe`;
   `app.manifest` com `asInvoker` e `supportedOS` declarados.
5. **Submissão de falso-positivo aos fabricantes.** É trabalho recorrente, não uma vez só.

### O que o instalador resolve — e o que não resolve

**Resolve:** o pior caminho possível para um binário desconhecido é *zip → extrair no
Downloads → dar duplo-clique num `.exe` solto*. Além de ser o padrão que a heurística mais
pontua, várias versões do 7-Zip **não propagam o Zone.Identifier** para os arquivos
extraídos: o `.exe` resultante parece um arquivo local, o SmartScreen nem chega a ser
consultado, e **nenhuma** reputação é acumulada para aquele hash. Um `setup.exe` único,
baixado da mesma URL por todo mundo, instalando em `Program Files`, acumula reputação no
ritmo máximo possível e roda de um diretório que as políticas de allowlist tratam como
confiável.

**Não resolve:** a detecção em si, no dia zero. O modelo por trás dos falso-positivos do
360 Total Security e do Defender é **prevalência**, não análise do instalador: binário
novo, sem assinatura e sem histórico é presumido suspeito por construção. Trocar o formato
do pacote sem assinar move pouco. Não prometa "zero detecções" — trate detecção como algo
a reportar, não como bug de build.

### Rodando a bateria de verificação

```powershell
pwsh tools\build-installer.ps1 -Zip
```

Publica, compila o instalador e roda `tools\av-selfcheck.ps1`, que faz 11 testes:
estrutura do publish (pasta, nunca bundle auto-extraível), executáveis inesperados no
payload, `VersionInfo` completo, Authenticode, entropia e nomes de seção do PE (heurística
de packer), manifesto embutido, varredura do Windows Defender, **instalação e
desinstalação silenciosa de verdade** com smoke test do binário instalado, VirusTotal,
hashes, e os invariantes de código-fonte.

Um teste que não pode ser feito sai como `SKIP`, nunca como `PASS`. O Defender fica `SKIP`
em máquina onde ele está desligado ou substituído; o VirusTotal fica `SKIP` sem chave de
API:

```powershell
pwsh tools\av-selfcheck.ps1 -VirusTotalApiKey <chave>
```

Chave grátis em <https://www.virustotal.com/gui/join-us>. No CI, o secret `VT_API_KEY`
liga o teste automaticamente nos dois workflows.

### O que foi testado, e por que so a assinatura resolve

Na v1.12.0 o instalador publicado marcou 2/65 no VirusTotal: `Trojan:Win32/Wacatac.B!ml`
(Microsoft) e `MALICIOUS` (DeepInstinct). Os dois sao veredito de **machine learning** — o
sufixo `!ml` e explicito, e o DeepInstinct e um motor puramente de ML. Nenhum dos dois e
assinatura de familia de malware real.

Antes de aceitar isso, quatro variantes do MESMO codigo foram compiladas e submetidas:

| Variante | Compressao | Deteccoes |
|---|---|---|
| Build local | `lzma2/max` + solid | **0/67** |
| Build do CI (o publicado) | `lzma2/max` + solid | 2/65 |
| Sem solid | `lzma2/max` | 1/69 |
| Compressao normal | `lzma2/normal` + solid | 2/68 |
| Compressao zip | `zip` | 1/67 |

As duas primeiras linhas sao o resultado que importa: **mesma configuracao, mesmo codigo,
0 numa maquina e 2 na outra**. E nenhuma configuracao de compressao zerou — passar para
`zip`, que engorda o arquivo em 16 MB, ainda deixa 1.

A conclusao e que nao ha nada no conteudo sendo detectado. O binario fica na margem de um
classificador, e cai de um lado ou do outro conforme ruido de build. Mexer em compressao
para tentar escapar disso e perda de tempo, e o custo (download maior) e real.

O que muda o resultado, em ordem de efeito: **assinar** (a reputacao acumula na identidade
do certificado e transfere entre versoes) e **submeter o falso-positivo**, que para
deteccao `!ml` funciona — o modelo e reajustado. Prevalencia resolve sozinha com o tempo,
mas so com volume de download.

O `.exe` do app e o zip portatil marcaram **0** em todas as varreduras. Quem passa da
instalacao nao encontra nada.

### Como o CI decide reprovar (e por que o limiar numerico foi trocado)

O gate era `malicious >= 3` reprova. Isso pareceu razoavel enquanto o numero ficava em 2 —
mas 2 era justamente a margem descrita acima, e a margem se move sozinha. Trocar o nome do
produto e o icone na v1.60.0 zerou a pouca reputacao que aquele hash tinha e empurrou a
contagem para o outro lado: o release parou de sair, sem que uma linha de comportamento
tivesse mudado.

Subir o limiar teria resolvido o sintoma cegando o teste. O criterio agora separa as duas
coisas que a contagem crua misturava:

| Veredito | Exemplo | Resultado |
|---|---|---|
| machine learning / generico | `Trojan:Win32/Wacatac.B!ml`, `MALICIOUS`, `Gen:Variant.Tedy`, `Heur.AdvML.B` | **WARN** — nao reprova |
| familia nomeada | `Trojan.Emotet`, `Win32.AgentTesla` | **FAIL** — reprova, mesmo se for um so motor |
| ruido de ML em volume | qualquer, com `malicious >= 6` | **FAIL** — deixou de ser margem |

O raciocinio e o mesmo da secao anterior: um veredito de ML diz que o binario caiu do lado
errado de um classificador; uma assinatura nomeada diz que um motor reconheceu algo
especifico. A primeira e ruido de build — medido, com 0 numa maquina e 2 em outra para o
mesmo codigo. A segunda merece parar o release na hora, e agora para, o que o limiar de 3
nao fazia.

### Texto pronto para a submissão

Cole isto no campo de informações adicionais. Ele é escrito para um analista: cada
afirmação é verificável no repositório, e é por isso que as verificações do CI existem.
**Troque o hash, a versão e o link do build a cada release.**

> DLSS 5 Launcher is an open-source (MIT) one-click installer for DLSS 5 Neural Rendering, and a manager for RenoDX HDR game mods.
> Source: https://github.com/xdzleo/dlss5-launcher
>
> This exact file was produced by GitHub Actions, not on a developer machine:
> https://github.com/xdzleo/dlss5-launcher/actions/runs/32233599158 (commit `20c0247`).
> SHA-256: `e5ea09223e605c53e9f37ac5b0041e2c49c69622244927249bfb17b2716884b8`
>
> The application does not inject code into other processes. The source tree contains no
> `WriteProcessMemory`, `CreateRemoteThread`, `VirtualAllocEx`, `SetWindowsHookEx`,
> `OpenProcess` or `QueueUserAPC`; this is asserted by an automated check on every push
> (`tools/av-selfcheck.ps1`, test 11). It installs no service, driver, scheduled task or
> Run key, and never copies itself. The only P/Invokes in the project are `AttachConsole`,
> `AllocConsole`, `FreeConsole` (command-line mode) and `WinVerifyTrust`.
>
> What it does that may look unusual, and why:
>
> - It downloads ReShade (add-on build) from reshade.me and extracts `ReShade64.dll` from
>   the archive appended to the official installer. Before extracting anything it verifies
>   the download's Authenticode signature against the ReShade author's certificate
>   (`CN=ReShade`, cert SHA-256 `445802BC…95790`) and discards the download on mismatch.
> - It writes that DLL into a game folder under a graphics-API proxy name (`dxgi.dll`,
>   `d3d9.dll`, `opengl32.dll`). This is how PC graphics mods have worked for fifteen
>   years — ReShade, ENB, Special K, dxvk.
> - It reads the PE import table of game executables with ordinary file I/O, to choose the
>   correct proxy name.
>
> The installer is stock Inno Setup 6.7.3. Code signing through SignPath Foundation is in
> progress.

### Canais de submissão de falso-positivo

Submeta **depois** de a assinatura estar no lugar: fabricante coloca **certificado** em
allowlist com muito mais facilidade do que hash avulso. Mande sempre os dois arquivos, o
`setup.exe` e o `RenoDXLauncher.exe`.

| Fabricante | Canal | Notas |
|---|---|---|
| **Microsoft Defender** | <https://www.microsoft.com/en-us/wdsi/filesubmission> | persona **"Software developer"**. O dropdown de produto lista SmartScreen separado do Defender Antivirus — faça as duas. Guarde o ID da submissão e o SHA-256. |
| **Qihoo 360** *(o mais reportado pelos usuários)* | <https://www.360totalsecurity.com/en/suspicion/false-positive/> | até 20 MB em RAR/ZIP/7z, ou link de download acima disso; screenshot < 2 MB; e-mail obrigatório; CAPTCHA. Reporte também a URL do release em <https://www.360totalsecurity.com/en/suspicion/false-positive-url/>. Fallback: `support@360safe.com` |
| **Avast + AVG + Avira + Norton** *(todos Gen Digital — um formulário só)* | <https://www.avast.com/en-us/whitelist-program-registration> | **a submissão de maior retorno depois da assinatura**: aceita allowlist **por assinatura digital**, o que resolve os quatro de uma vez e para todas as releases futuras. Grátis, até 60 MB. FP pontual: <https://www.avast.com/false-positive-file-form.php> |
| **Kaspersky** | <https://opentip.kaspersky.com/> → "Submit to reanalyze" | programa proativo de allowlist: <https://www.kaspersky.com/partners/allowlist-program> |
| **Bitdefender** | <https://www.bitdefender.com/submit/> | ou `virus_submission@bitdefender.com` |
| **McAfee** | <https://www.mcafee.com/en-us/consumer-support/dispute-detection-allowlisting.html> | ou `virus_research@mcafee.com` |
| **Google Safe Browsing** | <https://www.google.com/safebrowsing/report_error/> | só entra em cena se o Chrome bloquear o download; `github.com` já é domínio confiável |
| **Demais motores** | <https://github.com/yaronelh/False-Positive-Center> | diretório de endpoints. Priorize os grandes: os menores costumam seguir. |

**Anexe sempre:** a URL do repositório, a licença MIT, o link da execução do workflow que
gerou aquele hash exato, o SHA-256, e uma explicação curta de por que o app grava
`dxgi.dll` — incluindo a frase, verificável, de que ele **não injeta em processos**.

Repita a cada release nos primeiros meses. Depois o certificado ganha histórico e o volume
cai sozinho.

### Uma recomendação que foi avaliada e recusada

A revisão que produziu esta página sugeriu restringir `KnownProxyNames` — tirar
`version.dll`, `winmm.dll`, `dinput8.dll` e `ddraw.dll` da lista, ou escondê-los atrás de
um toggle "avançado", com o argumento de que são nomes reais de DLL do System32 e não
correlacionam com "mod gráfico" como `dxgi`/`d3d*`/`opengl32` correlacionam.

O argumento está certo no geral e errado neste código. `KnownProxyNames` é usado num lugar
só, `ReShadeService.Detect()`, que **lê** uma pasta de jogo procurando um ReShade já
instalado. Quem escolhe o nome na hora de **escrever** é `PickDllName()`, e ela só devolve
`dxgi.dll`, `opengl32.dll` ou o que a API do jogo exigir — nunca `winmm.dll`.

Ou seja: encurtar a lista não mudaria nada no que o app grava, e faria ele deixar de
reconhecer instalações de ReShade que a pessoa já tinha feito à mão como `winmm.dll` — uma
regressão funcional em troca de zero ganho. A lista fica como está.
