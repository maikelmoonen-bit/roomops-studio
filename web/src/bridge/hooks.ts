import { useEffect } from "react";
import { useMutation, useQuery, useQueryClient, type UseQueryOptions } from "@tanstack/react-query";
import { bridge, type BridgeError } from "./client";
import type { BridgeEventTopic, BridgeEvents, BridgeMethodName, BridgeMethods } from "./contracts.gen";
import { useSession } from "@/state/session";

type Req<M extends BridgeMethodName> = BridgeMethods[M]["request"];
type Res<M extends BridgeMethodName> = BridgeMethods[M]["response"];

/**
 * Query keys always start with the session epoch. A new session (tenant switch, demo ↔ live) clears the cache, and the
 * epoch prefix guarantees no stale entry from another tenant can ever be read.
 */
export function useBridgeQuery<M extends BridgeMethodName>(
  method: M,
  params: Req<M>,
  options?: Omit<UseQueryOptions<Res<M>, BridgeError>, "queryKey" | "queryFn"> & { requireSession?: boolean },
) {
  const { session } = useSession();
  const requireSession = options?.requireSession ?? true;
  const active = !requireSession || (session != null && session.mode !== "None");
  return useQuery<Res<M>, BridgeError>({
    queryKey: [session?.epoch ?? 0, method, params],
    queryFn: () => bridge.call(method, params),
    ...options,
    enabled: active && (options?.enabled ?? true),
  });
}

export function useBridgeMutation<M extends BridgeMethodName>(method: M) {
  return useMutation<Res<M>, BridgeError, Req<M>>({ mutationFn: (params) => bridge.call(method, params) });
}

export function useBridgeEvent<T extends BridgeEventTopic>(topic: T, handler: (payload: BridgeEvents[T]) => void) {
  useEffect(() => bridge.on(topic, handler), [topic, handler]);
}

/** Invalidates every query of the current session (after a change was executed, or after a scan). */
export function useInvalidateAll() {
  const client = useQueryClient();
  return () => client.invalidateQueries();
}
