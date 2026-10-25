using RoomOps.Domain.Common;
using System.Net;
using System.Text;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Changes;

namespace RoomOps.Application.Services;

/// <summary>Self-contained HTML change report in the RoomOps visual identity, suitable for change-management records.</summary>
public static class ReportRenderer
{
    public static string Disclaimer => Text.L(
        "This report is generated from the application-local operation history of RoomOps Studio on the administrator's device. " +
        "It is not the Microsoft 365 unified audit log, it is not centralized, and it is not tamper-proof. Use the Microsoft Purview audit log for authoritative records.",
        "Dit rapport is gemaakt uit de lokale bewerkingsgeschiedenis van RoomOps Studio op het apparaat van de beheerder. " +
        "Het is niet het uniforme auditlogboek van Microsoft 365, niet centraal en niet beveiligd tegen manipulatie. Gebruik het auditlogboek van Microsoft Purview voor gezaghebbende gegevens.");

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    public static string Render(IReadOnlyList<AuditRecord> records, SessionSnapshot session, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        var succeeded = records.Count(r => r.Outcome == AuditOutcome.Succeeded);
        var failed = records.Count(r => r.Outcome is AuditOutcome.Failed or AuditOutcome.PartiallyApplied or AuditOutcome.Stale);
        var verified = records.Count(r => r.Verification?.Outcome == VerificationOutcome.Verified);
        sb.Append("""
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>RoomOps Studio — Change report</title>
<style>
:root{--bg:#f7f6f3;--surface:#fff;--ink:#16181d;--muted:#5c6170;--line:#e4e2dc;--accent:#0b7fb8;--ok:#2f855a;--warn:#b7791f;--bad:#c53030;--mono:"JetBrains Mono","Cascadia Mono",Consolas,monospace}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:13px/1.55 "Inter","Segoe UI Variable","Segoe UI",system-ui,sans-serif}
.wrap{max-width:1040px;margin:0 auto;padding:40px 32px 64px}
header{display:flex;align-items:center;gap:14px;margin-bottom:28px}
header h1{font-size:20px;margin:0;letter-spacing:-.01em}header p{margin:2px 0 0;color:var(--muted)}
.meta{display:grid;grid-template-columns:repeat(4,1fr);gap:1px;background:var(--line);border:1px solid var(--line);border-radius:10px;overflow:hidden;margin-bottom:20px}
.meta div{background:var(--surface);padding:12px 14px}.meta b{display:block;font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);font-weight:600;margin-bottom:2px}
.note{background:#fff8e6;border:1px solid #f1dfb0;border-radius:10px;padding:10px 14px;color:#6b4e0e;margin-bottom:28px}
.demo{background:#eaf4fb;border-color:#bcdcef;color:#134a66}
.rec{background:var(--surface);border:1px solid var(--line);border-radius:12px;margin-bottom:16px;page-break-inside:avoid}
.rec h2{font-size:14px;margin:0}.rec .head{display:flex;justify-content:space-between;gap:12px;padding:14px 16px;border-bottom:1px solid var(--line)}
.rec .sub{color:var(--muted);font-size:12px;margin-top:2px}.rec .body{padding:12px 16px 16px}
.pill{display:inline-block;border-radius:999px;padding:2px 9px;font-size:11px;font-weight:600;white-space:nowrap}
.Succeeded,.Verified{background:#e6f4ec;color:var(--ok)}.Failed,.Mismatch,.Stale,.PartiallyApplied{background:#fdecec;color:var(--bad)}
.PendingPropagation,.PendingProvisioning,.NotVerified{background:#fdf3e1;color:var(--warn)}.NoChange,.Skipped,.NotApplicable{background:#eef0f3;color:var(--muted)}
table{width:100%;border-collapse:collapse;margin:8px 0}th{text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted);font-weight:600;padding:6px 8px;border-bottom:1px solid var(--line)}
td{padding:6px 8px;border-bottom:1px solid #f0eee9;vertical-align:top}code,.mono{font-family:var(--mono);font-size:11.5px}
pre{background:#14161b;color:#d7dae0;border-radius:8px;padding:10px 12px;overflow:auto;font:11.5px/1.5 var(--mono);white-space:pre-wrap;word-break:break-word}
h3{font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);margin:14px 0 4px}
footer{margin-top:32px;color:var(--muted);font-size:11px}
@media print{body{background:#fff}.wrap{padding:0}.rec{border-color:#ccc}}
</style></head><body><div class="wrap">
<header><svg width="36" height="36" viewBox="0 0 32 32" aria-hidden="true"><rect width="32" height="32" rx="8" fill="#16181d"/><rect x="7" y="7" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" stroke-width="1.6"/><rect x="17" y="7" width="8" height="8" rx="2" fill="#2bb3f0"/><rect x="7" y="17" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" stroke-width="1.6"/><rect x="17" y="17" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" stroke-width="1.6"/></svg>
""");
        sb.Append($"<div><h1>{Text.L("Change report", "Wijzigingsrapport")}</h1><p>RoomOps Studio · {Text.L("Intelligent Workspace Administration", "Intelligent werkplekbeheer")}</p></div></header>\n");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<div><b>Tenant</b>{E(session.TenantName ?? session.TenantId)}<div class=\"mono\">{E(session.TenantId)}</div></div>");
        sb.Append($"<div><b>{Text.L("Generated", "Gemaakt")}</b>{generatedAt:yyyy-MM-dd HH:mm} UTC</div>");
        sb.Append(Text.L($"<div><b>Records</b>{records.Count} ({succeeded} succeeded, {failed} failed)</div>", $"<div><b>Records</b>{records.Count} ({succeeded} geslaagd, {failed} mislukt)</div>"));
        sb.Append(Text.L($"<div><b>Verified by reread</b>{verified} of {records.Count}</div>", $"<div><b>Geverifieerd door teruglezen</b>{verified} van {records.Count}</div>"));
        sb.Append("</div>");
        if (session.Mode == SessionMode.Demo)
        {
            sb.Append(Text.L("<div class=\"note demo\"><b>Demonstration data.</b> These records were produced in RoomOps demo mode against a synthetic tenant. No Microsoft 365 changes were made.</div>", "<div class=\"note demo\"><b>Demonstratiegegevens.</b> Deze records zijn gemaakt in de demomodus van RoomOps tegen een fictieve tenant. Er zijn geen wijzigingen in Microsoft 365 aangebracht.</div>"));
        }
        sb.Append($"<div class=\"note\">{E(Disclaimer)}</div>");

        foreach (var r in records)
        {
            sb.Append("<section class=\"rec\"><div class=\"head\"><div>");
            sb.Append($"<h2>{E(r.OperationLabel)} — {E(r.TargetName)}</h2>");
            sb.Append($"<div class=\"sub\">{r.Timestamp:yyyy-MM-dd HH:mm:ss} UTC · {E(r.Actor)} · <span class=\"mono\">{E(r.TargetId)}</span></div></div><div>");
            sb.Append($"<span class=\"pill {r.Outcome}\">{E(Humanize(r.Outcome.ToString()))}</span> ");
            if (r.Verification is { } v) sb.Append($"<span class=\"pill {v.Outcome}\">{E(Humanize(v.Outcome.ToString()))}</span>");
            sb.Append("</div></div><div class=\"body\">");
            if (r.Changes.Count > 0)
            {
                sb.Append(Text.L("<h3>Changes</h3><table><thead><tr><th>Setting</th><th>Source</th><th>Before</th><th>Requested</th></tr></thead><tbody>", "<h3>Wijzigingen</h3><table><thead><tr><th>Instelling</th><th>Bron</th><th>Voor</th><th>Aangevraagd</th></tr></thead><tbody>"));
                foreach (var c in r.Changes)
                {
                    sb.Append($"<tr><td>{E(c.Label)}<div class=\"mono\" style=\"color:var(--muted)\">{E(c.Field)}</div></td><td>{E(Humanize(c.Source.ToString()))}</td><td>{E(c.Before)}</td><td>{E(c.After)}</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            if (r.Steps.Count > 0)
            {
                sb.Append(Text.L("<h3>Executed commands</h3><pre>", "<h3>Uitgevoerde opdrachten</h3><pre>"));
                foreach (var s in r.Steps)
                {
                    sb.Append($"# {E(s.Description)} — {E(s.Outcome)}{(s.Attempts > 1 ? Text.L($" after {s.Attempts} attempts", $" na {s.Attempts} pogingen") : string.Empty)}{(s.Error is null ? string.Empty : $" — {E(s.Error)}")}\n{E(s.Script)}\n");
                }
                sb.Append("</pre>");
            }
            if (r.Verification is { } ver)
            {
                sb.Append($"<h3>{Text.L("Verification", "Verificatie")}</h3><p>{E(ver.Message)}</p>");
                if (ver.Fields.Count > 0)
                {
                    sb.Append(Text.L("<table><thead><tr><th>Value</th><th>Expected</th><th>Read back</th><th></th></tr></thead><tbody>", "<table><thead><tr><th>Waarde</th><th>Verwacht</th><th>Teruggelezen</th><th></th></tr></thead><tbody>"));
                    foreach (var f in ver.Fields)
                    {
                        sb.Append($"<tr><td>{E(f.Label)}</td><td>{E(f.Expected)}</td><td>{E(f.Actual)}</td><td>{(f.Matches ? "✓" : "✗")}</td></tr>");
                    }
                    sb.Append("</tbody></table>");
                }
            }
            if (r.Error is not null) sb.Append($"<h3>{Text.L("Error", "Fout")}</h3><p>{E(r.Error)}</p>");
            sb.Append("</div></section>");
        }
        sb.Append($"<footer>{Text.L("Generated by RoomOps Studio. Commands are shown as executed through parameter binding; no credentials or tokens are included.", "Gemaakt door RoomOps Studio. Opdrachten worden getoond zoals ze via parameterbinding zijn uitgevoerd; aanmeldgegevens of tokens zijn niet opgenomen.")}</footer></div></body></html>");
        return sb.ToString();
    }

    private static string Humanize(string pascal)
    {
        var sb = new StringBuilder();
        foreach (var ch in pascal)
        {
            if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
            sb.Append(sb.Length == 0 ? ch : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
