#!/usr/bin/env python3
"""Gera os .resx a partir de src/Localization/strings.json.

O JSON e a fonte de verdade porque e onde da para ver todos os idiomas lado a lado, que
e como se revisa traducao. Os .resx sao artefato de build, mas ficam versionados para o
projeto continuar compilando sem Python instalado.

    python tools/gen_resx.py

Formato do strings.json:

    {
      "Chave_Da_String": {
        "pt-BR": "Texto em portugues",
        "en":    "Text in English",
        "note":  "opcional: contexto para quem traduz"
      }
    }

O idioma neutro (o .resx sem sufixo) e pt-BR: e o idioma em que o app foi escrito, e o
que aparece se alguem rodar num Windows cujo idioma nao tem traducao.
"""
from __future__ import annotations

import json
import pathlib
import sys
from xml.sax.saxutils import escape

NEUTRAL = "pt-BR"

RESX_HEADER = """<?xml version="1.0" encoding="utf-8"?>
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
"""


def gerar(entradas: dict, idioma: str, destino: pathlib.Path) -> int:
    partes = [RESX_HEADER]
    escritas = 0
    for chave in sorted(entradas):
        valores = entradas[chave]
        texto = valores.get(idioma)
        if texto is None:
            # Sem traducao: nao emite a chave. O ResourceManager cai no resx neutro
            # sozinho, o que e melhor do que gravar string vazia (que apareceria como
            # rotulo em branco na tela).
            continue
        # xml:space="preserve" porque varias strings terminam em espaco de proposito
        partes.append(f'  <data name="{escape(chave)}" xml:space="preserve">\n')
        partes.append(f"    <value>{escape(texto)}</value>\n")
        nota = valores.get("note")
        if nota:
            partes.append(f"    <comment>{escape(nota)}</comment>\n")
        partes.append("  </data>\n")
        escritas += 1
    partes.append("</root>\n")
    destino.write_text("".join(partes), encoding="utf-8")
    return escritas


def main() -> int:
    raiz = pathlib.Path(__file__).resolve().parent.parent
    origem = raiz / "src" / "Localization" / "strings.json"
    if not origem.exists():
        print(f"nao achei {origem}", file=sys.stderr)
        return 1

    entradas = json.loads(origem.read_text(encoding="utf-8"))

    idiomas = sorted({idioma for v in entradas.values() for idioma in v if idioma != "note"})
    if NEUTRAL not in idiomas:
        print(f"o idioma neutro ({NEUTRAL}) nao aparece em nenhuma entrada", file=sys.stderr)
        return 1

    faltando: dict[str, list[str]] = {}
    for idioma in idiomas:
        sufixo = "" if idioma == NEUTRAL else f".{idioma}"
        destino = raiz / "src" / "Localization" / f"Strings{sufixo}.resx"
        n = gerar(entradas, idioma, destino)
        ausentes = [c for c in entradas if idioma not in entradas[c]]
        if ausentes:
            faltando[idioma] = ausentes
        print(f"{destino.name:<24} {n} de {len(entradas)} strings")

    for idioma, ausentes in faltando.items():
        print(f"\n{idioma}: {len(ausentes)} sem traducao (cai no {NEUTRAL}):", file=sys.stderr)
        for c in ausentes[:20]:
            print(f"  {c}", file=sys.stderr)
        if len(ausentes) > 20:
            print(f"  ... e mais {len(ausentes) - 20}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
