import os, struct, sys

root = sys.argv[1]
out = os.path.join(root, 'tests', 'fixtures', 'stardict-test')
os.makedirs(out, exist_ok=True)

entries = [
    ("wordlens", "WordLens test entry: a tiny StarDict dictionary.\nSecond line of meaning."),
    ("telescope", "望远镜 (imported from the test StarDict fixture)."),
    ("galaxy", "星系；银河 (test fixture)."),
]

idx = bytearray()
data = bytearray()
for word, meaning in entries:
    wb = word.encode('utf-8') + b'\x00'
    idx += wb
    idx += struct.pack('>II', len(data), len(meaning.encode('utf-8')))
    data += meaning.encode('utf-8')

with open(os.path.join(out, 'wordlens-test.idx'), 'wb') as f:
    f.write(idx)
with open(os.path.join(out, 'wordlens-test.dict'), 'wb') as f:
    f.write(data)

ifo = (
    "StarDict's dict ifo file\n"
    "version=2.4.2\n"
    "wordcount=%d\n"
    "idxfilesize=%d\n"
    "bookname=WordLens 测试词典\n"
    "author=WordLens tests\n"
    "description=Small fixture dictionary for automated tests.\n"
    "sametypesequence=m\n"
) % (len(entries), len(idx))
with open(os.path.join(out, 'wordlens-test.ifo'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(ifo)
print('fixture written to', out)
