import os
import sqlite3
import sys

# Inspect a built .wlcdb dictionary (default: data/build/builtin-zh.wlcdb).
default = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data', 'build', 'builtin-zh.wlcdb')
p = sys.argv[1] if len(sys.argv) > 1 else default
p = os.path.abspath(p)
if not os.path.exists(p):
    sys.exit('dictionary not found: %s\nBuild it first with tools/build_dicts.py' % p)

print('file', p, os.path.getsize(p), 'bytes')
con = sqlite3.connect(p)
cur = con.cursor()
print('tables', [r[0] for r in cur.execute("select name from sqlite_master where type='table'")])
print('cols entries', [r[1] for r in cur.execute('pragma table_info(entries)')])
for hw in ['同学', '同學']:
    print('---', hw)
    for row in cur.execute('select * from entries where headword=?', (hw,)):
        print(row)
con.close()
