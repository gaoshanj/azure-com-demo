#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# COM 合同订单管理系统 · 一键部署
#
#   ./deploy.sh                          # 全量部署（基础设施 + 发布代码）
#   ./deploy.sh --infra-only             # 只部署基础设施
#   ./deploy.sh --publish-only           # 只发布代码（改代码后用）
#   ./deploy.sh --profile lite --no-pe   # 低成本 / 排障模式（公网访问）
#   ./deploy.sh --what-if                # 只预览变更，不落资源
# ─────────────────────────────────────────────────────────────
set -euo pipefail

SUBSCRIPTION=""
LOCATION="eastasia"
NAME="com-poc"
PROFILE="full"
ENABLE_PE="true"
INFRA_ONLY="false"
PUBLISH_ONLY="false"
WHAT_IF="false"
ALERT_EMAIL="unmarriedcn@hotmail.com"
# East Asia 的 Cosmos DB 经常因容量不足返回 ServiceUnavailable，
# 审计镜像对延迟不敏感，默认落到新加坡。置空则跟随 --location。
COSMOS_LOCATION="southeastasia"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --subscription) SUBSCRIPTION="$2"; shift 2 ;;
    --location)     LOCATION="$2"; shift 2 ;;
    --name)         NAME="$2"; shift 2 ;;
    --profile)      PROFILE="$2"; shift 2 ;;
    --email)        ALERT_EMAIL="$2"; shift 2 ;;
    --cosmos-location) COSMOS_LOCATION="$2"; shift 2 ;;
    --no-pe)        ENABLE_PE="false"; shift ;;
    --infra-only)   INFRA_ONLY="true"; shift ;;
    --publish-only) PUBLISH_ONLY="true"; shift ;;
    --what-if)      WHAT_IF="true"; shift ;;
    -h|--help)      sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "未知参数：$1"; exit 1 ;;
  esac
done

RG="rg-${NAME}-${LOCATION}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
OUT="$ROOT/.build"
SECRETS="$HERE/.secrets.env"
PY="${PYTHON_BIN:-python}"

# Git Bash 会自动把「独立参数」里的 /d/... 转成 D://...，但 python -c 脚本
# 字符串里的路径不会被转换，必须显式转。
to_win() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

c_ok()   { printf "\033[32m%s\033[0m\n" "$*"; }
c_info() { printf "\033[36m%s\033[0m\n" "$*"; }
c_warn() { printf "\033[33m%s\033[0m\n" "$*"; }
step()   { printf "\n\033[1m▶ %s\033[0m\n" "$*"; }

# ── 0. 前置检查 ──────────────────────────────────────────────
command -v az >/dev/null || { echo "未找到 Azure CLI"; exit 1; }
command -v dotnet >/dev/null || { echo "未找到 .NET SDK"; exit 1; }
az account show >/dev/null 2>&1 || { echo "请先执行 az login"; exit 1; }

if [[ -n "$SUBSCRIPTION" ]]; then
  az account set --subscription "$SUBSCRIPTION"
fi
SUB_ID=$(az account show --query id -o tsv)
SUB_NAME=$(az account show --query name -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

c_info "订阅：$SUB_NAME ($SUB_ID)"
c_info "区域：$LOCATION    资源组：$RG    规格：$PROFILE"

# bicep：优先用 azd 自带版本，避免 az bicep 下载失败
BICEP_BIN="$(command -v bicep || true)"
if [[ -z "$BICEP_BIN" && -x "$HOME/.azd/bin/bicep.exe" ]]; then BICEP_BIN="$HOME/.azd/bin/bicep.exe"; fi
if [[ -z "$BICEP_BIN" && -x "$HOME/.azd/bin/bicep" ]]; then BICEP_BIN="$HOME/.azd/bin/bicep"; fi

# 关键：不要交给 az 去隐式编译 bicep。
# az 会调用 $HOME/.azure/bin/bicep.exe，若该文件为 0 字节（下载被墙/中断的常见结果），
# 会报 [WinError 193] 而不是给出可读的错误。这里显式编译成 ARM JSON 再提交。
TEMPLATE_FILE="$HERE/main.bicep"
if [[ -n "$BICEP_BIN" ]]; then
  mkdir -p "$OUT"
  step "编译 Bicep → ARM JSON"
  if "$BICEP_BIN" build "$HERE/main.bicep" --outfile "$OUT/main.json" 2>"$OUT/bicep.log"; then
    TEMPLATE_FILE="$OUT/main.json"
    c_ok "编译通过（$(wc -c < "$OUT/main.json") 字节）"
  else
    c_warn "Bicep 编译失败，明细见 $OUT/bicep.log"
    tail -20 "$OUT/bicep.log"
    exit 1
  fi
else
  c_warn "未找到 bicep 可执行文件，交由 az 隐式编译（可能因内置 bicep 损坏而失败）"
fi

# ── 1. 资源组 ────────────────────────────────────────────────
step "创建/确认资源组"
az group create -n "$RG" -l "$LOCATION" --tags app=com env=poc owner=azure-training costCenter=training-poc -o none
c_ok "资源组就绪：$RG"

read_out() { az deployment group show -g "$RG" -n "$1" --query "properties.outputs.$2.value" -o tsv 2>/dev/null || true; }

publish_apps() {
  step "发布应用代码"
  local api_app web_app func_app
  api_app=$(read_out com-deploy apiApp)
  web_app=$(read_out com-deploy webApp)
  func_app=$(read_out com-deploy funcApp)
  [[ -z "$api_app" ]] && { echo "找不到部署输出，请先执行 --infra-only"; exit 1; }

  mkdir -p "$OUT"
  c_info "编译 API + 门户（同一发布包，靠 APP_ROLE 区分角色）"
  # 指定 -r linux-x64：不指定时会把 windows/macos 的原生库一起打进去，
  # 包体 175MB → 65MB。目标只有 App Service Linux，留着其它平台纯属浪费部署时间。
  dotnet publish "$ROOT/src/Com.Api" -c Release -r linux-x64 --self-contained false \
    -o "$OUT/api" --nologo -v q
  rm -f "$OUT/api.zip"
  $PY -c "import shutil;shutil.make_archive(r'$(to_win "$OUT/api")','zip',r'$(to_win "$OUT/api")')"

  c_info "部署到 $api_app"
  az webapp deploy -g "$RG" -n "$api_app" --src-path "$OUT/api.zip" --type zip -o none
  c_info "部署到 $web_app"
  az webapp deploy -g "$RG" -n "$web_app" --src-path "$OUT/api.zip" --type zip -o none

  if [[ -n "$func_app" ]]; then
    c_info "编译并部署 Worker → $func_app"
    dotnet publish "$ROOT/src/Com.Worker" -c Release -r linux-x64 --self-contained false \
      -o "$OUT/func" --nologo -v q
    rm -f "$OUT/func.zip"
    $PY -c "import shutil;shutil.make_archive(r'$(to_win "$OUT/func")','zip',r'$(to_win "$OUT/func")')"
    az functionapp deployment source config-zip -g "$RG" -n "$func_app" --src "$OUT/func.zip" -o none
  fi

  # 工作流触发器地址只有部署后才知道，单独回写（配置即代码 + 一次性补丁）
  local trigger
  trigger=$(read_out com-deploy logicAppTriggerUrl)
  if [[ -n "$trigger" ]]; then
    c_info "回写 Logic Apps 触发器地址到 API 配置"
    az webapp config appsettings set -g "$RG" -n "$api_app" --settings LOGICAPP_TRIGGER_URL="$trigger" -o none
  else
    c_warn "未取得工作流触发器地址，审批将走 API 内部推进（WORKFLOW_MODE=internal）"
  fi

  az webapp restart -g "$RG" -n "$api_app" -o none
  az webapp restart -g "$RG" -n "$web_app" -o none
  c_ok "代码发布完成"
}

if [[ "$PUBLISH_ONLY" == "true" ]]; then
  publish_apps
  echo
  c_ok "门户：https://$(read_out com-deploy webUrl | sed 's|https://||')"
  exit 0
fi

# ── 2. 生成/复用密钥 ─────────────────────────────────────────
step "准备密钥"
if [[ -f "$SECRETS" ]]; then
  # shellcheck disable=SC1090
  source "$SECRETS"
  c_info "复用已有密钥文件 $SECRETS"
else
  SQL_PASSWORD="Com$(openssl rand -base64 18 2>/dev/null | tr -dc 'A-Za-z0-9' | head -c 18)9a!"
  JWT_KEY=$(openssl rand -base64 48 2>/dev/null | tr -d '\n' || $PY -c "import secrets;print(secrets.token_urlsafe(48))")
  INTERNAL_KEY=$(openssl rand -hex 24 2>/dev/null || $PY -c "import secrets;print(secrets.token_hex(24))")
  cat > "$SECRETS" <<EOF
SQL_PASSWORD='$SQL_PASSWORD'
JWT_KEY='$JWT_KEY'
INTERNAL_KEY='$INTERNAL_KEY'
EOF
  chmod 600 "$SECRETS" 2>/dev/null || true
  c_ok "已生成新密钥并写入 $SECRETS（不要提交到版本库）"
fi

DEPLOYER_OID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)

# ── 3. 部署基础设施 ──────────────────────────────────────────
if [[ "$INFRA_ONLY" != "false" || "$PUBLISH_ONLY" == "false" ]]; then
  step "部署基础设施（约 6–12 分钟）"
  PARAMS_FILE="$OUT/params.json"
  mkdir -p "$OUT"
  $PY - "$PARAMS_FILE" "$LOCATION" "$NAME" "$PROFILE" "$ENABLE_PE" "$SQL_PASSWORD" "$JWT_KEY" "$INTERNAL_KEY" "$DEPLOYER_OID" "$ALERT_EMAIL" "$COSMOS_LOCATION" <<'PYEOF'
import json, sys
out, loc, name, profile, pe, sqlpwd, jwt, ikey, oid, email, cloc = sys.argv[1:12]
params = {
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "location": {"value": loc},
    "namePrefix": {"value": name},
    "profile": {"value": profile},
    "enablePrivateEndpoints": {"value": pe.lower() == "true"},
    "sqlAdminPassword": {"value": sqlpwd},
    "jwtKey": {"value": jwt},
    "internalKey": {"value": ikey},
    "deployerObjectId": {"value": oid or ""},
    "alertEmail": {"value": email},
    "cosmosLocation": {"value": cloc},
    "budgetAmount": {"value": 150 if profile == "full" else 60},
    "enableFunctions": {"value": True},
    "enableLogicApps": {"value": True},
    "enableCosmos": {"value": True}
  }
}
open(out, "w", encoding="utf-8").write(json.dumps(params, ensure_ascii=False, indent=2))
PYEOF

  if [[ "$WHAT_IF" == "true" ]]; then
    c_info "what-if 预览（不创建资源）"
    az deployment group what-if -g "$RG" -n com-deploy \
      --template-file "$TEMPLATE_FILE" --parameters "@$PARAMS_FILE" -o table | head -80
    exit 0
  fi

  az deployment group create -g "$RG" -n com-deploy \
    --template-file "$TEMPLATE_FILE" \
    --parameters "@$PARAMS_FILE" \
    --only-show-errors -o none
  c_ok "基础设施部署完成"
fi

if [[ "$INFRA_ONLY" == "true" ]]; then
  echo
  c_info "门户：$(read_out com-deploy webUrl)"
  c_info "API ：$(read_out com-deploy apiUrl)"
  exit 0
fi

# ── 4. 发布代码 ──────────────────────────────────────────────
publish_apps

# ── 5. 汇总 ──────────────────────────────────────────────────
echo
c_ok "──────────────────────────────────────────────"
echo "门户      ：$(read_out com-deploy webUrl)"
echo "API       ：$(read_out com-deploy apiUrl)"
echo "Function  ：$(read_out com-deploy funcApp)"
echo "Key Vault ：$(read_out com-deploy kvName)"
echo "SQL       ：$(read_out com-deploy sqlFqdn)"
echo
echo "演示账号（密码 Com@2026）："
echo "  sales@com.demo     销售 张伟"
echo "  director@com.demo  销售总监 李强"
echo "  finance@com.demo   财务 王敏"
echo "  gm@com.demo        总经理 陈静"
echo
echo "角色直达：<门户>/?as=finance@com.demo"
echo "验收脚本：python infra/verify.py $(read_out com-deploy apiUrl)"
echo "清理环境：./teardown.sh --name $NAME --location $LOCATION --yes"
c_ok "──────────────────────────────────────────────"
