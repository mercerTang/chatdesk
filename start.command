#!/usr/bin/env bash
# 一键启动：后端(5180) + 访客前端(5173) + 客服前端(5174)
# 用法：./start.command       启动并在浏览器打开访客页与客服页
#       ./start.command stop  停止本次启动的进程
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
WEB="$ROOT/web"
DOTNET_BIN="${DOTNET_BIN:-${HOME}/Desktop/开发/dotnet-sdk/dotnet}"
BACKEND_PORT="${BACKEND_PORT:-5180}"
VISITOR_PORT="${VISITOR_PORT:-5173}"
AGENT_PORT="${AGENT_PORT:-5174}"
STATE_DIR="${TMPDIR:-/tmp}/customer-chat-dev"
PID_FILE="$STATE_DIR/pids"
mkdir -p "$STATE_DIR"

log() { printf '%s\n' "$*"; }
fail() { printf '错误：%s\n' "$*" >&2; exit 1; }

port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

wait_http() {
  local url=$1 timeout=${2:-60} waited=0
  while (( waited < timeout )); do
    if curl -fsS --max-time 2 "$url" >/dev/null 2>&1; then return 0; fi
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

open_url() { open "$1" 2>/dev/null || xdg-open "$1" 2>/dev/null || true; }

kill_tree() {
  local pid=$1 child
  for child in $(pgrep -P "$pid" 2>/dev/null); do kill_tree "$child"; done
  kill "$pid" 2>/dev/null || true
}

stop_all() {
  if [[ -f "$PID_FILE" ]]; then
    while read -r pid; do
      [[ -n "$pid" ]] || continue
      kill_tree "$pid"
    done < "$PID_FILE"
    rm -f "$PID_FILE"
  fi
}

is_running() {
  [[ -f "$PID_FILE" ]] || return 1
  local alive=0 pid
  while read -r pid; do
    [[ -n "$pid" ]] || continue
    if kill -0 "$pid" 2>/dev/null; then alive=1; fi
  done < "$PID_FILE"
  (( alive == 1 ))
}

if [[ "${1:-start}" == "stop" ]]; then
  stop_all
  log "已停止前后端"
  exit 0
fi

if is_running; then
  log "服务已在运行，直接打开页面"
  open_url "http://localhost:$VISITOR_PORT/login"
  open_url "http://localhost:$AGENT_PORT/login"
  exit 0
fi

for port in "$BACKEND_PORT" "$VISITOR_PORT" "$AGENT_PORT"; do
  if port_busy "$port"; then
    fail "端口 $port 已被占用。请先释放，或执行 ./start.command stop"
  fi
done

[[ -x "$DOTNET_BIN" ]] || fail "找不到 dotnet：$DOTNET_BIN（可用 DOTNET_BIN=/path/to/dotnet 指定）"
if [[ ! -d "$WEB/node_modules" ]]; then
  log "首次运行，安装前端依赖…"
  (cd "$WEB" && npm install --no-audit --no-fund) || fail "npm install 失败"
fi

: > "$PID_FILE"
cleanup() { stop_all; log "已停止前后端"; }
trap 'cleanup; exit 0' INT TERM

log "启动后端（${BACKEND_PORT}）…"
"$DOTNET_BIN" run --project "$ROOT/ChatServer" --urls "http://localhost:$BACKEND_PORT" \
  >"$STATE_DIR/backend.log" 2>&1 &
echo $! >> "$PID_FILE"
wait_http "http://localhost:$BACKEND_PORT/health" 90 || {
  cleanup
  fail "后端启动超时，日志：$STATE_DIR/backend.log"
}
log "后端已就绪"

log "启动访客前端（${VISITOR_PORT}）…"
(cd "$WEB" && npm run dev -- --port "$VISITOR_PORT" --strictPort) \
  >"$STATE_DIR/visitor.log" 2>&1 &
echo $! >> "$PID_FILE"

log "启动客服前端（${AGENT_PORT}）…"
(cd "$WEB" && npm run dev -- --port "$AGENT_PORT" --strictPort) \
  >"$STATE_DIR/agent.log" 2>&1 &
echo $! >> "$PID_FILE"

wait_http "http://localhost:$VISITOR_PORT/" 60 || {
  cleanup
  fail "访客前端启动超时，日志：$STATE_DIR/visitor.log"
}
wait_http "http://localhost:$AGENT_PORT/" 60 || {
  cleanup
  fail "客服前端启动超时，日志：$STATE_DIR/agent.log"
}

open_url "http://localhost:$VISITOR_PORT/login"
open_url "http://localhost:$AGENT_PORT/login"

cat <<EOF
已启动：
  访客端   http://localhost:$VISITOR_PORT/login
  客服端   http://localhost:$AGENT_PORT/login
           （演示账号 agent1 / agent2 / agent3，密码 Agent@123456）
  后端     http://localhost:$BACKEND_PORT/health
日志：$STATE_DIR/{backend,visitor,agent}.log

按 Ctrl+C 停止；或在另一个终端执行 ./start.command stop
EOF

wait
