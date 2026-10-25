import { lazy, Suspense, type ReactNode } from "react";
import { useLanguage } from "@/lib/i18n";
import { useRoute } from "@/lib/router";
import { useSession } from "@/state/session";
import { AppShell, LoadingScreen } from "@/components/shell/app-shell";
import { Skeleton } from "@/components/ui/core";
import { WelcomePage } from "@/features/welcome/welcome-page";
import { ConnectionsPage } from "@/features/connections/connections-page";
import { SettingsPage } from "@/features/settings/settings-page";

const DashboardPage = lazy(() => import("@/features/dashboard/dashboard-page"));
const RoomsPage = lazy(() => import("@/features/rooms/rooms-page"));
const RoomDetailPage = lazy(() => import("@/features/rooms/room-detail-page"));
const CreateRoomWizard = lazy(() => import("@/features/create-room/create-room-wizard"));
const RoomListsPage = lazy(() => import("@/features/room-lists/room-lists-page"));
const BuildingsPage = lazy(() => import("@/features/places/buildings-page"));
const PlacesPage = lazy(() => import("@/features/places/places-page"));
const BookingPoliciesPage = lazy(() => import("@/features/booking/booking-policies-page"));
const DelegatesPage = lazy(() => import("@/features/delegates/delegates-page"));
const WorkingHoursPage = lazy(() => import("@/features/working-hours/working-hours-page"));
const TemplatesPage = lazy(() => import("@/features/templates/templates-page"));
const BulkPage = lazy(() => import("@/features/bulk/bulk-page"));
const ValidationPage = lazy(() => import("@/features/validation/validation-page"));
const HistoryPage = lazy(() => import("@/features/history/history-page"));

function PageFallback() {
  return (
    <div className="space-y-4">
      <Skeleton className="h-8 w-64" />
      <Skeleton className="h-4 w-96" />
      <div className="grid grid-cols-4 gap-3 pt-4">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-24" />)}</div>
      <Skeleton className="h-80" />
    </div>
  );
}

function routeElement(segments: string[]): ReactNode {
  const [first, second] = segments;
  switch (first) {
    case undefined: return <DashboardPage />;
    case "rooms":
      if (second === "new") return <CreateRoomWizard />;
      return second ? <RoomDetailPage roomId={second} /> : <RoomsPage />;
    case "room-lists": return <RoomListsPage />;
    case "buildings": return <BuildingsPage />;
    case "places": return <PlacesPage />;
    case "booking": return <BookingPoliciesPage roomId={second} />;
    case "delegates": return <DelegatesPage roomId={second} />;
    case "working-hours": return <WorkingHoursPage roomId={second} />;
    case "templates": return <TemplatesPage />;
    case "bulk": return <BulkPage />;
    case "validation": return <ValidationPage />;
    case "history": return <HistoryPage auditId={second} />;
    case "connections": return <ConnectionsPage />;
    case "settings": return <SettingsPage />;
    default: return <DashboardPage />;
  }
}

export function App() {
  const { session } = useSession();
  const route = useRoute();
  // A language change remounts the shell so every label, including memoized tables, is rendered in the new language.
  const language = useLanguage();
  if (!session) return <LoadingScreen />;

  const first = route.segments[0];
  const sessionFree = first === "connections" || first === "settings";
  const content = session.mode === "None" && !sessionFree ? <WelcomePage /> : routeElement(route.segments);
  return (
    <AppShell key={language}>
      <Suspense fallback={<PageFallback />}>{content}</Suspense>
    </AppShell>
  );
}
