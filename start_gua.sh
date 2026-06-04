#!/usr/bin/env bash
# Launch GUA_Blazor with llama-server (Gemma 4 E4B GGUF) on this machine.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Config (override via env) ──────────────────────────────────────────────
MODEL_PATH="${GUA_MODEL_PATH:-/opt/models/gemma4-uncensored/Q6_K_P.gguf}"
MMPROJ_PATH="${GUA_MMPROJ_PATH:-}"          # leave empty = text-only
LLAMA_PORT="${LLAMA_PORT:-8082}"
APP_PORT="${APP_PORT:-5168}"
CTX="${GUA_CTX:-8192}"
GUA_MODEL_NAME="${GUA_MODEL:-gemma-4-e4b-it}"
LOG_DIR="/tmp"

echo "=== GUA Blazor Launcher ==="
echo "  Model GGUF:  $MODEL_PATH"
echo "  llama port:  $LLAMA_PORT  (ctx=$CTX)"
echo "  App port:    $APP_PORT"
echo "  Supports images: ${GUA_SUPPORTS_IMAGES:-false}"
echo ""

# ── 1. Kill existing instances ─────────────────────────────────────────────
pkill -9 -f "GUA_Blazor" 2>/dev/null || true
pkill -9 -f "dotnet.*run" 2>/dev/null || true
lsof -ti:$APP_PORT | xargs kill -9 2>/dev/null || true
pkill -9 -f "llama-server" 2>/dev/null || true
lsof -ti:$LLAMA_PORT | xargs kill -9 2>/dev/null || true
sleep 2

# ── 2. Start llama-server ──────────────────────────────────────────────────
echo "Starting llama-server on port $LLAMA_PORT..."

LLAMA_CMD=(
    llama-server
    -m "$MODEL_PATH"
    --port "$LLAMA_PORT"
    -c "$CTX"
    --host 127.0.0.1
)

if [[ -n "$MMPROJ_PATH" && -f "$MMPROJ_PATH" ]]; then
    LLAMA_CMD+=(--mmproj "$MMPROJ_PATH")
    echo "  mmproj: $MMPROJ_PATH"
fi

nohup "${LLAMA_CMD[@]}" > "$LOG_DIR/llama_server.log" 2>&1 &
LLAMA_PID=$!
echo "  PID $LLAMA_PID → $LOG_DIR/llama_server.log"

# Wait for server to become healthy
echo -n "  Waiting"
for i in $(seq 1 60); do
    if curl -sf "http://127.0.0.1:$LLAMA_PORT/health" >/dev/null 2>&1; then
        echo " ready"
        break
    fi
    echo -n "."
    sleep 2
done

# ── 3. Start GUA_Blazor ────────────────────────────────────────────────────
echo "Starting GUA_Blazor on port $APP_PORT..."
> "$LOG_DIR/gua_blazor.log"

nohup env \
    GUA_API_ENDPOINT="http://localhost:$LLAMA_PORT/v1/" \
    GUA_MODEL="$GUA_MODEL_NAME" \
    GUA_SUPPORTS_IMAGES="${GUA_SUPPORTS_IMAGES:-false}" \
    GUA_MAX_TOKENS="${GUA_MAX_TOKENS:-4096}" \
    GUA_HEADLESS="${GUA_HEADLESS:-true}" \
    dotnet run --project "$SCRIPT_DIR" --urls "http://0.0.0.0:$APP_PORT" \
    > "$LOG_DIR/gua_blazor.log" 2>&1 &
APP_PID=$!
echo "  PID $APP_PID → $LOG_DIR/gua_blazor.log"

# Wait for app to be up
echo -n "  Waiting"
for i in $(seq 1 40); do
    HTTP=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$APP_PORT/" 2>/dev/null)
    if [[ "$HTTP" == "200" ]]; then
        echo " ready (HTTP $HTTP)"
        break
    fi
    echo -n "."
    sleep 3
done

echo ""
echo "GUA is running:"
echo "  UI   → http://localhost:$APP_PORT/"
echo "  API  → http://localhost:$APP_PORT/api/agent"
echo ""
echo "Logs:"
echo "  tail -f $LOG_DIR/llama_server.log"
echo "  tail -f $LOG_DIR/gua_blazor.log"
