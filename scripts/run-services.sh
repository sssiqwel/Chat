#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
export CHAT_ROOT="$ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Нужен .NET SDK 8." >&2
  exit 1
fi

dotnet build Chat.sln -c Release --nologo

pids=""

cleanup() {
  if [ -z "$pids" ]; then
    return
  fi
  for pid in $pids; do
    kill -TERM "$pid" 2>/dev/null || true
  done
  i=0
  while [ "$i" -lt 25 ]; do
    alive=0
    for pid in $pids; do
      if kill -0 "$pid" 2>/dev/null; then
        alive=1
      fi
    done
    if [ "$alive" -eq 0 ]; then
      break
    fi
    i=$((i + 1))
    sleep 0.2
  done
  for pid in $pids; do
    kill -KILL "$pid" 2>/dev/null || true
  done
  for pid in $pids; do
    wait "$pid" 2>/dev/null || true
  done
  pids=""
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

start() {
  name="$1"
  port="$2"
  dll="$3"
  echo "Старт ${name} на 127.0.0.1:${port}"
  ASPNETCORE_URLS="http://127.0.0.1:${port}" ASPNETCORE_ENVIRONMENT=Production dotnet exec "$dll" &
  pids="$pids $!"
}

start Account 5091 "$ROOT/src/Account/bin/Release/net8.0/Account.dll"
start Room 5092 "$ROOT/src/Room/bin/Release/net8.0/Room.dll"
start Message 5093 "$ROOT/src/Message/bin/Release/net8.0/Message.dll"
start Gateway 5090 "$ROOT/src/Gateway/bin/Release/net8.0/Gateway.dll"

wait_health() {
  url="$1"
  i=0
  while [ "$i" -lt 50 ]; do
    if curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi
    i=$((i + 1))
    sleep 0.2
  done
  echo "Не дождались $url" >&2
  return 1
}

wait_health "http://127.0.0.1:5091/health"
wait_health "http://127.0.0.1:5092/health"
wait_health "http://127.0.0.1:5093/health"
wait_health "http://127.0.0.1:5090/health"

echo "Страница: http://127.0.0.1:5090"
wait
