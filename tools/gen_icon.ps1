# Gera o icone do app (src/Assets/app.ico) + o PNG de preview do README.
#
# Equivalente ao tools/gen_icon.py, para maquina sem Python (o alias da Microsoft Store so abre
# a loja). Usa System.Drawing, que ja vem com o .NET no Windows.
#
# A marca: quadrado arredondado quase preto, um halo neon subindo do centro-baixo, e o "5" em
# gradiente verde-lima -> ciano ocupando quase toda a altura. O verde e o do ecossistema NVIDIA;
# o ciano puxa para o lado "neural". Atras do numero, uma grade de pixels que se dissolve da
# esquerda para a direita -- a reconstrucao acontecendo, que e literalmente o que o programa faz.
#
# O "5" e o assunto: e o que separa este launcher de qualquer outro instalador de DLSS, e o que
# a pessoa procura na barra de tarefas a 16 px, onde tudo mais some.
#
#     powershell -File tools/gen_icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$raiz = Split-Path $PSScriptRoot -Parent
$SS   = 8      # supersample: desenha grande e reduz, para a curva ficar limpa a 16 px
$TAMANHOS = @(256, 128, 64, 48, 32, 24, 16)

function Construir([int]$tamanho) {
  $S = $tamanho * $SS
  $bmp = New-Object Drawing.Bitmap($S, $S, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode     = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([Drawing.Color]::Transparent)

  # ---- recorte do quadrado arredondado ----
  $raio = [int]($S * 0.22)
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $d = $raio * 2
  $path.AddArc(0, 0, $d, $d, 180, 90)
  $path.AddArc($S - $d - 1, 0, $d, $d, 270, 90)
  $path.AddArc($S - $d - 1, $S - $d - 1, $d, $d, 0, 90)
  $path.AddArc(0, $S - $d - 1, $d, $d, 90, 90)
  $path.CloseFigure()
  $g.SetClip($path)

  # ---- corpo: quase preto com um azul frio no topo ----
  $rectAll = New-Object Drawing.Rectangle(0, 0, $S, $S)
  $bgBrush = New-Object Drawing.Drawing2D.LinearGradientBrush(
      $rectAll,
      [Drawing.Color]::FromArgb(255, 18, 22, 32),
      [Drawing.Color]::FromArgb(255,  6,  7, 10),
      [Drawing.Drawing2D.LinearGradientMode]::Vertical)
  $g.FillRectangle($bgBrush, $rectAll)
  $bgBrush.Dispose()

  # ---- grade de pixels que se dissolve: a reconstrucao ----
  # blocos grandes e visiveis a esquerda, sumindo para a direita
  $cel = [int]($S * 0.075)
  for ($y = 0; $y -lt $S; $y += $cel) {
    for ($x = 0; $x -lt $S; $x += $cel) {
      $t = $x / $S                      # 0 na esquerda, 1 na direita
      $a = [int](26 * (1 - $t) * (1 - $t))
      if ($a -le 1) { continue }
      $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb($a, 120, 200, 255), [Math]::Max(1, $S * 0.002))
      $g.DrawRectangle($pen, $x, $y, $cel, $cel)
      $pen.Dispose()
    }
  }

  # ---- halo neon atras do numero ----
  # Concentrado e discreto: a versao anterior era um circulo enorme e opaco que lavava o fundo
  # inteiro de verde e comia o contraste do "5", que e o que precisa ser lido a 16 px.
  $hx = [int]($S * 0.50); $hy = [int]($S * 0.52); $hr = [int]($S * 0.40)
  $halo = New-Object Drawing.Drawing2D.GraphicsPath
  $halo.AddEllipse($hx - $hr, $hy - $hr, $hr * 2, $hr * 2)
  $pg = New-Object Drawing.Drawing2D.PathGradientBrush($halo)
  $pg.CenterColor = [Drawing.Color]::FromArgb(70, 60, 220, 130)
  $pg.SurroundColors = @([Drawing.Color]::FromArgb(0, 0, 0, 0))
  $g.FillEllipse($pg, $hx - $hr, $hy - $hr, $hr * 2, $hr * 2)
  $pg.Dispose(); $halo.Dispose()

  # ---- o "5" ----
  # desenhado como path, para receber gradiente e brilho por fora
  $fonte  = New-Object Drawing.Font("Segoe UI", ($S * 0.62), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object Drawing.StringFormat
  $fmt.Alignment     = [Drawing.StringAlignment]::Center
  $fmt.LineAlignment = [Drawing.StringAlignment]::Center

  $texto = New-Object Drawing.Drawing2D.GraphicsPath
  $texto.AddString("5", $fonte.FontFamily, [int][Drawing.FontStyle]::Bold, ($S * 0.62),
                   (New-Object Drawing.RectangleF(0, ($S * -0.045), $S, $S)), $fmt)

  # brilho: o mesmo contorno desenhado varias vezes, cada vez mais grosso e mais transparente
  for ($w = ($S * 0.075); $w -gt 0; $w -= ($S * 0.009)) {
    $a = [int](26 * (1 - $w / ($S * 0.075)))
    if ($a -le 0) { continue }
    $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb($a, 120, 255, 170), $w)
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $texto)
    $pen.Dispose()
  }

  # preenchimento em gradiente: verde-lima em cima -> ciano embaixo
  $rectTxt = New-Object Drawing.Rectangle(0, [int]($S * 0.12), $S, [int]($S * 0.78))
  $grad = New-Object Drawing.Drawing2D.LinearGradientBrush(
      $rectTxt,
      [Drawing.Color]::FromArgb(255, 190, 255, 120),
      [Drawing.Color]::FromArgb(255,  60, 210, 255),
      [Drawing.Drawing2D.LinearGradientMode]::Vertical)
  $g.FillPath($grad, $texto)
  $grad.Dispose()

  # fio de luz fino no contorno, para o numero destacar do fundo sem virar adesivo
  $penTop = New-Object Drawing.Pen([Drawing.Color]::FromArgb(120, 230, 255, 240), ($S * 0.0035))
  $penTop.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
  $g.DrawPath($penTop, $texto)
  $penTop.Dispose()

  $texto.Dispose(); $fonte.Dispose(); $fmt.Dispose()

  # ---- brilho interno na borda do quadrado ----
  $penBorda = New-Object Drawing.Pen([Drawing.Color]::FromArgb(40, 255, 255, 255), ($S * 0.008))
  $g.DrawPath($penBorda, $path)
  $penBorda.Dispose()
  $path.Dispose()
  $g.Dispose()

  # ---- reduz para o tamanho final ----
  $fin = New-Object Drawing.Bitmap($tamanho, $tamanho, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g2 = [Drawing.Graphics]::FromImage($fin)
  $g2.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g2.DrawImage($bmp, 0, 0, $tamanho, $tamanho)
  $g2.Dispose(); $bmp.Dispose()
  return $fin
}

# ---- PNG de preview (512, para o README) ----
$png = Construir 512
$destPng = Join-Path $raiz 'src\Assets\app.png'
$png.Save($destPng, [Drawing.Imaging.ImageFormat]::Png)
Write-Host ("  app.png   {0:N0} bytes" -f (Get-Item $destPng).Length)
New-Item -ItemType Directory -Path (Join-Path $raiz 'docs') -Force | Out-Null
Copy-Item $destPng (Join-Path $raiz 'docs\icon.png') -Force

# ---- ICO com todos os tamanhos ----
# Escrito a mao: System.Drawing.Icon nao grava multi-resolucao, e um .ico de um tamanho so fica
# borrado na barra de tarefas e na lista de programas.
$imgs = @{}
foreach ($t in $TAMANHOS) { $imgs[$t] = Construir $t }

$destIco = Join-Path $raiz 'src\Assets\app.ico'
$fs = [IO.File]::Create($destIco)
$bw = New-Object IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$TAMANHOS.Count)   # ICONDIR

$blobs = @()
foreach ($t in $TAMANHOS) {
  $ms = New-Object IO.MemoryStream
  $imgs[$t].Save($ms, [Drawing.Imaging.ImageFormat]::Png)   # PNG dentro do ICO: aceito desde o Vista
  $blobs += ,$ms.ToArray()
  $ms.Dispose()
}
$offset = 6 + (16 * $TAMANHOS.Count)
for ($i = 0; $i -lt $TAMANHOS.Count; $i++) {
  $t = $TAMANHOS[$i]
  $bw.Write([Byte]($(if ($t -ge 256) { 0 } else { $t })))    # 0 significa 256
  $bw.Write([Byte]($(if ($t -ge 256) { 0 } else { $t })))
  $bw.Write([Byte]0); $bw.Write([Byte]0)                     # cores na paleta, reservado
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)                # planos, bits por pixel
  $bw.Write([UInt32]$blobs[$i].Length)
  $bw.Write([UInt32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
foreach ($t in $TAMANHOS) { $imgs[$t].Dispose() }
$png.Dispose()
Write-Host ("  app.ico   {0:N0} bytes  ({1} tamanhos)" -f (Get-Item $destIco).Length, $TAMANHOS.Count)
