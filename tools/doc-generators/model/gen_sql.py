# -*- coding: utf-8 -*-
import sys, datetime
sys.path.insert(0, "model")
from schema_model import *

def sqltype(t):
    return t[5:] if t.startswith("enum:") else t

def q(s): return s.replace("'", "''")

def gen():
    out = []
    w = out.append
    w("-- =====================================================================")
    w("-- NEXO: схема базы данных PostgreSQL 16")
    w("-- Документ-источник: RU.00000000.00001-01 13 02 (Описание модели данных)")
    w("-- Файл сгенерирован из model/schema_model.py. Вручную не редактировать.")
    w("-- =====================================================================")
    w("")
    w("CREATE EXTENSION IF NOT EXISTS citext;")
    w("CREATE SCHEMA IF NOT EXISTS nexo;")
    w("SET search_path = nexo, public;")
    w("")
    w("-- ---------- Перечисляемые типы ----------")
    for name, vals in ENUMS.items():
        w(f"CREATE TYPE {name} AS ENUM (" + ", ".join(f"'{v}'" for v, _ in vals) + ");")
    w("")
    w("-- ---------- Таблицы ----------")
    for t in TABLES:
        w(f"-- {DOMAINS[t['domain']]}")
        w(f"CREATE TABLE {t['name']} (")
        lines = []
        for c in t["cols"]:
            typ = sqltype(c["type"])
            ln = f"    {c['name']} {typ}"
            d = c["default"]
            if d and d.startswith("GENERATED"):
                ln += " " + d
            if "nn" in c["flags"] or "pk" in c["flags"]:
                ln += " NOT NULL"
            if d and not d.startswith("GENERATED"):
                ln += f" DEFAULT {d}"
            if "uq" in c["flags"]:
                ln += " UNIQUE"
            if c["check"]:
                ln += f" CHECK ({c['check']})"
            lines.append(ln)
        if t["pk"]:
            lines.append("    PRIMARY KEY (" + ", ".join(t["pk"]) + ")")
        for uname, ucols in t["uniques"]:
            lines.append(f"    CONSTRAINT {uname} UNIQUE (" + ", ".join(ucols) + ")")
        for i, chk in enumerate(t["checks"], 1):
            lines.append(f"    CONSTRAINT ck_{t['name']}_{i} CHECK ({chk})")
        w(",\n".join(lines))
        w(");")
        w("")
    w("-- ---------- Внешние ключи ----------")
    for t in TABLES:
        pk = set(t["pk"] or [])
        for c in t["cols"]:
            if c["fk"]:
                if c["name"] in pk:
                    act = "ON DELETE CASCADE"
                elif "nn" not in c["flags"]:
                    act = "ON DELETE SET NULL"
                else:
                    act = "ON DELETE NO ACTION"
                w(f"ALTER TABLE {t['name']} ADD CONSTRAINT fk_{t['name']}_{c['name']} "
                  f"FOREIGN KEY ({c['name']}) REFERENCES {c['fk']} (id) {act};".replace(
                      "REFERENCES chat_folders (id)", "REFERENCES chat_folders (id)"))
    w("")
    w("-- ---------- Индексы ----------")
    for t in TABLES:
        for name, cols, uniq, where in t["indexes"]:
            u = "UNIQUE " if uniq else ""
            using = " USING gin" if name == "ix_messages_fts" else ""
            cs = ", ".join(cols)
            col_sql = f"({cs})" if not using else f"(({cs}))"
            w(f"CREATE {u}INDEX {name} ON {t['name']}{using} " + (col_sql if using else f"({cs})")
              + (f" WHERE {where}" if where else "") + ";")
    # внешние ключи на колонки, где parent PK не id (user_settings и т.п.) отсутствуют
    w("")
    w("-- ---------- Комментарии ----------")
    for t in TABLES:
        w(f"COMMENT ON TABLE {t['name']} IS '{q(t['doc'])}';")
        for c in t["cols"]:
            w(f"COMMENT ON COLUMN {t['name']}.{c['name']} IS '{q(c['doc'])}';")
    w("")
    w("-- Примечание по масштабированию: таблицы messages и user_updates при росте нагрузки")
    w("-- переводятся на секционирование по HASH(chat_id) и HASH(user_id) соответственно")
    w("-- (см. пояснительную записку, раздел «Масштабирование»).")
    return "\n".join(out) + "\n"

if __name__ == "__main__":
    s = gen()
    open("build/schema.sql", "w", encoding="utf-8").write(s)
    print(len(s.splitlines()), "строк")
