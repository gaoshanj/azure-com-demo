using Com.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Com.Shared.Data;

/// <summary>幂等种子数据：容器重建、全新环境、本地开发都靠它一键得到可演示的状态。</summary>
public static class Seeder
{
    public const string DemoPassword = "Com@2026";

    public static async Task SeedAsync(ComDbContext db, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var pwd = PasswordHasher.Hash(DemoPassword);

        var users = new List<AppUser>
        {
            new() { Upn = "sales@com.demo",    DisplayName = "张伟 · 华东销售", Role = Roles.Sales },
            new() { Upn = "director@com.demo", DisplayName = "李强 · 销售总监", Role = Roles.Approver1 },
            new() { Upn = "finance@com.demo",  DisplayName = "王敏 · 财务经理", Role = Roles.Finance },
            new() { Upn = "gm@com.demo",       DisplayName = "陈静 · 总经理",   Role = Roles.Admin },
        };
        foreach (var u in users) { u.PasswordHash = pwd.Hash; u.PasswordSalt = pwd.Salt; }
        db.Users.AddRange(users);

        var customers = new List<Customer>
        {
            new() { Code = "C-1001", Name = "上海远洋装备制造有限公司", TaxId = "91310115MA1K35XX01", CreditLimit = 3_000_000m },
            new() { Code = "C-1002", Name = "江苏精衡精密仪器股份有限公司", TaxId = "91320500MA1UX8XX02", CreditLimit = 1_500_000m },
            new() { Code = "C-1003", Name = "深圳市恒达电子科技有限公司", TaxId = "91440300MA5EJ9XX03", CreditLimit = 2_200_000m },
            new() { Code = "C-1004", Name = "北京中科智联信息技术有限公司", TaxId = "91110108MA01T7XX04", CreditLimit = 800_000m },
            new() { Code = "C-1005", Name = "成都天府新材料有限责任公司", TaxId = "91510100MA6C2PXX05", CreditLimit = 5_000_000m },
        };
        db.Customers.AddRange(customers);

        var products = new List<Product>
        {
            new() { Sku = "SW-LIC-001", Name = "云平台年度授权（标准版）", UnitPrice = 128_000m },
            new() { Sku = "SW-LIC-002", Name = "云平台年度授权（企业版）", UnitPrice = 368_000m },
            new() { Sku = "HW-SRV-210", Name = "边缘计算服务器 E210",       UnitPrice = 86_500m },
            new() { Sku = "HW-GTW-050", Name = "工业物联网关 G50",          UnitPrice = 12_800m },
            new() { Sku = "SV-IMP-010", Name = "实施与迁移服务（人天）",     UnitPrice = 6_800m  },
            new() { Sku = "SV-TRN-005", Name = "原厂认证培训（人天）",       UnitPrice = 4_500m  },
            new() { Sku = "SW-SEC-030", Name = "零信任接入模块（年）",       UnitPrice = 96_000m },
            new() { Sku = "SV-SUP-001", Name = "7×24 原厂支持（年）",        UnitPrice = 58_000m },
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var today = now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd");

        // 订单 1：草稿
        var o1 = new Order
        {
            OrderNo = $"COM-{today}-0001", CustomerId = customers[3].Id, Status = OrderStatus.Draft,
            CreatedBy = "sales@com.demo", CreatedAt = now.AddHours(-6), Terms = "款到发货",
            Remark = "客户要求先出方案再签合同"
        };
        o1.Lines.Add(new OrderLine { OrderId = o1.Id, LineNo = 1, ProductId = products[2].Id, ProductName = products[2].Name, Qty = 4, UnitPrice = products[2].UnitPrice, Amount = products[2].UnitPrice * 4 });
        o1.Lines.Add(new OrderLine { OrderId = o1.Id, LineNo = 2, ProductId = products[4].Id, ProductName = products[4].Name, Qty = 10, UnitPrice = products[4].UnitPrice, Amount = products[4].UnitPrice * 10 });
        o1.TotalAmount = o1.Lines.Sum(l => l.Amount);

        // 订单 2：审批中（2 级，L1 已批，L2 待批）
        var o2 = new Order
        {
            OrderNo = $"COM-{today}-0002", CustomerId = customers[0].Id, Status = OrderStatus.PendingApproval,
            CreatedBy = "sales@com.demo", CreatedAt = now.AddHours(-26), SubmittedAt = now.AddHours(-25), Terms = "月结 30 天"
        };
        o2.Lines.Add(new OrderLine { OrderId = o2.Id, LineNo = 1, ProductId = products[1].Id, ProductName = products[1].Name, Qty = 3, UnitPrice = products[1].UnitPrice, Amount = products[1].UnitPrice * 3 });
        o2.Lines.Add(new OrderLine { OrderId = o2.Id, LineNo = 2, ProductId = products[6].Id, ProductName = products[6].Name, Qty = 1, UnitPrice = products[6].UnitPrice, Amount = products[6].UnitPrice });
        o2.TotalAmount = o2.Lines.Sum(l => l.Amount); // 1,200,000

        // 订单 3：已通过（3 级全批），等待生成合同
        var o3 = new Order
        {
            OrderNo = $"COM-{today}-0003", CustomerId = customers[4].Id, Status = OrderStatus.Approved,
            CreatedBy = "sales@com.demo", CreatedAt = now.AddDays(-3), SubmittedAt = now.AddDays(-3), Terms = "预付 30% + 验收后 70%",
            Remark = "战略客户首单，需在季度末前完成交付"
        };
        o3.Lines.Add(new OrderLine { OrderId = o3.Id, LineNo = 1, ProductId = products[0].Id, ProductName = products[0].Name, Qty = 12, UnitPrice = products[0].UnitPrice, Amount = products[0].UnitPrice * 12 });
        o3.Lines.Add(new OrderLine { OrderId = o3.Id, LineNo = 2, ProductId = products[7].Id, ProductName = products[7].Name, Qty = 3, UnitPrice = products[7].UnitPrice, Amount = products[7].UnitPrice * 3 });
        o3.Lines.Add(new OrderLine { OrderId = o3.Id, LineNo = 3, ProductId = products[5].Id, ProductName = products[5].Name, Qty = 15, UnitPrice = products[5].UnitPrice, Amount = products[5].UnitPrice * 15 });
        o3.TotalAmount = o3.Lines.Sum(l => l.Amount); // 1,536,000 + 174,000 + 67,500

        // 订单 4：已归档（有合同记录，无 Blob —— 仅作历史展示会显得不真实，因此不塞）
        db.Orders.AddRange(o1, o2, o3);
        await db.SaveChangesAsync(ct);

        // 审批任务
        db.ApprovalTasks.AddRange(
            new ApprovalTask
            {
                OrderId = o2.Id, Level = 1, ApproverRole = Roles.Approver1, ApproverUpn = "director@com.demo",
                Decision = ApprovalDecision.Approved, DecidedAt = now.AddHours(-20),
                Comment = "客户资质良好，同意", CreatedAt = now.AddHours(-25), DueAt = now.AddHours(-1)
            },
            new ApprovalTask
            {
                OrderId = o2.Id, Level = 2, ApproverRole = Roles.Finance, ApproverUpn = "finance@com.demo",
                Decision = ApprovalDecision.Pending, CreatedAt = now.AddHours(-20), DueAt = now.AddHours(28)
            });
        foreach (var (level, role, upn) in new[] { (1, Roles.Approver1, "director@com.demo"), (2, Roles.Finance, "finance@com.demo"), (3, Roles.Admin, "gm@com.demo") })
        {
            db.ApprovalTasks.Add(new ApprovalTask
            {
                OrderId = o3.Id, Level = level, ApproverRole = role, ApproverUpn = upn,
                Decision = ApprovalDecision.Approved, DecidedAt = now.AddDays(-3).AddHours(level),
                Comment = level == 1 ? "价格已谈定" : level == 2 ? "回款计划已确认" : "同意，按战略客户条款执行",
                CreatedAt = now.AddDays(-3), DueAt = now.AddDays(-1)
            });
        }

        // 审计事件
        db.AuditEvents.AddRange(
            new AuditEvent { EntityType = "Order", EntityId = o1.Id.ToString(), Action = "order.created", ToStatus = "Draft", Actor = "sales@com.demo", CreatedAt = o1.CreatedAt, TraceId = "-" },
            new AuditEvent { EntityType = "Order", EntityId = o2.Id.ToString(), Action = "order.created", ToStatus = "Draft", Actor = "sales@com.demo", CreatedAt = o2.CreatedAt, TraceId = "-" },
            new AuditEvent { EntityType = "Order", EntityId = o2.Id.ToString(), Action = "order.submitted", FromStatus = "Draft", ToStatus = "PendingApproval", Actor = "sales@com.demo", CreatedAt = o2.SubmittedAt!.Value, TraceId = "-" },
            new AuditEvent { EntityType = "Order", EntityId = o2.Id.ToString(), Action = "approval.approved.L1", Actor = "director@com.demo", CreatedAt = now.AddHours(-20), TraceId = "-" },
            new AuditEvent { EntityType = "Order", EntityId = o3.Id.ToString(), Action = "order.submitted", FromStatus = "Draft", ToStatus = "PendingApproval", Actor = "sales@com.demo", CreatedAt = o3.SubmittedAt!.Value, TraceId = "-" },
            new AuditEvent { EntityType = "Order", EntityId = o3.Id.ToString(), Action = "order.approved", FromStatus = "PendingApproval", ToStatus = "Approved", Actor = "gm@com.demo", CreatedAt = now.AddDays(-3).AddHours(3), TraceId = "-" });

        // 站内信
        db.Notifications.AddRange(
            new Notification { UserUpn = "finance@com.demo", Title = $"待审批 · {o2.OrderNo}", Body = $"客户 {customers[0].Name} 的订单 ¥{o2.TotalAmount:N2} 已通过一级审批，等待财务复核。", Link = $"/#/orders/{o2.Id}", CreatedAt = now.AddHours(-20) },
            new Notification { UserUpn = "sales@com.demo", Title = $"订单已全部批准 · {o3.OrderNo}", Body = "三级审批已通过，可生成合同。", Link = $"/#/orders/{o3.Id}", CreatedAt = now.AddDays(-3).AddHours(3) });

        await db.SaveChangesAsync(ct);
    }
}
