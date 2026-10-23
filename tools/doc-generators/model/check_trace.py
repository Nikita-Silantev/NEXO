# -*- coding: utf-8 -*-
"""Проверка трассировки: каждое обращение «Сервис.Метод» в требованиях существует в .proto;
каждый метод .proto встречается хотя бы в одном требовании."""
import re, sys, os
sys.path.insert(0, os.path.dirname(__file__))
import requirements as q
from google.protobuf import descriptor_pb2

def run(desc="build/nexo.desc", verbose=True):
    fds = descriptor_pb2.FileDescriptorSet(); fds.ParseFromString(open(desc, "rb").read())
    svc = {s.name: {m.name for m in s.method} for f in fds.file for s in f.service}
    used, errs = set(), []
    for r in q.REQS:
        for part in re.split(r";\s*", r["ref"]):
            m = re.match(r"([A-Z]\w+Service)\.(.+)$", part.strip())
            if not m: continue
            for n in re.split(r",\s*", m.group(2)):
                n = re.match(r"\w+", n.strip())
                if not n: continue
                n = n.group(0)
                if n not in svc[m.group(1)]: errs.append(f"{r['id']}: нет метода {m.group(1)}.{n}")
                else: used.add((m.group(1), n))
    allm = {(s, m) for s, ms in svc.items() for m in ms}
    for s, m in sorted(allm - used): errs.append(f"метод без требования: {s}.{m}")
    if verbose:
        print(f"Требований: {len(q.REQS)}; методов в proto: {len(allm)}; охвачено: {len(used)}")
        for e in errs: print("  ", e)
    return errs

if __name__ == "__main__":
    sys.exit(1 if run() else 0)
