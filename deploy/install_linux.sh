#!/usr/bin/env bash
# Runs as root on the Linux host (e.g. a Raspberry Pi): install_linux.sh <service-user> <port>. Expects the published
# EchoRelay.Headless binary and echorelay-headless.service next to this script; keeps /opt/echorelay/storage.
set -euo pipefail
SVC_USER=$1; PORT=$2; SRC=$(dirname "$0"); APP=/opt/echorelay
install -d -o "$SVC_USER" "$APP" "$APP/bin" "$APP/storage"
systemctl stop echorelay 2>/dev/null || true
install -m 755 -o "$SVC_USER" "$SRC/EchoRelay.Headless" "$APP/bin/EchoRelay.Headless"
sed -e "s#__USER__#$SVC_USER#g" -e "s#__PORT__#$PORT#g" "$SRC/echorelay-headless.service" > /etc/systemd/system/echorelay.service
systemctl daemon-reload; systemctl enable echorelay; systemctl restart echorelay
if command -v ufw >/dev/null && ufw status | grep -q 'Status: active'; then ufw allow "$PORT/tcp"; fi
sleep 4
echo "== status: $(systemctl is-active echorelay)"; journalctl -u echorelay -n 15 --no-pager -o cat
ss -ltnp | grep -E ":$PORT " || echo "nothing listening on :$PORT"
