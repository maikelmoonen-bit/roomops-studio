import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MotionConfig } from "motion/react";
import { Toaster } from "sonner";
import "./styles/globals.css";
import { BridgeError } from "./bridge/client";
import { App } from "./App";
import { SessionProvider } from "./state/session";
import { PlanFlowProvider } from "./state/plan-flow";
import { TooltipProvider } from "./components/ui/overlay";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      gcTime: 10 * 60_000,
      refetchOnWindowFocus: false,
      // Retry transient failures only; permission, validation and connection errors are shown immediately.
      retry: (count, error) => count < 2 && error instanceof BridgeError && (error.kind === "Transient" || error.kind === "Throttled"),
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <MotionConfig reducedMotion="user">
        <TooltipProvider delayDuration={350} skipDelayDuration={200}>
          <SessionProvider>
            <PlanFlowProvider>
              <App />
              <Toaster position="bottom-right" theme="system" toastOptions={{ className: "!bg-raised !border-line !text-ink !shadow-pop !text-[13px]" }} />
            </PlanFlowProvider>
          </SessionProvider>
        </TooltipProvider>
      </MotionConfig>
    </QueryClientProvider>
  </StrictMode>,
);
