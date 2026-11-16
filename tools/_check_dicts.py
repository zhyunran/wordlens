import sqlite3, sys
root = sys.argv[1]
en = sqlite3.connect(root + r'\data\build\builtin-en.wlcdb')
zh = sqlite3.connect(root + r'\data\build\builtin-zh.wlcdb')

def show(c, w, label):
    rows = c.execute('SELECT headword,phonetic,translation,definition,pos,tags,frq,collins,oxford FROM entries WHERE fold=? LIMIT 2', (w.lower(),)).fetchall()
    print('===', label, w, 'rows', len(rows))
    for r in rows:
        print('  head:', r[0], '| phon:', r[1], '| frq:', r[6], '| collins:', r[7], '| oxford:', r[8])
        print('  trans:', (r[2] or '')[:160].replace(chr(10), ' / '))
        print('  def  :', (r[3] or '')[:120].replace(chr(10), ' / '))
        print('  pos  :', r[4], '| tags:', r[5])

for w in ['hello', 'world', 'perceive', 'perceived', 'running', 'extraordinary', 'internationalization', 'telescope', 'Galileo', 'china']:
    show(en, w, 'EN')
for w in ['国际化', '国际', '词典', '中国', '一个']:
    show(zh, w, 'ZH')
print('forms perceived:', en.execute('SELECT base FROM forms WHERE form=?', ('perceived',)).fetchall())
print('forms running:', en.execute('SELECT base FROM forms WHERE form=?', ('running',)).fetchall())
print('suggest pre:', en.execute("SELECT headword FROM entries WHERE fold>=? AND fold<? ORDER BY fold LIMIT 8", ('pre', 'prf')).fetchall())
print('counts:', en.execute('SELECT count(*) FROM entries').fetchone(), zh.execute('SELECT count(*) FROM entries').fetchone())
print('meta:', en.execute("SELECT key,value FROM meta WHERE key IN ('name','entry_count','language')").fetchall())
