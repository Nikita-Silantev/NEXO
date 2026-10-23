# -*- coding: utf-8 -*-
import sys, re
sys.path.insert(0, "model")
from schema_model import ENUMS
from google.protobuf import descriptor_pb2

fds = descriptor_pb2.FileDescriptorSet()
fds.ParseFromString(open("build/nexo.desc", "rb").read())

def camel_to_snake(n):
    return re.sub(r'(?<!^)(?=[A-Z])', '_', n).lower()

proto_enums = {}
for f in fds.file:
    if not f.name.endswith(".proto") or f.name.startswith("google/"): continue
    for e in f.enum_type:
        proto_enums[camel_to_snake(e.name)] = [v.name for v in e.value]

problems = []
mapped = 0
alias = {"legal_status": "legal_status", "visibility": "visibility", "upload_state": "upload_state"}
for db_name, vals in ENUMS.items():
    pe = proto_enums.get(db_name)
    if pe is None:
        continue
    prefix = db_name.upper() + "_"
    pvals = [v[len(prefix):] for v in pe if v.startswith(prefix)]
    dvals = [v for v, _ in vals]
    missing_in_proto = [v for v in dvals if v not in pvals]
    extra_in_proto = [v for v in pvals if v not in dvals and v not in ("UNSPECIFIED",)]
    mapped += 1
    if missing_in_proto:
        problems.append(f"{db_name}: нет в proto {missing_in_proto}")
    if extra_in_proto:
        problems.append(f"{db_name}: лишнее в proto {extra_in_proto} (допустимо только если помечено как не-БД)")
print("сопоставлено перечислений БД и proto:", mapped)
print("замечания:", problems or "нет")
# сервисы
n_rpc = 0
for f in fds.file:
    if f.name.startswith("google/"): continue
    for s in f.service:
        n_rpc += len(s.method)
print("сервисов:", sum(len(f.service) for f in fds.file if not f.name.startswith('google/')), "методов:", n_rpc)
