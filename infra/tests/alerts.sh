#!/usr/bin/env bash
# Prueba de infra/scripts/alert.sh contra un receptor HTTPS local (python3, certificado autofirmado de un día):
# sin destino solo registra; con destino envía el texto fijo (sin contenido financiero) con la cabecera del token;
# no repite dentro de ALERT_REPEAT_HOURS; rechaza http:// y nombres de unidad no válidos. No toca /var/log ni secretos.
#   infra/tests/alerts.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
if [[ -f .env ]] && grep -qE '^ALERT_(WEBHOOK_URL|EMAIL)=.+' .env; then echo "Esta prueba usa su propio destino: .env ya define ALERT_*" >&2; exit 1; fi
tmp="$(mktemp -d)"; pid=""
cleanup() { if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi; rm -rf "$tmp"; }
trap cleanup EXIT
fail() { echo "FALLA: $*" >&2; exit 1; }
ok() { echo "OK    $*"; }
port=$(( 20000 + RANDOM % 20000 ))
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" \
  -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
cat > "$tmp/server.py" <<'PY'
import http.server, ssl, sys
d, port = sys.argv[1], int(sys.argv[2])
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
        with open(d + "/received.txt", "a") as f:
            f.write(f"AUTH={self.headers.get('Authorization')}\nBODY={body}\n")
        self.send_response(200); self.end_headers()
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", port), H)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(d + "/cert.pem", d + "/key.pem")
s.socket = ctx.wrap_socket(s.socket, server_side=True)
s.serve_forever()
PY
python3 -I "$tmp/server.py" "$tmp" "$port" & pid=$!
for _ in $(seq 1 20); do curl -s -o /dev/null --cacert "$tmp/cert.pem" -X POST "https://localhost:$port/" 2>/dev/null && break; sleep 0.2; done
rm -f "$tmp/received.txt"
export ALERT_LOG_DIR="$tmp/log" ALERT_STATE_DIR="$tmp/state" NO_PROXY="localhost,127.0.0.1,${NO_PROXY:-}"
alert=infra/scripts/alert.sh

"$alert" alvarez-backup.service >/dev/null || fail "sin destino debería terminar bien"
grep -q 'alvarez-backup.service FALLA' "$tmp/log/alerts.log" || fail "no registró la falla"
[[ ! -e "$tmp/received.txt" ]] || fail "envió algo sin destino configurado"
ok "sin destino: solo queda registrado"

export ALERT_WEBHOOK_URL="https://localhost:$port/alvarez" ALERT_WEBHOOK_CA_FILE="$tmp/cert.pem"
export ALERT_WEBHOOK_HEADERS_FILE="$tmp/headers"
printf 'Authorization: Bearer token-de-prueba\n' > "$ALERT_WEBHOOK_HEADERS_FILE"
"$alert" alvarez-backup.service >/dev/null || fail "no se pudo enviar"
grep -qx 'AUTH=Bearer token-de-prueba' "$tmp/received.txt" || fail "no llegó la cabecera del token"
grep -qx 'BODY=Álvarez Finanzas: falló alvarez-backup.service en .* Revisá en el servidor: journalctl -u alvarez-backup.service' "$tmp/received.txt" \
  || { cat "$tmp/received.txt"; fail "mensaje inesperado"; }
ok "con destino: texto fijo (unidad, servidor, hora) y token desde archivo"
"$alert" alvarez-backup.service >/dev/null
[[ "$(grep -c '^BODY=' "$tmp/received.txt")" == 1 ]] || fail "repitió el aviso dentro de ALERT_REPEAT_HOURS"
ALERT_REPEAT_HOURS=0 "$alert" alvarez-backup.service >/dev/null
[[ "$(grep -c '^BODY=' "$tmp/received.txt")" == 2 ]] || fail "no volvió a avisar al vencer ALERT_REPEAT_HOURS"
ok "no repite el aviso de la misma unidad antes de ALERT_REPEAT_HOURS"
if ALERT_WEBHOOK_URL="http://localhost:$port/x" "$alert" alvarez-healthcheck.service >/dev/null 2>&1; then fail "aceptó un destino http://"; fi
[[ "$(grep -c '^BODY=' "$tmp/received.txt")" == 2 ]] || fail "envió por http://"
ok "rechaza destinos sin TLS (y la unidad queda fallida)"
if "$alert" 'x;id' >/dev/null 2>&1; then fail "aceptó un nombre de unidad no válido"; fi
ok "rechaza nombres de unidad no válidos"
echo "Alertas OK"
