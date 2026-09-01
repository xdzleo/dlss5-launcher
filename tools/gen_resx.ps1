# Gera os .resx a partir de src/Localization/strings.json.
#
# Equivalente ao tools/gen_resx.py, para maquina sem Python instalado (o alias da Microsoft
# Store nao conta: ele so abre a loja). Mesma saida byte a byte -- mesmas chaves ordenadas,
# mesmo cabecalho, mesmo xml:space="preserve".
#
#     powershell -File tools/gen_resx.ps1

$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$json = Join-Path $raiz 'src\Localization\strings.json'
$NEUTRO = 'pt-BR'

$header = @'
<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    GERADO por tools/gen_resx.py a partir de src/Localization/strings.json.
    Nao edite este arquivo a mao: edite o JSON e rode o gerador de novo.
  -->
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>

'@

function Esc($s) {
    if ($null -eq $s) { return '' }
    $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;')
}

$dados = Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json
$chaves = $dados.PSObject.Properties.Name | Sort-Object -CaseSensitive

foreach ($par in @(@{ idioma = $NEUTRO; arq = 'Strings.resx' },
                   @{ idioma = 'en';    arq = 'Strings.en.resx' })) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append($header)
    $n = 0
    foreach ($chave in $chaves) {
        $v = $dados.$chave
        $texto = $v.PSObject.Properties[$par.idioma]
        if ($null -eq $texto -or $null -eq $texto.Value) { continue }
        [void]$sb.Append("  <data name=""$(Esc $chave)"" xml:space=""preserve"">`n")
        [void]$sb.Append("    <value>$(Esc $texto.Value)</value>`n")
        $nota = $v.PSObject.Properties['note']
        if ($null -ne $nota -and $nota.Value) { [void]$sb.Append("    <comment>$(Esc $nota.Value)</comment>`n") }
        [void]$sb.Append("  </data>`n")
        $n++
    }
    [void]$sb.Append("</root>`n")
    $destino = Join-Path $raiz "src\Localization\$($par.arq)"
    [IO.File]::WriteAllText($destino, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
    Write-Host ("  {0,-20} {1} chaves" -f $par.arq, $n)
}
