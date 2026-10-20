#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# COM 合同订单管理系统 · 环境清理
#
#   ./teardown.sh --name com-poc --location eastasia --yes
#   ./teardown.sh --name com-poc --dry-run        # 只列出将被删除的资源
#   ./teardown.sh --name com-poc --yes --keep-kv  # 保留 Key Vault（软删除保护）
#
# 为什么需要专门脚本：
#   1. 合同容器启用了不可变（WORM）策略，即使策略是 Unlocked 状态，
#      删除存储账户前也必须显式解除，否则 ARM 删除会卡在 409 Conflict。
#   2. Key Vault 有软删除 + 清除保护，资源组删除后名字仍被占用 90 天，
#      演示环境反复重建时会撞名，所以默认顺带 purge。
#   3. 逻辑删除的 App Service / SQL 也可能占名，脚本会一并检查。
# ─────────────────────────────────────────────────────────────
set -euo pipefail

NAME="com-poc"
LOCATION="eastasia"
SUBSCRIPTION=""
ASSUME_YES="false"
DRY_RUN="false"
KEEP_KV="false"
PURGE_ONLY="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --subscription) SUBSCRIPTION="$2"; shift 2 ;;
    --location)     LOCATION="$2"; shift 2 ;;
    --name)         NAME="$2"; shift 2 ;;
    --yes|-y)       ASSUME_YES="true"; shift ;;
    --dry-run)      DRY_RUN="true"; shift ;;
    --keep-kv)      KEEP_KV="true"; shift ;;
    --purge-only)   PURGE_ONLY="true"; shift ;;
    -h|--help)      sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "未知参数：$1"; exit 1 ;;
  esac
done

RG="rg-${NAME}-${LOCATION}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PY="${PYTHON_BIN:-python}"

c_ok()   { printf "\033[32m%s\033[0m\n" "$*"; }
c_info() { printf "\033[36m%s\033[0m\n" "$*"; }
c_warn() { printf "\033[33m%s\033[0m\n" "$*"; }
c_err()  { printf "\033[31m%s\033[0m\n" "$*"; }
step()   { printf "\n\033[1m▶ %s\033[0m\n" "$*"; }

command -v az >/dev/null || { c_err "未找到 Azure CLI"; exit 1; }
az account show >/dev/null 2>&1 || { c_err "请先执行 az login"; exit 1; }
if [[ -n "$SUBSCRIPTION" ]]; then az account set --subscription "$SUBSCRIPTION"; fi

SUB_ID=$(az account show --query id -o tsv)
c_info "订阅：$(az account show --query name -o tsv) ($SUB_ID)"

# ── 资源组是否存在 ───────────────────────────────────────────
RG_EXISTS=$(az group exists -n "$RG" -o tsv)
if [[ "$RG_EXISTS" != "true" && "$PURGE_ONLY" != "true" ]]; then
  c_warn "资源组 $RG 不存在，无需清理。"
  exit 0
fi

read_out() { az deployment group show -g "$RG" -n "$1" --query "properties.outputs.$2.value" -o tsv 2>/dev/null || true; }

# ── 枚举将删除的资源 ─────────────────────────────────────────
if [[ "$RG_EXISTS" == "true" ]]; then
  step "资源清单"
  az resource list -g "$RG" --query "[].{名称:name,类型:type}" -o table
  RG_LOCATION=$(az group show -n "$RG" --query location -o tsv)

  STORAGE_NAME=$(read_out com-deploy storageName)
  KV_NAME=$(read_out com-deploy kvName)
  SQL_FQDN=$(read_out com-deploy sqlFqdn)
  [[ -z "$STORAGE_NAME" ]] && STORAGE_NAME=$(az resource list -g "$RG" --resource-type Microsoft.Storage/storageAccounts --query "[0].name" -o tsv 2>/dev/null || true)
  [[ -z "$KV_NAME" ]] && KV_NAME=$(az resource list -g "$RG" --resource-type Microsoft.KeyVault/vaults --query "[0].name" -o tsv 2>/dev/null || true)

  c_info "存储账户：${STORAGE_NAME:-<未识别>}"
  c_info "Key Vault：${KV_NAME:-<未识别>}"
else
  RG_LOCATION="$LOCATION"
fi

if [[ "$DRY_RUN" == "true" ]]; then
  echo
  c_warn "dry-run：以上资源不会被删除。去掉 --dry-run 并加 --yes 才会真正清理。"
  exit 0
fi

# ── 二次确认 ─────────────────────────────────────────────────
if [[ "$ASSUME_YES" != "true" ]]; then
  echo
  c_warn "⚠️  即将删除资源组 $RG 及其下全部资源，此操作不可恢复（Key Vault 还会被 purge）。"
  read -r -p "确认请输入资源组名 [$RG]：" CONFIRM
  [[ "$CONFIRM" == "$RG" ]] || { c_warn "已取消。"; exit 1; }
fi

# ── 1. 解除 WORM 不可变策略 ──────────────────────────────────
if [[ "$RG_EXISTS" == "true" && -n "$STORAGE_NAME" ]]; then
  step "解除合同容器不可变策略（否则删除会被 409 拒绝）"
  for CONTAINER in contracts intake; do
    HAS_POLICY=$(az storage container immutability-policy show \
      --account-name "$STORAGE_NAME" -c "$CONTAINER" \
      --auth-mode login --query "id" -o tsv 2>/dev/null || true)
    if [[ -n "$HAS_POLICY" ]]; then
      if az storage container immutability-policy delete \
           --account-name "$STORAGE_NAME" -c "$CONTAINER" \
           --auth-mode login --only-show-errors -o none 2>/dev/null; then
        c_ok "已解除 $CONTAINER"
      else
        c_warn "$CONTAINER 策略为 Locked 状态，无法解除 —— 只能等保留期到期（默认 30 天）"
        c_warn "如需立刻删除：az storage container immutability-policy delete 会失败，属预期行为（WORM 的语义就是不可提前删）"
      fi
    else
      c_info "$CONTAINER 无不可变策略，跳过"
    fi
  done

  # 容器里还有 legal hold 的话也要先关
  for CONTAINER in contracts intake; do
    HOLD=$(az storage container legal-hold show \
      --account-name "$STORAGE_NAME" -c "$CONTAINER" \
      --auth-mode login --query "hasLegalHold" -o tsv 2>/dev/null || echo "false")
    if [[ "$HOLD" == "true" ]]; then
      az storage container legal-hold clear \
        --account-name "$STORAGE_NAME" -c "$CONTAINER" \
        --auth-mode login --tags "" --only-show-errors -o none 2>/dev/null || true
      c_ok "已清除 $CONTAINER 的 legal hold"
    fi
  done
fi

# ── 2. 删除资源组 ────────────────────────────────────────────
if [[ "$RG_EXISTS" == "true" ]]; then
  step "删除资源组 $RG（约 3–8 分钟）"
  az group delete -n "$RG" --yes --only-show-errors -o none
  c_ok "资源组已删除"
fi

# ── 3. 清除 Key Vault 软删除残留 ─────────────────────────────
if [[ "$KEEP_KV" != "true" && -n "${KV_NAME:-}" ]]; then
  step "清除 Key Vault 软删除记录（否则同名重建会失败）"
  if az keyvault show-deleted --name "$KV_NAME" --query name -o tsv >/dev/null 2>&1; then
    az keyvault purge --name "$KV_NAME" --location "$RG_LOCATION" --only-show-errors -o none \
      && c_ok "已 purge $KV_NAME" \
      || c_warn "purge 失败（可能开启了 purge protection，需等 7–90 天自动过期）"
  else
    c_info "无软删除记录，跳过"
  fi
fi

# ── 4. 汇总 ──────────────────────────────────────────────────
echo
c_ok "──────────────────────────────────────────────"
echo "资源组   ：$RG 已删除"
echo "Key Vault：$([[ "$KEEP_KV" == "true" ]] && echo "已保留（软删除）" || echo "${KV_NAME:-n/a} 已尝试 purge")"
echo
echo "常见残留检查："
echo "  az group list --query \"[?starts_with(name,'rg-${NAME}')].name\" -o table"
echo "  az keyvault list-deleted -o table"
echo "  az staticwebapp list -o table 2>/dev/null || true"
c_ok "──────────────────────────────────────────────"
