# -*- coding: utf-8 -*-
"""Извлечение модели протокола из build/nexo.desc (дескриптор с комментариями)."""
from google.protobuf import descriptor_pb2 as dp
import os

F = dp.FieldDescriptorProto
SCALAR = {F.TYPE_DOUBLE: "double", F.TYPE_FLOAT: "float", F.TYPE_INT64: "int64", F.TYPE_UINT64: "uint64",
          F.TYPE_INT32: "int32", F.TYPE_BOOL: "bool", F.TYPE_STRING: "string", F.TYPE_BYTES: "bytes",
          F.TYPE_UINT32: "uint32", F.TYPE_SINT32: "sint32", F.TYPE_SINT64: "sint64", F.TYPE_FIXED32: "fixed32", F.TYPE_FIXED64: "fixed64"}
OWN = ("common.proto", "moderation.proto", "presence.proto", "users.proto", "admin.proto", "auth.proto", "calls.proto", "crypto.proto",
       "messages.proto", "chats.proto", "devices.proto", "keys.proto", "legal.proto", "media.proto", "settings.proto", "sync.proto")


def _comment(loc):
    t = (loc.leading_comments or "").strip()
    tr = (loc.trailing_comments or "").strip()
    return " ".join((t + " " + tr).split()) if (t or tr) else ""


def load(path=None):
    path = path or os.path.join(os.path.dirname(__file__), "..", "build", "nexo.desc")
    fds = dp.FileDescriptorSet(); fds.ParseFromString(open(path, "rb").read())
    model = dict(files=[], services=[], messages=[], enums=[])
    for f in fds.file:
        if f.name not in OWN: continue
        loc = {tuple(l.path): l for l in f.source_code_info.location}
        header = (loc.get((12,)).leading_comments if loc.get((12,)) else "") or ""
        model["files"].append(dict(name=f.name, header=header.strip(), pkg=f.package,
                                   services=[s.name for s in f.service], nmsg=len(f.message_type), nenum=len(f.enum_type)))

        def tname(fd, mp):
            if fd.type in SCALAR: return SCALAR[fd.type]
            n = fd.type_name.split(".")[-1]
            if fd.type_name in mp: return mp[fd.type_name]
            return n

        def walk_msg(m, path, prefix, fname):
            if m.options.map_entry: return
            mp = {}
            for n in m.nested_type:
                if n.options.map_entry:
                    k, v = n.field[0], n.field[1]
                    mp["." + f.package + "." + prefix + m.name + "." + n.name] = f"map<{tname(k, {})}, {tname(v, {})}>"
            fields = []
            oneof_names = [o.name for o in m.oneof_decl]
            for k, fd in enumerate(m.field):
                c = _comment(loc[tuple(path + [2, k])]) if tuple(path + [2, k]) in loc else ""
                ty = tname(fd, mp)
                rep = fd.label == F.LABEL_REPEATED and not ty.startswith("map<")
                fields.append(dict(name=fd.name, num=fd.number, type=("repeated " if rep else "") + ty, comment=c,
                                   oneof=oneof_names[fd.oneof_index] if fd.HasField("oneof_index") else ""))
            mc = _comment(loc[tuple(path)]) if tuple(path) in loc else ""
            model["messages"].append(dict(name=prefix + m.name, file=f.name, comment=mc, fields=fields))
            for j, n in enumerate(m.nested_type):
                walk_msg(n, path + [3, j], prefix + m.name + ".", fname)
            for j, e in enumerate(m.enum_type):
                walk_enum(e, path + [4, j], prefix + m.name + ".")

        def walk_enum(e, path, prefix):
            ec = _comment(loc[tuple(path)]) if tuple(path) in loc else ""
            vals = []
            for k, v in enumerate(e.value):
                c = _comment(loc[tuple(path + [2, k])]) if tuple(path + [2, k]) in loc else ""
                vals.append(dict(name=v.name, num=v.number, comment=c))
            model["enums"].append(dict(name=prefix + e.name, file=f.name, comment=ec, values=vals))

        for i, m in enumerate(f.message_type): walk_msg(m, [4, i], "", f.name)
        for i, e in enumerate(f.enum_type): walk_enum(e, [5, i], "")
        for i, s in enumerate(f.service):
            sc = _comment(loc[(6, i)]) if (6, i) in loc else ""
            meths = []
            for k, m in enumerate(s.method):
                c = _comment(loc[(6, i, 2, k)]) if (6, i, 2, k) in loc else ""
                meths.append(dict(name=m.name, req=m.input_type.split(".")[-1], resp=m.output_type.split(".")[-1],
                                  stream=m.server_streaming, cstream=m.client_streaming, comment=c))
            model["services"].append(dict(name=s.name, file=f.name, comment=sc, methods=meths))
    return model

if __name__ == "__main__":
    m = load()
    print(len(m["services"]), sum(len(s["methods"]) for s in m["services"]), len(m["messages"]), sum(len(x["fields"]) for x in m["messages"]), len(m["enums"]))
    print(m["services"][0]["name"], m["services"][0]["methods"][0])
    print(sum(1 for s in m["services"] for x in s["methods"] if not x["comment"]), "методов без комментария")
    print(sum(1 for x in m["messages"] for f in x["fields"] if not f["comment"]), "полей без комментария из", sum(len(x["fields"]) for x in m["messages"]))
