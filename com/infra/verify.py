#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
端到端验收脚本：登录 → 建单 → 提交 → 多级审批 → 生成合同 → 归档 → 摘要校验 → 下载
用法：python verify.py http://localhost:5199
      python verify.py https://app-com-api-poc.azurewebsites.net
"""
import json
import sys
import time
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5199").rstrip("/")
PWD = "Com@2026"
PASS, FAIL = "\033[32mPASS\033[0m", "\033[31mFAIL\033[0m"
results = []


def call(path, method="GET", body=None, token=None, raw=False):
    url = BASE + path
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            content = r.read()
            return (r.status, content if raw else json.loads(content or b"{}"))
    except urllib.error.HTTPError as e:
        content = e.read()
        try:
            return e.code, json.loads(content)
        except Exception:
            return e.code, content[:300]


def check(name, cond, extra=""):
    results.append(cond)
    print(f"  {'✔' if cond else '✘'} {PASS if cond else FAIL}  {name}{('  ' + str(extra)) if extra else ''}")
    return cond


def login(upn):
    st, r = call("/api/auth/login", "POST", {"upn": upn, "password": PWD})
    assert st == 200, f"登录失败 {upn}: {st} {r}"
    return r["token"]


print(f"\n=== COM 端到端验收 @ {BASE} ===\n")

# 1. 健康
st, r = call("/healthz")
check("健康检查 /healthz", st == 200 and r.get("status") == "ok", r)

st, r = call("/api/ops/deps", token=login("sales@com.demo"))
okcount = sum(1 for d in r if d["ok"]) if isinstance(r, list) else 0
check("依赖探针 /api/ops/deps", isinstance(r, list) and len(r) >= 5, f"{okcount}/{len(r) if isinstance(r,list) else 0} 正常")
for d in (r if isinstance(r, list) else []):
    print(f"      · {d['name']:<26} {'OK ' if d['ok'] else 'BAD'} {d['latencyMs']:>5}ms  {d['detail'][:44]}")

# 2. 建单（金额跨 2 级审批）
sales = login("sales@com.demo")
st, products = call("/api/products", token=sales)
st, customers = call("/api/customers", token=sales)
big = max(products, key=lambda p: p["unitPrice"])
st, order = call("/api/orders", "POST", {
    "customerId": customers[0]["id"],
    "terms": "月结 30 天",
    "remark": "自动化验收脚本创建",
    "lines": [
        {"productId": big["id"], "qty": 5, "unitPrice": big["unitPrice"]},
        {"productId": products[0]["id"], "qty": 2, "unitPrice": products[0]["unitPrice"]},
    ],
    "idempotencyKey": f"verify-{int(time.time())}",
}, sales)
check("创建订单 POST /api/orders", st in (200, 201), f"{st} {order.get('orderNo', '')}")
oid = order["id"]
amount = order["totalAmount"]
levels = 1 if amount < 500_000 else 2 if amount < 2_000_000 else 3
check(f"订单金额 ¥{amount:,.2f} → 预期 {levels} 级审批", len([a for a in order["approvals"]]) == levels, order["approvalRule"])

st, order2 = call(f"/api/orders/{oid}/submit", "POST", None, sales)
check("提交订单 → PendingApproval", st == 200 and order2["status"] == "PendingApproval", order2.get("status"))

# 3. 逐级审批
chain = [("director@com.demo", "销售总监"), ("finance@com.demo", "财务"), ("gm@com.demo", "总经理")][:levels]
for i, (upn, role) in enumerate(chain, 1):
    tok = login(upn)
    st, pend = call("/api/approvals/pending", token=tok)
    task = next((p for p in pend if p["orderId"] == oid), None)
    if not check(f"L{i} {role} 收到待办", task is not None):
        break
    st, dr = call(f"/api/approvals/{task['taskId']}/decision", "POST",
                  {"decision": "approve", "comment": f"验收自动批准 L{i}"}, tok)
    check(f"L{i} 批准 → {dr.get('workflow', dr.get('status'))}", st == 200)

st, final = call(f"/api/orders/{oid}", token=sales)
check("订单状态 = Approved", final["status"] == "Approved", final["status"])

# 4. 生成合同
st, gen = call(f"/api/contracts/{oid}/generate", "POST", None, sales)
check("投递合同渲染任务 202", st in (200, 201, 202), gen)

contract = None
for _ in range(40):
    time.sleep(1.5)
    st, cur = call(f"/api/orders/{oid}", token=sales)
    c = cur.get("contract")
    if c and c["status"] in ("Archived", "Failed"):
        contract = c
        break
check("合同渲染 + 归档完成", contract is not None and contract["status"] == "Archived",
      (contract or {}).get("status", "超时"))
if contract:
    check("PDF 大小合理（>10KB）", contract["sizeBytes"] > 10_000, f"{contract['sizeBytes']} bytes")
    check("SHA256 已写入", bool(contract.get("sha256")), (contract.get("sha256") or "")[:24] + "…")
    check("WORM 不可变截止时间已设置", bool(contract.get("immutableUntil")), contract.get("immutableUntil"))
    st, ver = call(f"/api/contracts/{contract['id']}/verify", token=sales)
    check("摘要重算一致", st == 200 and ver.get("match") is True, ver.get("match"))
    st, pdf = call(f"/api/contracts/{contract['id']}/download", token=sales, raw=True)
    check("PDF 下载（文件头 %PDF）", st == 200 and pdf[:4] == b"%PDF", f"{len(pdf)} bytes")

# 5. 越权返回 404 而不是 403
st, r = call(f"/api/orders/{oid}", token=login("director@com.demo"))
check("跨角色访问订单详情可读（审批人视角）", st in (200, 404), st)

st, r = call("/api/orders", token=None)
check("未认证访问返回 401", st == 401, st)

st, r = call("/internal/notify", "POST", {"taskId": "00000000-0000-0000-0000-000000000000",
                                         "orderId": oid, "level": 1, "approverUpn": "x", "amount": 1})
check("内部端点无密钥被拒绝", st in (401, 403, 404), st)

total, passed = len(results), sum(1 for x in results if x)
print(f"\n=== 结果：{passed}/{total} 通过 ===")
print(f"订单 {order.get('orderNo')}  (id={oid})\n")
sys.exit(0 if passed == total else 1)
