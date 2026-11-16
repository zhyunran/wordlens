#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Build WordLens dictionary databases (.wlcdb = SQLite) from open data sources.

  * ECDICT (English -> Chinese, with IPA phonetics, English definitions, tags)
  * CC-CEDICT (Chinese <-> English, pinyin)

Usage:
  python build_dicts.py --raw ../data/raw --out ../data/build

Schema (per .wlcdb file):
  meta(key TEXT PRIMARY KEY, value TEXT)
  entries(id INTEGER PRIMARY KEY AUTOINCREMENT, headword TEXT, fold TEXT, sw TEXT,
          phonetic TEXT, translation TEXT, definition TEXT, pos TEXT, tags TEXT,
          exchange TEXT, extra TEXT, collins INT, oxford INT, bnc INT, frq INT)
  forms(form TEXT PRIMARY KEY, base TEXT)
"""
import argparse
import csv
import json
import os
import re
import sqlite3
import sys
import time
import zipfile
from datetime import datetime

sys.stdout.reconfigure(encoding="utf-8")

CJK_RE = re.compile(r"[\u3400-\u9fff\uf900-\ufaff]")
PINYIN_RE = re.compile(r"^([A-Za-zü:]+)([0-5])$")
TONE_MARKS = {
    "a": "āáǎà", "e": "ēéěè", "i": "īíǐì",
    "o": "ōóǒò", "u": "ūúǔù", "ü": "ǖǘǚǜ",
}


def strip_word(w: str) -> str:
    return "".join(c for c in w if c.isalnum()).lower()


def norm_key(w: str) -> str:
    return w.strip().lower()


def to_int(s: str) -> int:
    try:
        return int(str(s).strip())
    except Exception:
        return 0


def split_lines(s: str):
    if not s:
        return []
    s = s.replace("\\n", "\n").replace("\r\n", "\n").replace("\r", "\n")
    return [x.strip() for x in s.split("\n") if x.strip()]


def pinyin_tone(syl: str) -> str:
    """Convert one numbered pinyin syllable, e.g. chuan2 -> chuán."""
    m = PINYIN_RE.match(syl)
    if not m:
        return syl
    base, tone = m.group(1), int(m.group(2))
    base = base.replace("u:", "ü").replace("U:", "Ü").replace("v", "ü").replace("V", "Ü")
    if tone == 0 or tone == 5:
        return base
    low = base.lower()
    idx = -1
    if "a" in low:
        idx = low.index("a")
    elif "o" in low:
        idx = low.index("o")
    elif "e" in low:
        idx = low.index("e")
    elif "iu" in low:
        idx = low.index("iu") + 1
    elif "ui" in low:
        idx = low.index("ui") + 1
    else:
        for v in ("ü", "i", "u"):
            if v in low:
                idx = low.rindex(v)
                break
    if idx < 0:
        return base
    vowel = base[idx]
    marks = TONE_MARKS.get(vowel.lower())
    if not marks:
        return base
    ch = marks[tone - 1] if 1 <= tone <= 4 else vowel
    if vowel.isupper():
        ch = ch.upper()
    return base[:idx] + ch + base[idx + 1:]


def pinyin_display(raw: str) -> str:
    return " ".join(pinyin_tone(x) for x in raw.split())


def make_db(path: str) -> sqlite3.Connection:
    if os.path.exists(path):
        os.remove(path)
    con = sqlite3.connect(path)
    con.executescript(
        """
        PRAGMA journal_mode=OFF;
        PRAGMA synchronous=OFF;
        PRAGMA temp_store=MEMORY;
        PRAGMA cache_size=-64000;
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE entries(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            headword TEXT NOT NULL,
            fold TEXT NOT NULL,
            sw TEXT,
            phonetic TEXT,
            translation TEXT,
            definition TEXT,
            pos TEXT,
            tags TEXT,
            exchange TEXT,
            extra TEXT,
            collins INTEGER DEFAULT 0,
            oxford INTEGER DEFAULT 0,
            bnc INTEGER DEFAULT 0,
            frq INTEGER DEFAULT 0
        );
        CREATE TABLE forms(form TEXT PRIMARY KEY, base TEXT);
        """
    )
    return con


def finish_db(con: sqlite3.Connection, meta: dict, entries: int, forms: int):
    con.executemany("INSERT OR REPLACE INTO meta(key,value) VALUES(?,?)",
                    [(k, str(v)) for k, v in meta.items()])
    con.commit()
    con.executescript(
        """
        CREATE INDEX idx_entries_fold ON entries(fold);
        CREATE INDEX idx_entries_sw ON entries(sw);
        CREATE INDEX idx_forms_form ON forms(form);
        """
    )
    con.commit()
    con.execute("ANALYZE")
    con.commit()



def load_ipa(raw: str) -> dict:
    """Load open-dict-data/ipa-dict (MIT). UK first, US fills the gaps."""
    result = {}
    # The en_US data uses standard IPA stress placement. (en_UK in this dataset
    # places the stress mark before the stressed vowel, which looks wrong in a popup.)
    path = os.path.join(raw, "ipa", "en_US.txt")
    if not os.path.exists(path):
        return result
    count = 0
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 2:
                continue
            word = parts[0].strip().lower()
            variants = parts[1].split(", ")
            phonetic = variants[0].strip().strip("/")
            phonetic = phonetic.strip().strip("/")
            if word and phonetic and word not in result:
                result[word] = phonetic
                count += 1
    print("[ipa-dict] en_US.txt: %d entries" % count)
    return result
def build_ecdict(raw: str, out: str, stats: dict):
    csv_path = os.path.join(raw, "ecdict.csv")
    print("[ECDICT] reading", csv_path)
    ipa = load_ipa(raw)
    print("[ECDICT] IPA dictionary entries:", len(ipa))
    con = make_db(out)
    forms = {}
    batch = []
    n = 0
    ipa_hits = 0
    t0 = time.time()
    with open(csv_path, encoding="utf-8-sig", newline="") as f:
        r = csv.DictReader(f)
        for row in r:
            word = (row.get("word") or "").strip()
            if not word:
                continue
            phonetic = (row.get("phonetic") or "").strip()
            ipa_value = ipa.get(word.lower())
            if ipa_value:
                phonetic = ipa_value
                ipa_hits += 1
            translation = "\n".join(split_lines(row.get("translation") or ""))
            definition = "\n".join(split_lines(row.get("definition") or ""))
            if not translation and not definition:
                continue
            fold = norm_key(word)
            sw = strip_word(word)
            pos = (row.get("pos") or "").strip()
            tags = (row.get("tag") or "").strip()
            exchange = (row.get("exchange") or "").strip()
            extra = {}
            if exchange:
                extra["exchange"] = exchange
            batch.append((
                word, fold, sw, phonetic, translation, definition, pos, tags,
                exchange, json.dumps(extra, ensure_ascii=False) if extra else None,
                to_int(row.get("collins")), to_int(row.get("oxford")),
                to_int(row.get("bnc")), to_int(row.get("frq")),
            ))
            # forms
            if exchange:
                for part in exchange.split("/"):
                    if ":" not in part:
                        continue
                    typ, val = part.split(":", 1)
                    val = val.strip()
                    if typ == "0" and val:
                        forms[fold] = val.lower()
                    elif typ in ("p", "d", "i", "3", "r", "t", "s") and val:
                        forms[val.lower()] = fold
            n += 1
            if len(batch) >= 4000:
                con.executemany("INSERT INTO entries(headword,fold,sw,phonetic,translation,definition,pos,tags,exchange,extra,collins,oxford,bnc,frq) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)", batch)
                batch.clear()
    if batch:
        con.executemany("INSERT INTO entries(headword,fold,sw,phonetic,translation,definition,pos,tags,exchange,extra,collins,oxford,bnc,frq) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)", batch)
    con.commit()
    print("[ECDICT] %d entries in %.1fs, forms so far %d" % (n, time.time() - t0, len(forms)))

    # lemma.en.txt:  "be/4109826 -> is,was,are,were,'s,been,being,'re,'m,am,m"
    lemma_path = os.path.join(raw, "lemma.en.txt")
    if os.path.exists(lemma_path):
        cnt = 0
        with open(lemma_path, encoding="utf-8", errors="ignore") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith(";"):
                    continue
                if "->" not in line:
                    continue
                left, right = line.split("->", 1)
                lemma = left.strip().split("/")[0].strip().lower()
                if not lemma:
                    continue
                for form in right.split(","):
                    form = form.strip().lower()
                    if form and form != lemma:
                        forms.setdefault(form, lemma)
                        cnt += 1
        print("[ECDICT] lemma forms added %d" % cnt)

    fbatch = []
    for form, base in forms.items():
        if form and base and form != base:
            fbatch.append((form, base))
    con.executemany("INSERT OR REPLACE INTO forms(form,base) VALUES(?,?)", fbatch)
    con.commit()
    meta = {
        "name": "ECDICT 英汉词典",
        "language": "en",
        "type": "builtin",
        "source": "ECDICT (github.com/skywind3000/ECDICT)",
        "version": "1.0.28",
        "description": "英汉双解词典：音标、中文释义、英文释义、词频与考试标签（ECDICT，免费数据）",
        "license": "ECDICT data, free to use",
        "built_at": datetime.now().isoformat(timespec="seconds"),
        "entry_count": n,
        "form_count": len(fbatch),
        "priority": 100,
        "app": "WordLens",
    }
    finish_db(con, meta, n, len(fbatch))
    con.close()
    stats["ecdict"] = {"entries": n, "forms": len(fbatch), "ipa_hits": ipa_hits,
                       "size": os.path.getsize(out)}
    print("[ECDICT] done ->", out, os.path.getsize(out), "bytes")


def build_cedict(raw: str, out: str, stats: dict):
    zpath = os.path.join(raw, "cedict.zip")
    print("[CEDICT] reading", zpath)
    con = make_db(out)
    batch = []
    trad_count = simp_count = 0
    t0 = time.time()
    with zipfile.ZipFile(zpath) as z:
        names = [n for n in z.namelist() if n.endswith(".u8")]
        text = z.read(names[0]).decode("utf-8")
    for line in text.splitlines():
        if not line or line.startswith("#"):
            continue
        m = re.match(r"^(\S+)\s+(\S+)\s+\[([^\]]*)\]\s+/(.*)/\s*$", line)
        if not m:
            continue
        trad, simp, pinyin_raw, defs = m.group(1), m.group(2), m.group(3), m.group(4)
        defs_list = [d.strip() for d in defs.split("/") if d.strip()]
        if not defs_list:
            continue
        pinyin = pinyin_display(pinyin_raw)
        translation = "\n".join(defs_list)
        extra = json.dumps({"simp": simp, "trad": trad, "pinyin": pinyin},
                           ensure_ascii=False)
        # one row per written form so traditional and simplified both hit
        for head, variant in ((simp, "simp"), (trad, "trad")):
            if not head:
                continue
            batch.append((
                head, head, strip_word(head), pinyin, translation, "",
                "", "", "", extra, 0, 0, 0, 0,
            ))
            if variant == "simp":
                simp_count += 1
            else:
                trad_count += 1
        if len(batch) >= 4000:
            con.executemany("INSERT INTO entries(headword,fold,sw,phonetic,translation,definition,pos,tags,exchange,extra,collins,oxford,bnc,frq) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)", batch)
            batch.clear()
    if batch:
        con.executemany("INSERT INTO entries(headword,fold,sw,phonetic,translation,definition,pos,tags,exchange,extra,collins,oxford,bnc,frq) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)", batch)
    con.commit()
    n = simp_count + trad_count
    meta = {
        "name": "CC-CEDICT 汉英词典",
        "language": "zh",
        "type": "builtin",
        "source": "CC-CEDICT (cc-cedict.org)",
        "version": datetime.now().strftime("%Y.%m.%d"),
        "description": "汉英词典：拼音（带声调）与英文释义，简繁双收（CC-CEDICT，CC BY-SA 4.0）",
        "license": "CC BY-SA 4.0",
        "built_at": datetime.now().isoformat(timespec="seconds"),
        "entry_count": n,
        "form_count": 0,
        "priority": 90,
        "app": "WordLens",
    }
    finish_db(con, meta, n, 0)
    con.close()
    stats["cedict"] = {"entries": n, "size": os.path.getsize(out),
                       "simplified": simp_count, "traditional": trad_count}
    print("[CEDICT] %d rows in %.1fs -> %s (%d bytes)" %
          (n, time.time() - t0, out, os.path.getsize(out)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw", default=os.path.join(os.path.dirname(__file__), "..", "data", "raw"))
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "data", "build"))
    args = ap.parse_args()
    raw = os.path.abspath(args.raw)
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    stats = {}
    t0 = time.time()
    build_ecdict(raw, os.path.join(out, "builtin-en.wlcdb"), stats)
    build_cedict(raw, os.path.join(out, "builtin-zh.wlcdb"), stats)
    stats["build_seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(out, "data-stats.json"), "w", encoding="utf-8") as f:
        json.dump(stats, f, ensure_ascii=False, indent=2)
    print(json.dumps(stats, ensure_ascii=False, indent=2))
    print("ALL_DONE")


if __name__ == "__main__":
    main()
