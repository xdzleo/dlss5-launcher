"""Parse renodx game mods' Setting{...} initializers into a JSON manifest.

Usage: python extract_settings_manifest.py <repo_dir> [<repo_dir>...] -o manifest.json
Each repo_dir is a renodx checkout (or fork). Games are read from src/games/<slug>/*.cpp.
Later repos do NOT override slugs already extracted (pass canonical repo first).
"""
import json
import re
import sys
from pathlib import Path


def strip_comments(text: str) -> str:
    """Blank out // and /* */ comments, preserving length and newlines.

    Mods keep old Setting blocks commented out; parsing them publishes dead text as live
    instructions (akuru-q's bmw ships three contradictory status messages that way).
    """
    out = list(text)
    i = 0
    n = len(out)
    while i < n:
        c = out[i]
        if c in '"\'':
            quote = c
            i += 1
            while i < n and out[i] != quote:
                if out[i] == '\\':
                    i += 1
                i += 1
            i += 1
            continue
        if c == '/' and i + 1 < n:
            if out[i + 1] == '/':
                while i < n and out[i] != '\n':
                    out[i] = ' '
                    i += 1
                continue
            if out[i + 1] == '*':
                end = text.find('*/', i + 2)
                end = n if end < 0 else end + 2
                while i < end:
                    if out[i] != '\n':
                        out[i] = ' '
                    i += 1
                continue
        i += 1
    return ''.join(out)


def literal_mask(block: str):
    """True at every position inside a string literal, so a '.' in the author's prose is
    never mistaken for a designated-initializer field."""
    mask = [False] * len(block)
    i = 0
    n = len(block)
    while i < n:
        if block[i] != '"':
            i += 1
            continue
        start = i
        i += 1
        while i < n and block[i] != '"':
            if block[i] == '\\':
                i += 1
            i += 1
        for j in range(start, min(i + 1, n)):
            mask[j] = True
        i += 1
    return mask


def find_setting_blocks(source: str):
    """Yield the inside of every Setting{ ... } block, brace-balanced."""
    text = strip_comments(source)
    for m in re.finditer(r"Setting\s*\{", text):
        depth = 1
        i = m.end()
        start = i
        while i < len(text) and depth > 0:
            c = text[i]
            if c == '{':
                depth += 1
            elif c == '}':
                depth -= 1
            elif c == '"':  # skip string literal
                i += 1
                while i < len(text) and text[i] != '"':
                    if text[i] == '\\':
                        i += 1
                    i += 1
            i += 1
        if depth == 0:
            yield text[start:i - 1]


FIELD_RE = re.compile(r"\.(\w+)\s*=")


def split_fields(block: str):
    """Split a designated-initializer block into (name, raw_value) pairs."""
    mask = literal_mask(block)
    fields = []
    matches = [m for m in FIELD_RE.finditer(block) if not mask[m.start()]]
    for idx, m in enumerate(matches):
        start = m.end()
        # value ends at the comma at depth 0 before the next field (or end)
        end = matches[idx + 1].start() if idx + 1 < len(matches) else len(block)
        raw = block[start:end].strip()
        # strip trailing comma
        if raw.endswith(','):
            raw = raw[:-1].rstrip()
        fields.append((m.group(1), raw))
    return fields


STRING_RE = re.compile(r'^"(.*)"$', re.DOTALL)
NUM_RE = re.compile(r'^-?(?:[0-9]+\.?[0-9]*|\.[0-9]+)f?$')


ESCAPES = {'n': '\n', 'r': '', 't': '  ', '0': '', '\\': '\\', '"': '"', "'": "'"}


def unescape(text: str) -> str:
    """Undo C++ escapes in one left-to-right pass. Chained replaces never handled \\r and
    rewrote their own output."""
    out = []
    i = 0
    while i < len(text):
        if text[i] == '\\' and i + 1 < len(text):
            i += 1
            out.append(ESCAPES.get(text[i], text[i]))
        else:
            out.append(text[i])
        i += 1
    return ''.join(out)


BOILERPLATE_RE = re.compile(
    r"ko-?fi|patreon|discord|paypal|github\.com|buymeacoffee|twitter|donate"
    r"|was compiled on|^build:|^version:?$|^reset( all)?$|^credits?$"
    r"|^github$|^more mods$|special thanks|framework by|^mod by ", re.IGNORECASE)

OPENS_URL_RE = re.compile(r"LaunchURL|https?://|ShellExecute", re.IGNORECASE)
IMPERATIVE_RE = re.compile(
    r"\b(set|use|enable|disable|turn|toggle|adjust|change|open|close|restart|install|update"
    r"|make sure|leave|switch|move|press|click|select|apply|avoid|requires?|must|should|need)\b",
    re.IGNORECASE)
CREDIT_RE = re.compile(
    r"\b(by|thanks to|credits?|maintained|shout-?out|bug hunter|framework)\b", re.IGNORECASE)
SOCIAL_LINE_RE = re.compile(
    r"ko-?fi|patreon|paypal|buymeacoffee|twitter|donate|join .*(discord|server)"
    r"|was compiled on|^\s*build\s*(date)?\s*[:\-]|^\s*version\s*[:\-]", re.IGNORECASE)


def drop_social_lines(text: str) -> str:
    """Drop only the LINE that is social/build noise, never the whole block."""
    kept = [l for l in text.split('\n') if not SOCIAL_LINE_RE.search(l)]
    return '\n'.join(kept).strip()


PRESET_PAIR_RE = re.compile(r'\{\s*"(\w+)"\s*,\s*(-?[\d.]+)f?\s*\}')
UPDATE_SETTING_RE = re.compile(r'UpdateSetting\(\s*"(\w+)"\s*,\s*(-?[\d.]+)f?\s*\)')
LITERAL_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')


def parse_value(raw: str):
    raw = raw.strip()
    # C++ concatenates adjacent string literals, and mod authors write multi-line
    # instructions that way. Reading only the first literal truncated them mid-sentence.
    if raw.startswith('"') or raw.startswith('std::string'):
        lits = LITERAL_RE.findall(raw)
        if lits:
            joined = unescape(''.join(lits))
            return joined if joined.strip() else None
    m = STRING_RE.match(raw)
    if m:
        return m.group(1)
    if NUM_RE.match(raw):
        return float(raw.rstrip('f'))
    if raw in ('true', 'false'):
        return raw == 'true'
    return None  # expression / lambda / enum handled elsewhere


def parse_labels(raw: str):
    if not raw.startswith('{'):
        return None
    return re.findall(r'"((?:[^"\\]|\\.)*)"', raw)


def parse_setting(block: str):
    s = {}
    for name, raw in split_fields(block):
        if name == 'key':
            v = parse_value(raw)
            if v:
                s['key'] = v
        elif name == 'value_type':
            if 'INTEGER' in raw:
                s['type'] = 'int'
            elif 'BOOLEAN' in raw:
                s['type'] = 'bool'
            elif 'BUTTON' in raw:
                s['type'] = 'button'
            elif 'LABEL' in raw or 'TEXT' in raw or 'BULLET' in raw:
                s['type'] = 'label'
            elif 'FLOAT' in raw:
                s['type'] = 'float'
        elif name == 'default_value':
            v = parse_value(raw)
            if v is not None:
                s['default'] = v
        elif name in ('label', 'section', 'tooltip', 'group', 'format'):
            v = parse_value(raw)
            if v is not None:
                s[name] = v
        elif name in ('min', 'max'):
            v = parse_value(raw)
            if v is not None:
                s[name] = v
        elif name == 'labels':
            v = parse_labels(raw)
            if v:
                s['labels'] = v
        elif name == 'is_global':
            v = parse_value(raw)
            if v is not None:
                s['is_global'] = v
        elif name == 'binding':
            s['_has_binding'] = True
    s.setdefault('type', 'float')
    # TEXT/LABEL/BULLET/BUTTON blocks carry no key because they are not knobs: they are what the
    # mod's author wrote for the PLAYER inside the overlay. Dropping them is why games whose
    # author documented the whole procedure showed up in the launcher as "no adjustable settings".
    if s['type'] in ('button', 'label'):
        text = (s.get('label') or s.get('tooltip') or '').strip()
        section = (s.get('section') or '').strip()
        values = {}
        if s['type'] == 'button':
            values = {k: float(v) for k, v in PRESET_PAIR_RE.findall(block)}
            values.update({k: float(v) for k, v in UPDATE_SETTING_RE.findall(block)})
        # a button whose only job is opening a URL is a link, not guidance
        if not values and OPENS_URL_RE.search(block):
            return None
        text = drop_social_lines(text)
        guidance = section.lower() in ('instructions', 'notes', 'read me', 'readme', 'how to')
        if not text:
            return None
        # the author filed it under Instructions: believe them, whatever words it contains
        if not guidance and not values:
            if section.lower() == 'links' or BOILERPLATE_RE.search(text):
                return None
            if not IMPERATIVE_RE.search(text) and CREDIT_RE.search(text):
                return None
        s['instruction'] = True
        s['key'] = ''
        s['label'] = text
        if values:
            s['values'] = values
        s.pop('_has_binding', None)
        return s
    if 'key' not in s:
        return None
    s.pop('_has_binding', None)
    return s


def extract_game(game_dir: Path):
    settings = []
    seen = set()
    for cpp in sorted(game_dir.glob('*.cpp')):
        try:
            text = cpp.read_text(encoding='utf-8', errors='replace')
        except OSError:
            continue
        for block in find_setting_blocks(text):
            s = parse_setting(block)
            if not s:
                continue
            # instruction blocks share the empty key on purpose — deduping them by key
            # would keep only the first paragraph of the author's explanation
            if s.get('instruction'):
                settings.append(s)
            elif s['key'] not in seen:
                seen.add(s['key'])
                settings.append(s)
    return settings


def main(argv):
    out_path = None
    repos = []
    it = iter(argv)
    for a in it:
        if a == '-o':
            out_path = next(it)
        else:
            repos.append(Path(a))
    manifest = {}
    for repo in repos:
        games = repo / 'src' / 'games'
        if not games.is_dir():
            print(f'skip (no src/games): {repo}', file=sys.stderr)
            continue
        for game_dir in sorted(games.iterdir()):
            if not game_dir.is_dir():
                continue
            slug = game_dir.name
            if slug in manifest:
                continue
            # Grava tambem os slugs com ZERO settings: o app precisa distinguir
            # "mod sem opcoes ajustaveis" de "mod que eu nao conheco".
            manifest[slug] = extract_game(game_dir)
    print(f'{sum(len(v) for v in manifest.values())} settings across {len(manifest)} games')
    if out_path:
        Path(out_path).write_text(json.dumps(manifest, indent=1, ensure_ascii=False), encoding='utf-8')
        print(f'wrote {out_path}')


if __name__ == '__main__':
    main(sys.argv[1:])
