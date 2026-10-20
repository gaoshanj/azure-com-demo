using System.Reflection;
using Com.Shared.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Com.Shared.Services;

/// <summary>
/// 合同 PDF 渲染。用 QuestPDF 而不是无头浏览器：
/// 纯托管、Linux 原生、字体可控、渲染结果可复现（同样的输入永远同样的字节）。
/// </summary>
public class QuestPdfRenderer : IPdfRenderer
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _fontOk;

    public QuestPdfRenderer()
    {
        EnsureInit();
    }

    private static void EnsureInit()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.EnableDetailedLayoutErrors = false;

            // 内嵌中文字体：App Service Linux 基础镜像不带 CJK 字体，
            // 不显式注册会渲染成方框。这是部署到 Linux 时最常见的坑之一。
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                QuestPDF.Drawing.FontManager.RegisterFontFromBinaryData(ms.ToArray());
                _fontOk = true;
            }
            _initialized = true;
        }
    }

    public bool Ping() => _fontOk;

    public byte[] Render(ContractRenderModel m)
    {
        EnsureInit();
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.8f, Unit.Centimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(t => t.FontSize(9.5f).FontFamily("Noto Sans SC").FontColor("#1A1D22"));

                page.Header().Element(c => Header(c, m));
                page.Content().Element(c => Body(c, m));
                page.Footer().Element(Footer);
            });
        }).GeneratePdf();
    }

    private static void Header(IContainer c, ContractRenderModel m)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(x =>
                {
                    x.Item().Text("购销合同").FontSize(20).Bold().FontColor("#0B0C0E");
                    x.Item().PaddingTop(2).Text($"合同编号  {m.OrderNo}-CT").FontSize(9).FontColor("#6C7079");
                });
                row.ConstantItem(150).AlignRight().Column(x =>
                {
                    x.Item().Text("COM 合同订单管理系统").FontSize(8.5f).FontColor("#6C7079");
                    x.Item().Text(m.CreatedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm")).FontSize(8.5f).FontColor("#6C7079");
                    x.Item().PaddingTop(3).Text("状态：已归档（WORM）").FontSize(8.5f).FontColor("#0F7B57");
                });
            });
            col.Item().PaddingTop(10).LineHorizontal(1.2f).LineColor("#0B0C0E");
        });
    }

    private static void Body(IContainer c, ContractRenderModel m)
    {
        c.PaddingTop(14).Column(col =>
        {
            col.Spacing(10);

            // 双方信息
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(box => InfoBlock(box, "甲方（买方）",
                    new[] { m.CustomerName, $"客户编码：{m.CustomerCode}", $"纳税人识别号：{m.TaxId}" }));
                row.ConstantItem(16);
                row.RelativeItem().Element(box => InfoBlock(box, "乙方（卖方）",
                    new[] { "格思信息技术有限公司", "纳税人识别号：91310000MA1FL0XX0X", "上海市浦东新区张江路 88 号" }));
            });

            // 标的明细
            col.Item().Text("一、标的物明细").FontSize(10.5f).Bold().FontColor("#0B0C0E");
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(34);
                    cd.RelativeColumn(4);
                    cd.ConstantColumn(44);
                    cd.ConstantColumn(78);
                    cd.ConstantColumn(88);
                });

                table.Header(h =>
                {
                    foreach (var t in new[] { "序号", "产品名称", "数量", "单价（元）", "金额（元）" })
                        h.Cell().Element(Th).Text(t).FontSize(8.6f);
                });

                foreach (var l in m.Lines)
                {
                    table.Cell().Element(Td).Text(l.LineNo.ToString()).FontSize(8.8f);
                    table.Cell().Element(Td).Text(l.ProductName).FontSize(8.8f);
                    table.Cell().Element(Td).AlignRight().Text(l.Qty.ToString()).FontSize(8.8f);
                    table.Cell().Element(Td).AlignRight().Text(l.UnitPrice.ToString("N2")).FontSize(8.8f);
                    table.Cell().Element(Td).AlignRight().Text(l.Amount.ToString("N2")).FontSize(8.8f);
                }

                table.Cell().ColumnSpan(4).Element(Td).AlignRight().Text("合计（含税）").Bold().FontSize(9);
                table.Cell().Element(Td).AlignRight().Text($"¥ {m.TotalAmount:N2}").Bold().FontSize(9);
            });

            // 条款
            col.Item().Text("二、主要条款").FontSize(10.5f).Bold().FontColor("#0B0C0E");
            col.Item().Column(x =>
            {
                x.Spacing(3);
                x.Item().Text($"1. 付款方式：{m.Terms}；以双方确认的订单为准。");
                x.Item().Text("2. 交付与验收：乙方按订单约定的时间与地点交付，甲方应在到货后 5 个工作日内完成验收。");
                x.Item().Text("3. 质量保证：产品质保期为验收合格之日起 12 个月，质保期内非人为损坏由乙方负责。");
                x.Item().Text("4. 争议解决：双方协商不成的，提交合同签订地有管辖权的人民法院解决。");
                if (!string.IsNullOrWhiteSpace(m.Remark))
                    x.Item().Text($"5. 特别约定：{m.Remark}");
            });

            // 审批留痕
            col.Item().Text("三、审批留痕").FontSize(10.5f).Bold().FontColor("#0B0C0E");
            col.Item().Column(x =>
            {
                foreach (var t in m.ApprovalTrail)
                    x.Item().Text("· " + t).FontSize(8.6f).FontColor("#4A4F58");
            });

            // 签署
            col.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Element(b => SignBlock(b, "甲方（盖章）", m.CustomerName));
                row.ConstantItem(30);
                row.RelativeItem().Element(b => SignBlock(b, "乙方（盖章）", "格思信息技术有限公司"));
            });
        });
    }

    private static void InfoBlock(IContainer c, string title, string[] lines)
    {
        c.Border(0.8f).BorderColor("#D8DCE3").Padding(10).Column(x =>
        {
            x.Item().Text(title).FontSize(8).FontColor("#6C7079");
            x.Item().PaddingTop(3).Text(lines[0]).FontSize(10).Bold().FontColor("#0B0C0E");
            foreach (var l in lines.Skip(1))
                x.Item().PaddingTop(1).Text(l).FontSize(8.2f).FontColor("#4A4F58");
        });
    }

    private static void SignBlock(IContainer c, string role, string who)
    {
        c.Column(x =>
        {
            x.Item().Text(role).FontSize(8).FontColor("#6C7079");
            x.Item().PaddingTop(2).Text(who).FontSize(9).FontColor("#1A1D22");
            x.Item().PaddingTop(22).LineHorizontal(0.8f).LineColor("#D8DCE3");
            x.Item().PaddingTop(3).Text("签字 / 日期").FontSize(7.5f).FontColor("#9AA0A8");
        });
    }

    private static void Footer(IContainer c)
    {
        c.PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text("本文件由 COM 系统自动生成，归档后不可修改。")
                .FontSize(7.5f).FontColor("#9AA0A8");
            row.ConstantItem(90).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(7.5f).FontColor("#9AA0A8"));
                t.Span("第 ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
                t.Span(" 页");
            });
        });
    }

    private static IContainer Th(IContainer c) => c
        .Background("#F3F5F8").BorderBottom(0.8f).BorderColor("#D8DCE3")
        .PaddingVertical(6).PaddingHorizontal(5);

    private static IContainer Td(IContainer c) => c
        .BorderBottom(0.6f).BorderColor("#E7EAEF")
        .PaddingVertical(6).PaddingHorizontal(5);
}
