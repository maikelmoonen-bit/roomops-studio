import { Wand2 } from "lucide-react";
import type { Finding } from "@/bridge/contracts.gen";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/core";
import { SeverityBadge } from "@/components/common/domain";
import { t } from "@/lib/i18n";

export function FindingRow({ finding, showTarget = false, onNavigate }: { finding: Finding; showTarget?: boolean; onNavigate?: () => void }) {
  const { review } = usePlanFlow();
  const evidence = Object.entries(finding.evidence);
  return (
    <div className="grid grid-cols-[150px_1fr_auto] items-start gap-4 px-4 py-3">
      <div className="pt-0.5"><SeverityBadge severity={finding.severity} /></div>
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[13px] font-semibold text-ink">{finding.title}</span>
          <Badge tone="outline">{finding.category === "Technical" ? t("Technical") : t("Org baseline")}</Badge>
          <span className="t-mono text-[10.5px] text-ink-3">{finding.ruleId}</span>
        </div>
        {showTarget && (
          <button type="button" onClick={onNavigate} className="mt-0.5 text-[12px] font-medium text-accent-text hover:underline">{finding.targetName}</button>
        )}
        <p className="mt-0.5 text-[12.5px] text-ink-2">{finding.explanation}</p>
        {evidence.length > 0 && (
          <dl className="mt-1.5 flex flex-wrap gap-x-4 gap-y-0.5">
            {evidence.map(([k, v]) => <div key={k} className="flex gap-1.5 text-[11.5px]"><dt className="t-mono text-ink-3">{k}</dt><dd className="text-ink-2">{v}</dd></div>)}
          </dl>
        )}
      </div>
      <div>
        {finding.remediationId && finding.severity !== "UnableToVerify" && (
          <Button size="sm" icon={<Wand2 />} onClick={() => review({ kind: "remediate", findingIds: [finding.id] })}>{t("Remediate")}</Button>
        )}
      </div>
    </div>
  );
}
