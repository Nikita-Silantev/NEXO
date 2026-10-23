import sys, importlib, time
sys.path.insert(0, "docs"); sys.path.insert(0, "model")
from common import DOCS, LEGAL
names = sys.argv[1:] or ["d01_tz"]
for n in names:
    m = importlib.import_module(n)
    t = time.time()
    d = m.build()
    key = {v[1]: k for k, v in {**DOCS, **LEGAL}.items()}[d.title]
    fn = {**DOCS, **LEGAL}[key][2] + ".docx"
    out, total = d.build("out/Документы", fn)
    print(n, out, "листов:", total, f"{time.time()-t:.0f}s")
