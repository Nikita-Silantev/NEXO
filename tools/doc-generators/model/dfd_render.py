# -*- coding: utf-8 -*-
import sys, textwrap, subprocess, json, os
sys.path.insert(0, "model")
from dfd_model import *

FONT = "Times New Roman"

def wrap(s, w=26):
    out = []
    for part in s.split("\n"):
        out.extend(textwrap.wrap(part, w) or [""])
    return "\\n".join(x.replace('"', "'") for x in out)

def node_entity(i, name):
    return f'  "{i}" [shape=box, style="filled", fillcolor="#D9D9D9", penwidth=2, fontname="{FONT}", fontsize=13, label="{i}\\n{wrap(name,28)}", margin="0.15,0.1"];'

def node_store(i, name):
    n = wrap(name, 30).replace("\\n", "<BR/>")
    return (f'  "{i}" [shape=none, margin=0, label=<<TABLE BORDER="0" CELLBORDER="1" CELLSPACING="0" CELLPADDING="5">'
            f'<TR><TD SIDES="LTB" BGCOLOR="#EFEFEF"><B>{i}</B></TD><TD SIDES="TB" ALIGN="LEFT">{n}</TD></TR></TABLE>>, fontname="{FONT}", fontsize=12];')

def node_proc(i, name, w=22, big=False):
    n = wrap(name, w).replace("\\n", "<BR/>")
    fs = 14 if big else 13
    return (f'  "{i}" [shape=none, margin=0, label=<<TABLE STYLE="ROUNDED" BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="5">'
            f'<TR><TD SIDES="B" BORDER="1" ALIGN="LEFT"><B>{i}</B></TD></TR><TR><TD><FONT POINT-SIZE="{fs}">{n}</FONT></TD></TR></TABLE>>, fontname="{FONT}"];')

def edge(s, d, label, num=None, fs=11):
    lab = f"{num}" if num is not None else wrap(label, 24)
    return f'  "{s}" -> "{d}" [label="{lab}", fontname="{FONT}", fontsize={fs}, arrowsize=0.8];'

def header(title, rankdir="LR", extra=""):
    return (f'digraph G {{\n  rankdir={rankdir}; splines=true; nodesep=0.45; ranksep=1.0; pad=0.3; '
            f'bgcolor="white"; fontname="{FONT}"; {extra}\n  node [fontname="{FONT}"]; edge [fontname="{FONT}"];\n')

def render(dot_text, name, dpi=130):
    open(f"diagrams/{name}.dot", "w", encoding="utf-8").write(dot_text)
    subprocess.run(["dot", "-Tpng", f"-Gdpi={dpi}", f"diagrams/{name}.dot", "-o", f"diagrams/{name}.png"], check=True)

def main():
    errs = check()
    assert not errs, errs
    nodes_meta = {}
    # ---- контекст ----
    d = header("ctx", "LR")
    d += node_proc("0", "Система NEXO", 18, True).replace("{0|", "{0|") + "\n"
    ents = sorted({x for s, dd, _ in CONTEXT_FLOWS for x in (s, dd) if x.startswith("E")})
    for e in ents: d += node_entity(e, ENTITIES[e]) + "\n"
    for s, dd, l in CONTEXT_FLOWS: d += edge(s, dd, l, fs=12) + "\n"
    d += "}\n"
    render(d, "dfd0")
    # ---- уровень 1 (два представления одной диаграммы) ----
    flows = sorted(derive_l1(), key=lambda f: (f[0], f[1], f[2]))
    numbered = [(i + 1, s_, dd, l) for i, (s_, dd, l) in enumerate(flows)]
    json.dump(numbered, open("build/dfd1_flows.json", "w", encoding="utf-8"), ensure_ascii=False)
    for view in ("a", "b"):
        sel = [(n, s_, dd, l) for n, s_, dd, l in numbered
               if (view == "b") == (s_.startswith("D") or dd.startswith("D"))]
        d = header("l1", "LR", "newrank=true; ranksep=1.1; nodesep=0.35;")
        dup = {}
        if view == "a":
            for n, s_, dd, l in sel:
                for x, other in ((s_, dd), (dd, s_)):
                    if x == "E1" and other.startswith("P"): dup[other] = f"E1_{other}"
            for other, nid in sorted(dup.items()):
                d += f'  "{nid}" [shape=box, style="filled", fillcolor="#D9D9D9", penwidth=2, fontname="{FONT}", fontsize=12, label="E1\\nПользователь", margin="0.12,0.06"];\n'
        used = {x for _, s_, dd, _ in sel for x in (s_, dd)}
        for e in sorted(ENTITIES):
            if e in used and e != "E1": d += node_entity(e, ENTITIES[e]) + "\n"
        for pid in PROCESSES:
            if pid in used: d += node_proc(pid, PROCESSES[pid], 20, True) + "\n"
        for k, (n, _) in STORES.items():
            if k in used: d += node_store(k, n) + "\n"
        agg = {}
        for n, s_, dd, l in sel:
            s2 = dup[dd] if s_ == "E1" else s_
            d2 = dup[s_] if dd == "E1" else dd
            agg.setdefault(tuple(sorted((s2, d2))), []).append((n, s2, d2))
        for (x, y), items in sorted(agg.items()):
            fwd = [n for n, a_, b_ in items if (a_, b_) == (x, y)]
            bwd = [n for n, a_, b_ in items if (a_, b_) == (y, x)]
            lab = wrap(", ".join(f"F{n}" for n in sorted(fwd + bwd)), 16)
            dirattr = "both" if fwd and bwd else "forward"
            src, dst = (x, y) if fwd else (y, x)
            d += f'  "{src}" -> "{dst}" [dir={dirattr}, label="{lab}", fontname="{FONT}", fontsize=11, arrowsize=0.7];\n'
        d += "}\n"
        render(d, f"dfd1{view}", dpi=110)
    # ---- уровень 2 ----
    for parent, spec in L2.items():
        inner = spec["inner"]
        outside = sorted({x for s, dd, _, _ in spec["flows"] for x in (s, dd) if x not in inner})
        d = header(parent, "LR", "ranksep=1.3; nodesep=0.4;")
        for x in outside:
            if x.startswith("E"): d += node_entity(x, ENTITIES[x]) + "\n"
            elif x.startswith("D"): d += node_store(x, STORES[x][0]) + "\n"
            else: d += node_proc(x, PROCESSES[x], 20, False) + "\n"
        d += f'  subgraph cluster_{parent} {{ label="Процесс {parent[1:]}. {PROCESSES[parent]}"; labeljust=l; style=dashed; fontname="{FONT}"; fontsize=14; color="#666666";\n'
        for i, n in inner.items(): d += "  " + node_proc(i, n, 22, True) + "\n"
        d += "  }\n"
        nums = []
        for k, (s, dd, l, _) in enumerate(spec["flows"], 1):
            tag = f"{parent[1:]}.{k}"
            nums.append((tag, s, dd, l))
            d += edge(s, dd, l, num=f"({tag})", fs=11) + "\n"
        json.dump(nums, open(f"build/dfd2_{parent}_flows.json", "w", encoding="utf-8"), ensure_ascii=False)
        d += "}\n"
        render(d, f"dfd2_{parent}", dpi=110)
    print("ok")

if __name__ == "__main__":
    main()
