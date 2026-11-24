#!/usr/bin/env bash
# E2E webhook matrix (e2e skill, Phase 5W) against the running sample instances.
#
# Every request is built the way the real provider builds it: its Content-Type, its own headers and nothing
# else (no X-Webhook-Key / X-Webhook-Timestamp unless that IS the provider's protocol), signed with the
# provider's documented algorithm. Requires: curl, jq, python (3.x). Runs against an AppHost that is already
# up; the AppHost sets Webhooks__EnforcementMode=Enforce, so rejections are real 4xx answers.
#
# Usage:  bash e2e-webhooks.sh [port ...]        (default: 5101 5102 5103 5104)
export LC_ALL=C.UTF-8
A=(-u admin:admin)
PY=${PYTHON:-python}
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
PORTS=("$@"); [ ${#PORTS[@]} -eq 0 ] && PORTS=(5101 5102 5103 5104)

FLOW_GITHUB=d1f8a000-0000-0000-0000-000000000201
FLOW_SHARED=d1f8a000-0000-0000-0000-000000000207

# ── Signing helpers (independent of the library; follow each provider's docs) ──────────────────────
sign() { "$PY" -I - "$@" <<'PY'
import sys, hmac, hashlib, base64
kind, key, *rest = sys.argv[1:]
def body(): return open(rest[-1], 'rb').read()
if kind == 'github':      print('sha256=' + hmac.new(key.encode(), body(), hashlib.sha256).hexdigest())
elif kind == 'stripe':    ts = rest[0]; print(hmac.new(key.encode(), ts.encode() + b'.' + body(), hashlib.sha256).hexdigest())
elif kind == 'slack':     ts = rest[0]; print('v0=' + hmac.new(key.encode(), b'v0:' + ts.encode() + b':' + body(), hashlib.sha256).hexdigest())
elif kind == 'teams':     print('HMAC ' + base64.b64encode(hmac.new(base64.b64decode(key), body(), hashlib.sha256).digest()).decode())
elif kind == 'stdwh':     # key = whsec_<base64>; signed content = {id}.{ts}.{body}
    msg_id, ts = rest[0], rest[1]
    k = base64.b64decode(key.split('_', 1)[1])
    print('v1,' + base64.b64encode(hmac.new(k, f'{msg_id}.{ts}.'.encode() + body(), hashlib.sha256).digest()).decode())
elif kind == 'twilio':    # url + sorted(name+value) over the form fields in the body file
    from urllib.parse import parse_qsl
    url = rest[0]; fields = sorted(parse_qsl(body().decode(), keep_blank_values=True))
    s = url + ''.join(k + v for k, v in fields)
    print(base64.b64encode(hmac.new(key.encode(), s.encode(), hashlib.sha1).digest()).decode())
PY
}
uuid() { "$PY" -I -c 'import uuid;print(uuid.uuid4())'; }
now()  { date +%s; }

PASS=0; TOTAL=0; FAILS=()
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); TOTAL=$((TOTAL+1)); }
bad()  { echo "  ✗ $1 — $2"; TOTAL=$((TOTAL+1)); FAILS+=("$PORT $1: $2"); }
skp()  { echo "  ⊘ $1 — $2"; PASS=$((PASS+1)); TOTAL=$((TOTAL+1)); }

# post <url> <content-type> <body-file> [extra curl args...] → prints "<status> <response body>"
post() {
  local url=$1 ct=$2 file=$3; shift 3
  curl -s -o "$TMP/resp" -w "%{http_code}" -X POST "$url" -H "Content-Type: $ct" -H 'Accept:' "$@" --data-binary @"$file" > "$TMP/code"
  echo "$(cat "$TMP/code") $(tr -d '\n' < "$TMP/resp")"
}
runs_count() { curl -fsS "${A[@]}" "$B/flows/api/runs?flowId=$1&take=500" | jq 'length'; }
run()        { curl -fsS "${A[@]}" "$B/flows/api/runs/$1"; }
wait_run()   { local s; for _ in $(seq 1 60); do s=$(run "$1" | jq -r '.status'); case "$s" in Succeeded|Failed|Skipped|Cancelled|TimedOut) break;; esac; sleep 1; done; echo "$s"; }

check_instance() {
  PORT=$1; B="http://localhost:$PORT"; local name=$2
  echo "=== $name ($PORT) webhooks ==="
  local f r code body sig ts dlv run_id

  # W.1 GitHub — genuine delivery: body-only HMAC, no timestamp, no X-Webhook-Key. Also the existing
  #     WebhookEnterpriseSampleFlow (github-sample-flow, secret in BOTH webhookSecret and webhookHmacKey).
  f=$TMP/gh.json; printf '{"zen":"Design for failure.","hook_id":%s,"hook":{"type":"Repository"}}' "$RANDOM$RANDOM" > "$f"
  sig=$(sign github e2e-github-secret "$f"); dlv=$(uuid)
  gh_headers=(-H "User-Agent: GitHub-Hookshot/044aadd" -H "X-GitHub-Event: ping" -H "X-GitHub-Delivery: $dlv"
              -H "X-GitHub-Hook-ID: 292430182" -H "X-GitHub-Hook-Installation-Target-ID: 79929171"
              -H "X-GitHub-Hook-Installation-Target-Type: repository" -H "X-Hub-Signature-256: $sig")
  r=$(post "$B/flows/api/webhook/e2e-github" application/json "$f" "${gh_headers[@]}"); code=${r%% *}; body=${r#* }
  run_id=$(jq -r '.runId // empty' <<<"$body" 2>/dev/null); local gh_first=$code
  if [ "$code" = 200 ] && [ -n "$run_id" ] && [ "$(wait_run "$run_id")" = Succeeded ]; then
    local th; th=$(run "$run_id" | jq -r '.triggerHeaders["X-GitHub-Event"] // .triggerHeaders["x-github-event"] // empty')
    [ "$th" = ping ] && ok "W.1 GitHub genuine delivery → 200, run Succeeded, headers in run detail" \
                     || bad "W.1 GitHub" "run ok but X-GitHub-Event not in triggerHeaders"
  else bad "W.1 GitHub genuine delivery" "code=$code body=${body:0:160}"; fi

  # W.1b the shipped sample flow, sent exactly as GitHub would (this is the fixed 5.6).
  f=$TMP/ghs.json; printf '{"hello":"world","n":%s}' "$RANDOM$RANDOM" > "$f"
  r=$(post "$B/flows/api/webhook/github-sample-flow" application/json "$f" -H "User-Agent: GitHub-Hookshot/044aadd" \
        -H "X-GitHub-Event: ping" -H "X-GitHub-Delivery: $(uuid)" -H "X-Hub-Signature-256: $(sign github mySharedSecret "$f")")
  [ "${r%% *}" = 200 ] && ok "W.1b WebhookEnterpriseSampleFlow genuine GitHub delivery → 200" || bad "W.1b sample GitHub flow" "${r:0:200}"

  # W.2 GitHub — identical redelivery (same body, same signature) → 409, accepted once.
  r=$(post "$B/flows/api/webhook/e2e-github" application/json "$TMP/gh.json" "${gh_headers[@]}")
  [ "$gh_first" = 200 ] && [ "${r%% *}" = 409 ] && ok "W.2 GitHub identical redelivery → 409"     || bad "W.2 GitHub replay" "first=$gh_first second=${r:0:160}"

  # W.3 GitHub — wrong signature → 401 and listed as REJECTED in the webhook log.
  f=$TMP/gh-bad.json; printf '{"zen":"forged","n":%s}' "$RANDOM$RANDOM" > "$f"
  r=$(post "$B/flows/api/webhook/e2e-github" application/json "$f" -H "User-Agent: GitHub-Hookshot/044aadd" \
        -H "X-GitHub-Event: ping" -H "X-GitHub-Delivery: $(uuid)" -H "X-Hub-Signature-256: $(sign github not-the-secret "$f")")
  if [ "${r%% *}" = 401 ]; then
    local rej; rej=$(curl -fsS "${A[@]}" "$B/flows/api/webhooks/recent?flowId=$FLOW_GITHUB&rejectedOnly=true&take=20" \
                     | jq '[(if type == "array" then . else .items end)[] | select((.isAccepted|not) and (.statusCode==401))] | length')
    [ "${rej:-0}" -ge 1 ] && ok "W.3 GitHub wrong signature → 401 + REJECTED row in webhook log" \
                          || bad "W.3 GitHub wrong signature" "401 but no rejected row in /api/webhooks/recent"
  else bad "W.3 GitHub wrong signature" "${r:0:200}"; fi

  # W.4 Stripe — genuine (t= signed timestamp) → 200; exact same delivery again → 409.
  f=$TMP/stripe.json; printf '{\n  "id": "evt_%s",\n  "object": "event",\n  "type": "payment_intent.succeeded"\n}' "$RANDOM$RANDOM" > "$f"
  ts=$(now); sig=$(sign stripe whsec_e2eStripeSigningSecret000000000000 "$ts" "$f")
  st_headers=(-H "User-Agent: Stripe/1.0 (+https://stripe.com/docs/webhooks)" -H "Stripe-Signature: t=$ts,v1=$sig")
  r=$(post "$B/flows/api/webhook/e2e-stripe" "application/json; charset=utf-8" "$f" "${st_headers[@]}"); code=${r%% *}
  local r2; r2=$(post "$B/flows/api/webhook/e2e-stripe" "application/json; charset=utf-8" "$f" "${st_headers[@]}")
  [ "$code" = 200 ] && [ "${r2%% *}" = 409 ] && ok "W.4 Stripe genuine → 200, replay → 409" || bad "W.4 Stripe" "first=${r:0:120} second=${r2:0:120}"

  # W.5 Slack slash command — application/x-www-form-urlencoded, v0 signature.
  f=$TMP/slack.form; printf 'token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%%2Fwebhook-collect&text=e2e-%s&response_url=https%%3A%%2F%%2Fhooks.slack.com%%2Fcommands%%2FT1DC2JH3J%%2F397700885554%%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c' "$RANDOM" > "$f"
  ts=$(now)
  r=$(post "$B/flows/api/webhook/e2e-slack" application/x-www-form-urlencoded "$f" -H "User-Agent: Slackbot 1.0 (+https://api.slack.com/robots)" \
        -H "X-Slack-Request-Timestamp: $ts" -H "X-Slack-Signature: $(sign slack 8f742231b10e8888abcd99yyyzzz85a5 "$ts" "$f")")
  [ "${r%% *}" = 200 ] && ok "W.5 Slack slash command (form) → 200" || bad "W.5 Slack form" "${r:0:200}"

  # W.6 Twilio — form-urlencoded; signature over the URL the app is reached at + sorted fields.
  f=$TMP/twilio.form; printf 'CallSid=CA%s&Caller=%%2B14158675310&Digits=1234&From=%%2B14158675310&To=%%2B18005551212' "$RANDOM$RANDOM" > "$f"
  local turl="$B/flows/api/webhook/e2e-twilio?foo=1&bar=2"
  r=$(post "$turl" application/x-www-form-urlencoded "$f" -H "User-Agent: TwilioProxy/1.1" \
        -H "I-Twilio-Idempotency-Token: $(uuid)" -H "X-Twilio-Signature: $(sign twilio e2e-twilio-auth-token "$turl" "$f")")
  [ "${r%% *}" = 200 ] && ok "W.6 Twilio (form, HMAC-SHA1) → 200" || bad "W.6 Twilio" "${r:0:200}"

  # W.7 Microsoft Teams — Authorization: HMAC <base64>, key base64-decoded; Authorization must not reach run detail.
  f=$TMP/teams.json; printf '{"type":"message","id":"%s","timestamp":"2017-02-01T21:10:07.437Z","text":"<at>Sample</at> hello"}' "$RANDOM$RANDOM" > "$f"
  r=$(post "$B/flows/api/webhook/e2e-teams" "application/json; charset=utf-8" "$f" -H "User-Agent: Microsoft-SkypeBotApi (Microsoft-BotFramework/3.0)" \
        -H "Authorization: $(sign teams 6fMbcEmK8Zdzj5A9xXk1yW3m7Hn2pQ4rT0uVwY8zB1c= "$f")"); code=${r%% *}; body=${r#* }
  run_id=$(jq -r '.runId // empty' <<<"$body" 2>/dev/null)
  if [ "$code" = 200 ] && [ -n "$run_id" ]; then
    local auth; auth=$(run "$run_id" | jq -r '[.triggerHeaders // {} | keys[] | ascii_downcase | select(. == "authorization")] | length')
    [ "$auth" = 0 ] && ok "W.7 Teams outgoing webhook → 200, Authorization not persisted" || bad "W.7 Teams" "Authorization header persisted in run triggerHeaders"
  else bad "W.7 Teams outgoing webhook" "${r:0:200}"; fi

  # W.8 Standard Webhooks / Svix — genuine → 200, tampered signature → 401.
  f=$TMP/stdwh.json; printf '{"type":"contact.created","n":%s}' "$RANDOM$RANDOM" > "$f"
  local mid="msg_$RANDOM$RANDOM"; ts=$(now); sig=$(sign stdwh whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw "$mid" "$ts" "$f")
  r=$(post "$B/flows/api/webhook/e2e-standard-webhooks" application/json "$f" -H "User-Agent: Svix-Webhooks/1.0" \
        -H "webhook-id: $mid" -H "webhook-timestamp: $ts" -H "webhook-signature: $sig")
  f=$TMP/stdwh-bad.json; printf '{"type":"contact.created","forged":%s}' "$RANDOM$RANDOM" > "$f"
  r2=$(post "$B/flows/api/webhook/e2e-standard-webhooks" application/json "$f" -H "User-Agent: Svix-Webhooks/1.0" \
        -H "webhook-id: msg_forged$RANDOM" -H "webhook-timestamp: $(now)" -H "webhook-signature: v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")
  [ "${r%% *}" = 200 ] && [ "${r2%% *}" = 401 ] && ok "W.8 Standard Webhooks genuine → 200, forged → 401" || bad "W.8 Standard Webhooks" "genuine=${r:0:60} forged=${r2:0:100}"

  # W.9 Plain shared secret — X-Webhook-Key IS this protocol: right key → 200, wrong key → 401.
  f=$TMP/shared.json; printf '{"order":"%s"}' "$RANDOM$RANDOM" > "$f"
  r=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$f" -H "X-Webhook-Key: e2e-shared-secret")
  r2=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$f" -H "X-Webhook-Key: wrong")
  [ "${r%% *}" = 200 ] && [ "${r2%% *}" = 401 ] && ok "W.9 shared secret right → 200, wrong → 401" || bad "W.9 shared secret" "right=${r:0:80} wrong=${r2:0:80}"

  # W.10 Idempotency — same Idempotency-Key twice → same runId, second duplicate=true, one run.
  local idem="e2e-idem-$(uuid)" before after a b
  before=$(runs_count $FLOW_SHARED)
  f=$TMP/idem.json; printf '{"order":"idem-%s"}' "$RANDOM" > "$f"
  a=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$f" -H "X-Webhook-Key: e2e-shared-secret" -H "Idempotency-Key: $idem")
  b=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$f" -H "X-Webhook-Key: e2e-shared-secret" -H "Idempotency-Key: $idem")
  sleep 1; after=$(runs_count $FLOW_SHARED)
  if [ "${a%% *}" = 200 ] && [ "$(jq -r .runId <<<"${a#* }")" = "$(jq -r .runId <<<"${b#* }")" ] \
     && [ "$(jq -r .duplicate <<<"${b#* }")" = true ] && [ $((after - before)) -eq 1 ]; then ok "W.10 Idempotency-Key → one run, second duplicate=true"
  else bad "W.10 idempotency" "a=${a:0:90} b=${b:0:90} runs+$((after - before))"; fi

  # W.11 Body cap (1 MiB) — Content-Length and chunked both → 413.
  head -c 1200000 /dev/zero | tr '\0' ' ' > "$TMP/big.json"
  r=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$TMP/big.json" -H "X-Webhook-Key: e2e-shared-secret")
  r2=$(post "$B/flows/api/webhook/e2e-shared-secret" application/json "$TMP/big.json" -H "X-Webhook-Key: e2e-shared-secret" -H "Transfer-Encoding: chunked")
  [ "${r%% *}" = 413 ] && [ "${r2%% *}" = 413 ] && ok "W.11 body over cap → 413 (Content-Length + chunked)" || bad "W.11 body cap" "content-length=${r%% *} chunked=${r2%% *}"

  # W.12 Rate limit — 1 permit/s burst 2 on this trigger: 6 back-to-back deliveries must see a 429.
  local codes=() i
  for i in 1 2 3 4 5 6; do printf '{"n":%s}' "$i" > "$TMP/rl.json"; r=$(post "$B/flows/api/webhook/e2e-rate-limited" application/json "$TMP/rl.json"); codes+=("${r%% *}"); done
  [[ " ${codes[*]} " == *" 429 "* ]] && [ "${codes[0]}" = 200 ] && ok "W.12 rate limit → 429 (${codes[*]})" || bad "W.12 rate limit" "codes=${codes[*]}"

  # W.13 IP allow-list (198.51.100.0/24 only; host trusts 1 proxy hop). A direct call is refused, and a forged
  #      X-Forwarded-For prefix naming an allowed address must not open it: the trusted hop is the RIGHTMOST
  #      entry (what the proxy appended), here 203.0.113.9.
  printf '{"probe":1}' > "$TMP/ip.json"
  r=$(post "$B/flows/api/webhook/e2e-ip-allowlist" application/json "$TMP/ip.json")
  r2=$(post "$B/flows/api/webhook/e2e-ip-allowlist" application/json "$TMP/ip.json" -H "X-Forwarded-For: 198.51.100.7, 203.0.113.9")
  [ "${r%% *}" = 403 ] && [ "${r2%% *}" = 403 ] && ok "W.13 IP allow-list: direct → 403, forged XFF prefix → 403" || bad "W.13 IP allow-list" "direct=${r%% *} forgedXff=${r2%% *}"

  # W.14 Client disconnect mid-body — declare 100 000 bytes, send 1 000, drop the socket: no run, host healthy.
  before=$(runs_count $FLOW_SHARED)
  "$PY" -I - "$PORT" <<'PY'
import socket, sys
s = socket.create_connection(("localhost", int(sys.argv[1])), timeout=5)
s.sendall(b"POST /flows/api/webhook/e2e-shared-secret HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n"
          b"X-Webhook-Key: e2e-shared-secret\r\nContent-Length: 100000\r\n\r\n" + b'{"a":"' + b"x" * 994)
s.close()
PY
  sleep 2; after=$(runs_count $FLOW_SHARED); code=$(curl -s -o /dev/null -w "%{http_code}" "$B/health")
  [ "$after" = "$before" ] && [ "$code" = 200 ] && ok "W.14 client disconnect mid-body → no run, host healthy (check AppHost log for 'unhandled exception')" \
    || bad "W.14 client disconnect" "runs $before→$after health=$code"

  # W.15 Viewer masking (Keycloak instance only): a viewer sees the run's trigger headers with signature-bearing
  #      values masked; the operator (Basic admin) sees them in clear.
  if [ "$PORT" = 5102 ]; then viewer_check; else skp "W.15 viewer masking" "Keycloak viewer exists on 5102 only"; fi
  echo
}

viewer_check() {
  local jar=$TMP/kc.jar page action
  page=$(curl -s -c "$jar" -b "$jar" -L -H 'Accept: text/html' "$B/flows/")
  action=$(grep -o 'action="[^"]*"' <<<"$page" | head -1 | sed -e 's/^action="//' -e 's/"$//' -e 's/&amp;/\&/g')
  if [ -z "$action" ]; then skp "W.15 viewer masking" "could not reach the Keycloak login form"; return; fi
  curl -s -c "$jar" -b "$jar" -L -o /dev/null -d "username=viewer&password=viewer-demo-pass&credentialId=" "$action"
  local id masked clear
  id=$(curl -fsS "${A[@]}" "$B/flows/api/runs?flowId=$FLOW_GITHUB&take=1" | jq -r '.[0].id')
  masked=$(curl -s -b "$jar" "$B/flows/api/runs/$id" | jq -r '.triggerHeaders["X-Hub-Signature-256"] // .triggerHeaders["x-hub-signature-256"] // "absent"')
  clear=$(run "$id" | jq -r '.triggerHeaders["X-Hub-Signature-256"] // .triggerHeaders["x-hub-signature-256"] // "absent"')
  if [ "$masked" = "***redacted***" ] && [[ "$clear" == sha256=* ]]; then ok "W.15 viewer sees signature header masked, operator in clear"
  else bad "W.15 viewer masking" "viewer=$masked operator=${clear:0:20}"; fi
}

for p in "${PORTS[@]}"; do
  case $p in
    5101) n="flow-sqlserver  storage=sqlserver runtime=hangfire";;
    5102) n="flow-postgresql storage=postgresql runtime=hangfire";;
    5103) n="flow-inmemory   storage=inmemory runtime=inmemory";;
    5104) n="flow-servicebus storage=inmemory runtime=servicebus";;
    *)    n="instance";;
  esac
  check_instance "$p" "$n"
done
echo "RESULT: $PASS/$TOTAL passed"
for f in "${FAILS[@]}"; do echo "  FAIL $f"; done
