# renodx-mfg.addon64 — Multi Frame Generation acima do teto de fábrica

Add-on do ReShade que destrava os modos de Multi Frame Generation que o driver reserva à série
RTX 50, e corrige a colocação temporal dos quadros gerados em GPUs Ada.

O binário compilado deste diretório é o que vai embutido no launcher
(`src/Assets/mfg/renodx-mfg.addon64`), extraído por `MfgService.EnsureLibrary`.

## Origem e licença

O código é um fork de **[dashdogy/RTX40MFG-Unlock](https://github.com/dashdogy/RTX40MFG-Unlock)**,
licenciado sob MIT — ver `LICENSE.upstream`. O aviso de copyright e a licença do autor original
seguem preservados, como a licença exige.

NVIDIA Streamline, NGX e os demais componentes de terceiros continuam sujeitos aos termos deles.
O SDK do Streamline **não** é redistribuído aqui: ele é usado apenas em tempo de compilação, para
os cabeçalhos.

## O que foi mudado em relação ao upstream

O upstream entrega um plugin `.asi` do Cyber Engine Tweaks, que só existe no Cyberpunk 2077. O
patch em si nunca foi específico daquele jogo — ele mira o Streamline. As mudanças abaixo trocam o
carregador e mais nada da lógica de patch:

1. **Identidade de add-on do ReShade** (`patcher.cpp`). Exporta `NAME` e `DESCRIPTION`, os dois
   que o ReShade lê. O ReShade é o carregador universal deste launcher: ele já é instalado em todo
   jogo e o `LoadFromDllMain` dele entra antes de o jogo criar a feature de Frame Generation.
   Saída renomeada para `renodx-mfg.addon64` (`CMakeLists.txt`).

2. **Arquivos de controle com nome próprio** (`patcher.cpp`). O upstream lê `config.json` da pasta
   do CET e, como último recurso, um `config.json` na raiz da pasta do jogo — nome comum demais
   para escrever nele às cegas. Agora o add-on procura `renodx-mfg.json` ao lado do próprio módulo
   primeiro, e escreve o status em `renodx-mfg-status.json`.

3. **Ganchos procurados em todo módulo carregado, e repetidos** (`patcher.cpp`). O upstream olha a
   tabela de importação do executável do jogo, e só. No Cyberpunk basta — o exe importa o
   Streamline direto. Em jogo Unreal quem importa é um plugin que carrega depois, e nenhum gancho
   entrava. A varredura é feita **apenas na thread de trabalho**, nunca no `DllMain`: tirar retrato
   dos módulos pede o loader lock, e o `DllMain` já roda com ele — e neste caminho são dois níveis,
   porque quem nos carrega é o ReShade, do `DllMain` dele. Uma tentativa por segundo, no primeiro
   minuto.

4. **Confirmação da placa sem gancho** (`midpoint_fix.cpp`, `VerifyAdapterFromSoleCudaDevice`). A
   correção D157 só se aplica com a placa confirmada como Ada, e a confirmação vinha do gancho de
   `slSetD3DDevice`. Onde esse gancho não entra, a correção ficava desligada — e numa RTX 40 esse é
   o pior desfecho: os modos acima de 2x aparecem e entregam quadros colapsados. Agora, se nada
   confirmou a placa em 2 s, o CUDA é consultado direto: **uma** placa CUDA e a capacidade de
   computo dela decide. Zero ou mais de uma, a resposta continua sendo não.

   Esta função já foi escrita com DXGI e **isso derrubava o jogo**: o `dxgi.dll` da pasta é o
   proxy do ReShade, então `CreateDXGIFactory1` reentrava no ReShade no meio da inicialização
   gráfica, de outra thread, e o processo morria dois segundos depois de abrir. O CUDA responde a
   mesma pergunta sem encostar em nada que o jogo esteja usando.

## Compilar

Precisa de Visual Studio 2022 (Build Tools bastam), CMake 3.24+ e os cabeçalhos do Streamline SDK
2.12.0 (`git clone --branch v2.12.0 https://github.com/NVIDIAGameWorks/Streamline`).

```powershell
.\build.ps1 -StreamlineRoot C:\caminho\para\streamline
```

O script compila e copia o resultado para `src/Assets/mfg/renodx-mfg.addon64`, imprimindo o
SHA-256 e o tamanho. **Os dois têm de ser copiados para `EmbeddedSha256` e `EmbeddedLength` em
`src/Services/MfgService.cs`** — a extração confere ambos e recusa o recurso que não bater.

Não é compilado na integração contínua: ela não tem o SDK do Streamline, e o SDK não é
redistribuível aqui.
