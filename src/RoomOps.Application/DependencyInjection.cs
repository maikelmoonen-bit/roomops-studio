using Microsoft.Extensions.DependencyInjection;
using RoomOps.Application.Execution;
using RoomOps.Application.Gateways;
using RoomOps.Application.Planning;
using RoomOps.Application.Services;

namespace RoomOps.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRoomOpsApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ExchangeGateway>();
        services.AddSingleton<PlacesGateway>();
        services.AddSingleton<GraphGateway>();
        services.AddSingleton<InventoryService>();
        services.AddSingleton<ValidationService>();
        services.AddSingleton<RoomsService>();
        services.AddSingleton<BookingService>();
        services.AddSingleton<DelegatesService>();
        services.AddSingleton<WorkingHoursService>();
        services.AddSingleton<RoomListsService>();
        services.AddSingleton<PlacesService>();
        services.AddSingleton<TeamsRoomsService>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<TemplateService>();
        services.AddSingleton<BulkImportService>();
        services.AddSingleton<ChangePlanner>();
        services.AddSingleton<PlanRegistry>();
        services.AddSingleton<ExecutionEngine>();
        services.AddSingleton<PlanService>();
        return services;
    }
}
