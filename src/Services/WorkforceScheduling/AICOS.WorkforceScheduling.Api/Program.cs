using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.WorkforceScheduling.Api;
using AICOS.WorkforceScheduling.Api.Features.Attendance;
using AICOS.WorkforceScheduling.Api.Features.CashClosing;
using AICOS.WorkforceScheduling.Api.Features.Employees;
using AICOS.WorkforceScheduling.Api.Features.Payroll;
using AICOS.WorkforceScheduling.Api.Features.Replacements;
using AICOS.WorkforceScheduling.Api.Features.Scheduling;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosServiceDefaults(WorkforceSchedulingService.Info);
builder.AddAicosPostgres<WorkforceSchedulingDbContext>(WorkforceSchedulingService.Info);
builder.AddAicosEventBus();

builder.Services.AddSingleton<ShopClock>();
builder.Services.AddAicosBranchDirectory(builder.Configuration);   // asks Identity whether a branch belongs to the brand
builder.Services.AddAicosUserDirectory(builder.Configuration);     // asks Identity about the account behind an employee profile

// Cash taken by cashiers, recorded by the order / payment flow: the base of the expected amount of a cash closing.
builder.Services.AddIntegrationEventHandler<CashPaymentRecordedEvent, CashPaymentRecordedHandler>();

var app = builder.Build();

await app.MigrateDatabaseAsync<WorkforceSchedulingDbContext>();
app.UseAicosServiceDefaults();

var api = app.MapAicosApiGroup();
api.MapEmployeeEndpoints();
api.MapScheduleEndpoints();
api.MapAttendanceEndpoints();
api.MapReplacementEndpoints();
api.MapPayrollEndpoints();
api.MapCashClosingEndpoints();

await app.RunAsync();
