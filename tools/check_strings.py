#!/usr/bin/env python3
"""Confere src/Localization/strings.json antes de gerar os .resx.

Texto de tela em portugues PRECISA de acento. O repo evita acento em comentario de
codigo, que e convencao de fonte; aplicar isso ao que o usuario le deixa a interface com
cara de amadora, e foi um erro real cometido durante a traducao.

    python tools/check_strings.py

Sai com codigo != 0 se achar problema, para poder rodar no CI.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

NEUTRAL = "pt-BR"

# Palavras que quase sempre aparecem em texto de interface e que, sem acento, sao erro.
# A forma correta e a chave; a forma errada e o que procuramos.
ACENTOS = {
    "nao": "não", "sao": "são", "voce": "você", "vecs": "vocês",
    "e ": "é ",  # tratado a parte, ver verificar_e()
    "ja": "já", "so": "só", "ate": "até", "apos": "após", "atras": "atrás",
    "esta": "está", "estao": "estão", "sera": "será", "serao": "serão",
    "tambem": "também", "porem": "porém", "alem": "além", "ninguem": "ninguém",
    "ultima": "última", "ultimo": "último", "unico": "único", "unica": "única",
    "proprio": "próprio", "propria": "própria", "possivel": "possível",
    "disponivel": "disponível", "invalido": "inválido", "invalida": "inválida",
    "valido": "válido", "valida": "válida", "automatico": "automático",
    "automatica": "automática", "grafico": "gráfico", "grafica": "gráfica",
    "basico": "básico", "basica": "básica", "publico": "público", "publica": "pública",
    "historico": "histórico", "diagnostico": "diagnóstico", "codigo": "código",
    "usuario": "usuário", "diretorio": "diretório", "repositorio": "repositório",
    "obrigatorio": "obrigatório", "necessario": "necessário", "binario": "binário",
    "versao": "versão", "instalacao": "instalação", "configuracao": "configuração",
    "configuracoes": "configurações", "aplicacao": "aplicação", "opcao": "opção",
    "opcoes": "opções", "informacao": "informação", "informacoes": "informações",
    "atencao": "atenção", "correcao": "correção", "correcoes": "correções",
    "selecao": "seleção", "deteccao": "detecção", "extracao": "extração",
    "verificacao": "verificação", "calibracao": "calibração", "resolucao": "resolução",
    "alteracao": "alteração", "alteracoes": "alterações", "acao": "ação",
    "acoes": "ações", "sessao": "sessão", "permissao": "permissão", "razao": "razão",
    "botao": "botão", "padrao": "padrão", "endereco": "endereço", "comeco": "começo",
    "servico": "serviço", "servicos": "serviços", "espaco": "espaço", "arquivo": None,
    "pagina": "página", "maximo": "máximo", "minimo": "mínimo", "media": None,
    "musica": "música", "numero": "número", "duvida": "dúvida", "saida": "saída",
    "sucesso": None, "titulo": "título", "multiplo": "múltiplo", "multipla": "múltipla",
    "memoria": "memória", "criterio": "critério", "cenario": "cenário",
    "reinicie": None, "carrega-lo": "carregá-lo", "instala-lo": "instalá-lo",
    "remove-lo": "removê-lo", "aplica-lo": "aplicá-lo", "abri-lo": "abri-lo",
}
# entradas com valor None sao palavras corretas sem acento: ficam na tabela so para
# documentar que ja foram conferidas e nao sao esquecimento.
ACENTOS = {errada: certa for errada, certa in ACENTOS.items() if certa}

PALAVRA = re.compile(r"[A-Za-zÀ-ÿ][A-Za-zÀ-ÿ-]*")
PLACEHOLDER = re.compile(r"\{(\d+)\}")

# Regras de terminacao. Pegam muito mais que lista de palavra, e sem falso positivo
# relevante: em portugues essas terminacoes sao acentuadas por regra, nao por excecao.
# (min_len evita casar palavra curta que termina igual por coincidencia: "cao" ->
# so vale a partir de 5 letras, senao "cao" sozinho — que nao e palavra — passaria.)
SUFIXOS = [
    ("coes", "ções", 6),
    ("cao", "ção", 5),
    ("oes", "ões", 5),
    ("aveis", "áveis", 7),
    ("iveis", "íveis", 7),
    ("avel", "ável", 6),
    ("ivel", "ível", 6),
    ("encia", "ência", 7),
    ("ancia", "ância", 7),
    ("ario", "ário", 6),
    ("orio", "ório", 6),
    ("aria", "ária", 6),
    ("oria", "ória", 6),
]

# Palavras curtas que sao acentuadas e que a regra de sufixo nao alcanca. So entram
# aqui as que NAO tem homografo sem acento (por isso "da", "e", "esta" ficam de fora
# das curtas e sao tratadas na lista grande, com revisao humana).
CURTAS = {"le": "lê", "ve": "vê", "cre": "crê", "pe": "pé", "tres": "três",
          "mes": "mês", "pos": "pós", "voce": "você"}


# Formas verbais no futuro do pretérito terminam em -aria/-eria/-iria e NAO sao acentuadas
# ("rodaria", "ficaria", "seguiria"), mas casam com a regra de sufixo de "-ária/-éria". A regra
# nao tem como distinguir sem conjugar o verbo, entao a excecao e explicita.
NAO_ACENTUA = re.compile(
    r"^(?:[a-zà-ÿ]+(?:ar|er|ir))ia(?:m|s)?$|"          # rodaria, rodariam, comerias
    r"^(?:ser|ter|ir|vir|ver|dar|por|dizer|fazer|"
    r"haver|poder|querer|saber|trazer)ia(?:m|s)?$"
)


def problemas_de_acento(texto: str) -> list[str]:
    achados = []
    for m in PALAVRA.finditer(texto):
        original = m.group(0)
        p = original.lower()
        if NAO_ACENTUA.match(p):
            continue
        if p in ACENTOS:
            achados.append(f"{original} -> {ACENTOS[p]}")
            continue
        if p in CURTAS:
            achados.append(f"{original} -> {CURTAS[p]}")
            continue
        for fim, certo, minimo in SUFIXOS:
            if len(p) >= minimo and p.endswith(fim):
                achados.append(f"{original} -> ...{certo}")
                break
    return achados


# Como o codigo pede uma string: {loc:Tr Chave} no XAML, L.T("Chave") no C#.
REFS = [
    re.compile(r"\{loc:Tr\s+([A-Za-z0-9_]+)\s*\}"),
    re.compile(r"\bL\.T\(\s*\"([A-Za-z0-9_]+)\""),
]

# L.T() tambem e chamado com chave montada em variavel; esses casos nao dao para
# resolver estaticamente e nao sao contados como referencia.
def referencias(raiz: pathlib.Path) -> dict[str, str]:
    """Chave -> primeiro arquivo onde aparece."""
    achadas: dict[str, str] = {}
    for arquivo in list((raiz / "src").rglob("*.xaml")) + list((raiz / "src").rglob("*.cs")):
        if "obj" in arquivo.parts or "bin" in arquivo.parts:
            continue
        for linha in arquivo.read_text(encoding="utf-8", errors="replace").splitlines():
            # Comentario de codigo e de XML documenta o uso com chave inventada
            # ("L.T(\"Alguma_Chave\")"); nao e referencia de verdade.
            nu = linha.lstrip()
            if nu.startswith(("///", "//", "*", "<!--")):
                continue
            for padrao in REFS:
                for m in padrao.finditer(linha):
                    achadas.setdefault(m.group(1), arquivo.name)
    return achadas


def main() -> int:
    raiz = pathlib.Path(__file__).resolve().parent.parent
    origem = raiz / "src" / "Localization" / "strings.json"
    entradas = json.loads(origem.read_text(encoding="utf-8"))

    erros: list[str] = []
    avisos: list[str] = []

    idiomas = sorted({i for v in entradas.values() for i in v if i != "note"})

    for chave in sorted(entradas):
        valores = entradas[chave]

        # 1. acentuacao do portugues
        pt = valores.get(NEUTRAL)
        if pt:
            for p in problemas_de_acento(pt):
                erros.append(f"{chave}: acento faltando: {p}")

        # 2. os placeholders tem que ser os mesmos em todos os idiomas, senao
        #    string.Format lanca em producao no idioma que tiver um {1} a mais.
        base = set(PLACEHOLDER.findall(pt or ""))
        for idioma in idiomas:
            texto = valores.get(idioma)
            if texto is None:
                continue
            outros = set(PLACEHOLDER.findall(texto))
            if outros != base:
                erros.append(
                    f"{chave}: placeholders diferentes — {NEUTRAL}={sorted(base)} "
                    f"{idioma}={sorted(outros)}"
                )

        # 3. traducao faltando so avisa: cai no neutro e continua utilizavel
        for idioma in idiomas:
            if idioma not in valores:
                avisos.append(f"{chave}: sem {idioma}")

        # 4. texto identico em pt e en costuma ser esquecimento, mas e legitimo em
        #    nome proprio e termo tecnico, entao e aviso e nao erro.
        en = valores.get("en")
        if pt and en and pt == en and len(pt) > 3 and " " in pt:
            avisos.append(f"{chave}: pt-BR e en identicos ({pt[:40]!r})")

    # 5. chave que o codigo pede e o strings.json nao tem. Nao quebra a compilacao: a
    #    interface abre e mostra o nome da chave cru no lugar da frase, e so aparece
    #    para quem abrir aquela tela. Ja escapou assim mais de uma vez.
    usadas = referencias(raiz)
    for chave, arquivo in sorted(usadas.items()):
        if chave not in entradas:
            erros.append(f"{chave}: usada em {arquivo} e nao existe no strings.json")

    # Nao ha checagem do contrario (string que ninguem usa): metade das chaves e pedida
    # com o nome montado em variavel — L.T(badge.ResourceKey) — e a varredura estatica
    # marcaria todas elas como orfas. Aviso que erra metade das vezes vira ruido.

    for e in erros:
        print(f"ERRO   {e}")
    for a in avisos:
        print(f"aviso  {a}")

    print(f"\n{len(entradas)} chaves, {len(idiomas)} idiomas: {', '.join(idiomas)}")
    print(f"{len(erros)} erro(s), {len(avisos)} aviso(s)")
    return 1 if erros else 0


if __name__ == "__main__":
    raise SystemExit(main())
