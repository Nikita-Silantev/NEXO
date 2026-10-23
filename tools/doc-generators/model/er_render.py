# -*- coding: utf-8 -*-
import sys, subprocess, json, html
sys.path.insert(0, "model")
from schema_model import *
FONT = "Times New Roman"

def esc(s): return html.escape(s)

def table_node(t, ghost=False):
    name = t["name"]
    if ghost:
        return (f'  "{name}" [shape=box, style="dashed", fontname="{FONT}", fontsize=13, color="#777777", '
                f'label="{name}\\n(другая область)"];')
    rows = []
    pk = set(t["pk"] or [])
    fks = {c["name"] for c in t["cols"] if c["fk"]}
    shown = [c for c in t["cols"] if c["name"] in pk or c["name"] in fks or "uq" in c["flags"]]
    rest = len(t["cols"]) - len(shown)
    for c in shown:
        mark = []
        if c["name"] in pk: mark.append("PK")
        if c["name"] in fks: mark.append("FK")
        if "uq" in c["flags"]: mark.append("UQ")
        typ = c["type"][5:] if c["type"].startswith("enum:") else c["type"]
        rows.append(f'<TR><TD ALIGN="LEFT"><FONT POINT-SIZE="11"><B>{",".join(mark)}</B></FONT></TD>'
                    f'<TD ALIGN="LEFT">{esc(c["name"])}</TD><TD ALIGN="LEFT"><FONT POINT-SIZE="11" COLOR="#555555">{esc(typ)}</FONT></TD></TR>')
    if rest > 0:
        rows.append(f'<TR><TD COLSPAN="3" ALIGN="LEFT"><FONT POINT-SIZE="11" COLOR="#555555"><I>… и ещё атрибутов: {rest}</I></FONT></TD></TR>')
    return (f'  "{name}" [shape=none, margin=0, fontname="{FONT}", label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3">'
            f'<TR><TD COLSPAN="3" BGCOLOR="#D9D9D9" BORDER="1"><B>{name}</B></TD></TR>' + "".join(rows) + '</TABLE>>];')

def render_domain(dom):
    tabs = [t for t in TABLES if t["domain"] == dom]
    names = {t["name"] for t in tabs}
    ghosts = set()
    edges = []
    for t in tabs:
        pk = t["pk"] or []
        for c in t["cols"]:
            if not c["fk"]: continue
            parent = c["fk"]
            if parent not in names: ghosts.add(parent)
            one_to_one = (pk == [c["name"]])
            optional_parent = "nn" not in c["flags"] and c["name"] not in pk
            tail = "teeodot" if optional_parent else "teetee"
            head = "teeodot" if one_to_one else "crowodot"
            edges.append((parent, t["name"], c["name"], tail, head))
    # родители, на которых ссылаются из других областей (входящие связи извне)
    for t in TABLES:
        if t["domain"] == dom: continue
        for c in t["cols"]:
            if c["fk"] in names and c["fk"] != "users":
                ghosts.add(t["name"])
                pk = t["pk"] or []
                one_to_one = (pk == [c["name"]])
                optional_parent = "nn" not in c["flags"] and c["name"] not in pk
                edges.append((c["fk"], t["name"], c["name"], "teeodot" if optional_parent else "teetee",
                              "teeodot" if one_to_one else "crowodot"))
    d = (f'digraph ER {{ rankdir=LR; splines=true; nodesep=0.5; ranksep=1.1; pad=0.3; bgcolor="white";\n'
         f'  node [fontname="{FONT}", fontsize=13]; edge [fontname="{FONT}", fontsize=10, dir=both, arrowsize=0.9];\n')
    for t in tabs: d += table_node(t) + "\n"
    for g in sorted(ghosts - names): d += table_node(TABLE_BY_NAME[g], ghost=True) + "\n"
    for p, ch, col, tail, head in edges:
        style = ' style=dashed' if (p in ghosts - names or ch in ghosts - names) else ""
        d += f'  "{p}" -> "{ch}" [arrowtail={tail}, arrowhead={head}, label="{col}"{style}];\n'
    d += "}\n"
    open(f"diagrams/er_{dom}.dot", "w", encoding="utf-8").write(d)
    subprocess.run(["dot", "-Tpng", "-Gdpi=120", f"diagrams/er_{dom}.dot", "-o", f"diagrams/er_{dom}.png"], check=True)

for dom in DOMAINS: render_domain(dom)
from PIL import Image
for dom in DOMAINS: print(dom, Image.open(f"diagrams/er_{dom}.png").size)
