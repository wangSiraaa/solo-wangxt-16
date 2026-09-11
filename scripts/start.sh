#!/usr/bin/env bash
# 一键启动：PostgreSQL（用户态）+ AdRecon API（托管 Angular 产物）
# 用法：bash scripts/start.sh
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export LD_LIBRARY_PATH="/workspace/.pg/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# 1. PostgreSQL
if ! python3 -c "import socket; socket.create_connection(('127.0.0.1', 5432), 1)" 2>/dev/null; then
  echo "→ 启动 PostgreSQL (127.0.0.1:5432)"
  /workspace/.pg/bin/pg_ctl -D /workspace/.pgdata -l /workspace/.pg.log \
    -o "-p 5432 -k /tmp -c listen_addresses=127.0.0.1" start
  sleep 1
else
  echo "→ PostgreSQL 已在运行"
fi

# 2. 前端产物（若未构建过则构建一次）
if [ ! -f src/AdRecon.Api/wwwroot/index.html ]; then
  echo "→ 构建 Angular 前端"
  (cd web && npm install --no-audit --no-fund && ./node_modules/.bin/ng build)
  mkdir -p src/AdRecon.Api/wwwroot
  cp -r web/dist/adrecon-web/browser/* src/AdRecon.Api/wwwroot/
fi

# 3. API（自动建表；首次启动后 POST /api/seed/demo 灌演示数据）
echo "→ 启动 API  http://127.0.0.1:5080"
cd src/AdRecon.Api
dotnet run --urls http://127.0.0.1:5080
