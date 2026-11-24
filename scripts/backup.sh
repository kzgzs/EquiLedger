# 玄铁栈道 EquiLedger · 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
# 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
#!/bin/sh
# EquiLedger 自动加密备份：pg_dump 全库 + 依据文件 → AES-256-CBC(PBKDF2) 加密单文件
# 环境变量：EQUI_BACKUP_PASSPHRASE 必填（拒绝明文落盘）
#           EQUI_BACKUP_INTERVAL_MIN 周期分钟，默认 1440（每日）
#           EQUI_BACKUP_KEEP 保留份数，默认 30
# 用法：sh /backup.sh        循环定时备份
#       sh /backup.sh once   立即备份一次
set -eu
OUT=/backup
INTERVAL="${EQUI_BACKUP_INTERVAL_MIN:-1440}"
KEEP="${EQUI_BACKUP_KEEP:-30}"
if [ -z "${EQUI_BACKUP_PASSPHRASE:-}" ]; then
  echo "[backup] 未设置 EQUI_BACKUP_PASSPHRASE，拒绝写出明文备份，退出" >&2
  exit 1
fi
mkdir -p "$OUT"

do_backup() {
  ts=$(date +%Y%m%d-%H%M%S)
  work=$(mktemp -d)
  pg_dump -h db -U equi -Fc -f "$work/equiledger.dump" equiledger
  stage=$(mktemp -d)
  cp "$work/equiledger.dump" "$stage/"
  if [ -d /evidence ]; then cp -r /evidence "$stage/evidence"; fi
  tar -cf "$work/payload.tar" -C "$stage" .
  rm -rf "$stage"
  openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt \
    -pass env:EQUI_BACKUP_PASSPHRASE \
    -in "$work/payload.tar" -out "$OUT/equi-backup-$ts.tar.enc"
  sha=$(openssl dgst -sha256 "$OUT/equi-backup-$ts.tar.enc" | awk '{print $NF}')
  printf '{"time":"%s","file":"equi-backup-%s.tar.enc","sha256":"%s"}\n' \
    "$(date -Iseconds)" "$ts" "$sha" > "$OUT/latest.json"
  rm -rf "$work"
  ls -1t "$OUT"/equi-backup-*.tar.enc 2>/dev/null | tail -n +"$((KEEP + 1))" | xargs -r rm -f
  echo "[backup] 完成 equi-backup-$ts.tar.enc (sha256=$sha)"
}

if [ "${1:-loop}" = "once" ]; then
  do_backup
  exit 0
fi
echo "[backup] 定时加密备份已启动：每 ${INTERVAL} 分钟一次，保留 ${KEEP} 份，目标 $OUT"
while true; do
  do_backup || echo "[backup] 本轮失败，下轮重试" >&2
  sleep "$((INTERVAL * 60))"
done