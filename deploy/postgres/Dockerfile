# PostgreSQL 16 + WAL-G + age + rclone: один образ для БД и для сервиса резервного копирования.
FROM postgres:16-bookworm

ARG WALG_VERSION=v3.0.7
ARG WALG_SHA256=da3fe6c6b71a639e73b651ba2f8618fa2c64d2471ae758e933f65b0d2329e533

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl age rclone \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL -o /usr/local/bin/wal-g \
      "https://github.com/wal-g/wal-g/releases/download/${WALG_VERSION}/wal-g-pg-ubuntu-22.04-amd64" \
 && echo "${WALG_SHA256}  /usr/local/bin/wal-g" | sha256sum -c - \
 && chmod 0755 /usr/local/bin/wal-g

COPY --chmod=0755 backup/*.sh /scripts/
