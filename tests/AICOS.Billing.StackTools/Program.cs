using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using AICOS.KitchenOperations.Api.Features.Barista;
using System.Text.Json;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;
using AICOS.TableReservation.Api.Persistence;
using AICOS.Ordering.Api.Persistence;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Payments.Api.Persistence;
using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using AICOS.ReportingAnalytics.Api.Persistence;
using AICOS.WorkforceScheduling.Api.Persistence;

static DbContextOptions<T> Options<T>(string schema,string connection) where T:DbContext => new DbContextOptionsBuilder<T>().UseNpgsql(connection,p=>p.MigrationsHistoryTable("__ef_migrations_history",schema)).UseSnakeCaseNamingConvention().Options;
if (await ItemTransferDesignProbe.TryRunAsync(args)) return;
if (await AICOS.Billing.StackTools.ApprovedCollectionRecovery.TryRunAsync(args)) return;
if (await AICOS.Billing.StackTools.CollectionRecoveryProbe.TryRunAsync(args)) return;
const string offline="Host=127.0.0.1;Port=1;Database=offline;Username=offline;Timeout=1";
static object[] MigrationColumns(DbContext db,string id)
{
    var assembly=db.GetService<IMigrationsAssembly>();
    var migration=assembly.CreateMigration(assembly.Migrations[id],db.Database.ProviderName!);
    var model=db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel,designTime:true);
    return model.GetRelationalModel().Tables.SelectMany(t=>t.Columns.Select(c=>(object)new{table=t.Name,name=c.Name,type=c.StoreType,nullable=c.IsNullable})).ToArray();
}
if(args is ["runtime-compatibility-scripts",var compatibilityPath])
{
    Directory.CreateDirectory(compatibilityPath);
    using var menu=new MenuCatalogDbContext(Options<MenuCatalogDbContext>("menu",offline));
    using var customer=new CustomerEngagementDbContext(Options<CustomerEngagementDbContext>("customer",offline));
    var targets=new List<object>();
    foreach(var (schema,db,from,to) in new (string,DbContext,string,string)[]{("menu",menu,"20261021102711_ConstraintsAndIndexes","20261113170918_ProductPictureUpload"),("customer",customer,"20261101183341_StaffNotifications","20261113175515_HuntVouchers")})
    {
        var migrator=db.GetService<IMigrator>();
        File.WriteAllText(Path.Combine(compatibilityPath,schema+".baseline.sql"),migrator.GenerateScript("0",from));
        File.WriteAllText(Path.Combine(compatibilityPath,schema+".up.sql"),migrator.GenerateScript(from,to,MigrationsSqlGenerationOptions.NoTransactions));
        File.WriteAllText(Path.Combine(compatibilityPath,schema+".down.sql"),migrator.GenerateScript(to,from,MigrationsSqlGenerationOptions.NoTransactions));
        targets.Add(new{schema,from,to,baseline=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)<=0).ToArray(),pending=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)>0&&string.CompareOrdinal(x,to)<=0).ToArray(),columns=MigrationColumns(db,to)});
    }
    File.WriteAllText(Path.Combine(compatibilityPath,"targets.json"),JsonSerializer.Serialize(targets));
    Console.WriteLine("Menu/Customer compatibility deltas exported offline; existing migrations unchanged.");return;
}
if(args is ["dashboard-permissions-scripts",var permissionsPath])
{
    Directory.CreateDirectory(permissionsPath);
    using var order=new OrderingDbContext(Options<OrderingDbContext>("ordering",offline));
    using var kitchen=new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>("kitchen",offline));
    using var payment=new PaymentsDbContext(Options<PaymentsDbContext>("payment",offline));
    using var reporting=new ReportingAnalyticsDbContext(Options<ReportingAnalyticsDbContext>("reporting",offline));
    foreach(var (schema,db,to) in new (string,DbContext,string)[]{("ordering",order,"20261114164841_DurableTableTransfer"),("kitchen",kitchen,"20261114164849_TableTransferRoutingVersion"),("payment",payment,"20261115093913_RefundToBankAccount"),("reporting",reporting,"20261114125038_ConfirmedCollectionInbox")})
        File.WriteAllText(Path.Combine(permissionsPath,schema+".sql"),db.GetService<IMigrator>().GenerateScript("0",to));
    Console.WriteLine("Four reviewed ACL test baselines exported offline; no database connection.");return;
}
if(args is ["dashboard-upstream-scripts",var auditPath,var upstreamPath])
{
    Directory.CreateDirectory(upstreamPath);
    var audit=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(auditPath));
    var result=new List<object>();
    using var identity=new IdentityOrganizationDbContext(Options<IdentityOrganizationDbContext>("identity",offline));
    using var payment=new PaymentsDbContext(Options<PaymentsDbContext>("payment",offline));
    using var workforce=new WorkforceSchedulingDbContext(Options<WorkforceSchedulingDbContext>("workforce",offline));
    foreach(var (schema,db,to) in new (string,DbContext,string)[]{("identity",identity,"20261115110405_TenantPaymentAndSoftDelete"),("payment",payment,"20261115093913_RefundToBankAccount"),("workforce",workforce,"20261115162626_CashHandoverAtShiftStart")})
    {
        var target=audit.GetProperty("Targets").EnumerateArray().Single(x=>x.GetProperty("schema").GetString()==schema);
        var from=schema=="payment"?"20261114164856_BillSplitPlans":target.GetProperty("history").EnumerateArray().Last().GetString()!;
        File.WriteAllText(Path.Combine(upstreamPath,schema+".baseline.sql"),db.GetService<IMigrator>().GenerateScript("0",from));
        File.WriteAllText(Path.Combine(upstreamPath,schema+".up.sql"),db.GetService<IMigrator>().GenerateScript(from,to,MigrationsSqlGenerationOptions.NoTransactions));
        // Upstream refund snapshot predates local Dashboard tables; retain both model column sets.
        result.Add(new{schema,from,to,baseline=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)<=0).ToArray(),pending=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)>0&&string.CompareOrdinal(x,to)<=0).ToArray(),columns=MigrationColumns(db,from).Concat(MigrationColumns(db,to)).GroupBy(c=>{var json=JsonSerializer.SerializeToElement(c);return (json.GetProperty("table").GetString(),json.GetProperty("name").GetString());}).Select(g=>g.Last()).ToArray()});
    }
    File.WriteAllText(Path.Combine(upstreamPath,"targets.json"),JsonSerializer.Serialize(result));Console.WriteLine("Upstream deltas exported offline; no database connection.");return;
}
if(args is ["local-dashboard-scripts",var dashboardPath])
{
    Directory.CreateDirectory(dashboardPath);
    var targets = new (string Schema, DbContext Db, string From, string To)[]
    {
        ("ordering",new OrderingDbContext(Options<OrderingDbContext>("ordering",offline)),"20261101173630_TableBillRequest","20261114164841_DurableTableTransfer"),
        ("kitchen",new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>("kitchen",offline)),"20261114041318_KitchenBaristaWorkflow","20261114164849_TableTransferRoutingVersion"),
        ("payment",new PaymentsDbContext(Options<PaymentsDbContext>("payment",offline)),"20261021102642_ConstraintsAndIndexes","20261114164856_BillSplitPlans"),
        ("reporting",new ReportingAnalyticsDbContext(Options<ReportingAnalyticsDbContext>("reporting",offline)),"20261021151155_OrderFacts","20261114125038_ConfirmedCollectionInbox")
    };
    var description = new List<object>();
    foreach(var (schema,db,from,to) in targets)
    using(db)
    {
        var migrator=db.GetService<IMigrator>();
        File.WriteAllText(Path.Combine(dashboardPath,schema+".up.sql"),migrator.GenerateScript(from,to,MigrationsSqlGenerationOptions.NoTransactions));
        File.WriteAllText(Path.Combine(dashboardPath,schema+".down.sql"),migrator.GenerateScript(to,from,MigrationsSqlGenerationOptions.NoTransactions));
        description.Add(new{schema,from,to,baseline=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)<=0).ToArray(),pending=db.Database.GetMigrations().Where(x=>string.CompareOrdinal(x,from)>0&&string.CompareOrdinal(x,to)<=0).ToArray(),columns=MigrationColumns(db,to)});
    }
    File.WriteAllText(Path.Combine(dashboardPath,"targets.json"),JsonSerializer.Serialize(description));
    Console.WriteLine("Four offline Dashboard deltas exported; no DB connection or V2 migration.");return;
}
if(args is ["scripts",var path])
{
    Directory.CreateDirectory(path);
    using var identity=new IdentityOrganizationDbContext(Options<IdentityOrganizationDbContext>("identity",offline));
    using var menu=new MenuCatalogDbContext(Options<MenuCatalogDbContext>("menu",offline));
    using var table=new TableReservationDbContext(Options<TableReservationDbContext>("table_reservation",offline));
    using var order=new OrderingDbContext(Options<OrderingDbContext>("ordering",offline));
    using var kitchen=new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>("kitchen",offline));
    using var payment=new PaymentsDbContext(Options<PaymentsDbContext>("payment",offline));
    using var reporting=new ReportingAnalyticsDbContext(Options<ReportingAnalyticsDbContext>("reporting",offline));
    using var workforce=new WorkforceSchedulingDbContext(Options<WorkforceSchedulingDbContext>("workforce",offline));
    using var customer=new CustomerEngagementDbContext(Options<CustomerEngagementDbContext>("customer",offline));
    var models=new Dictionary<string,object>();
    foreach(var (name,db,to) in new (string,DbContext,string?)[]{("reporting",reporting,null),("workforce",workforce,null),("identity",identity,null),("customer",customer,null),("menu",menu,null),("table_reservation",table,null),("ordering",order,"20261106183340_OrderingCupFulfillmentFoundation"),("kitchen",kitchen,null),("payment",payment,"20261021102642_ConstraintsAndIndexes")})
    {
        File.WriteAllText(Path.Combine(path,name+".sql"),db.GetService<IMigrator>().GenerateScript("0",to));
        if (name is "ordering" or "payment")
            File.WriteAllText(Path.Combine(path,name+".post.sql"),db.GetService<IMigrator>().GenerateScript(name=="ordering"?"20261113125751_OrderingSessionCheckout":"20261113125804_PaymentSessionBills",null));
        models[name]=db.Model.GetRelationalModel().Tables.SelectMany(t=>t.Columns.Select(c=>new{table=t.Name,name=c.Name,type=c.StoreType,nullable=c.IsNullable})).ToArray();
    }
    File.WriteAllText(Path.Combine(path,"kitchen.barista.sql"),kitchen.GetService<IMigrator>().GenerateScript("20261108121518_KitchenEarlyPayments","20261114041318_KitchenBaristaWorkflow"));
    File.WriteAllText(Path.Combine(path,"kitchen.barista.down.sql"),kitchen.GetService<IMigrator>().GenerateScript("20261114041318_KitchenBaristaWorkflow","20261108121518_KitchenEarlyPayments"));
    File.WriteAllText(Path.Combine(path,"kitchen.pre-barista.sql"),kitchen.GetService<IMigrator>().GenerateScript("0","20261108121518_KitchenEarlyPayments"));
    File.WriteAllText(Path.Combine(path,"payment.cashier.sql"),payment.GetService<IMigrator>().GenerateScript("20261113125804_PaymentSessionBills","20261114125030_CashierMoneyShifts"));
    File.WriteAllText(Path.Combine(path,"payment.cashier.down.sql"),payment.GetService<IMigrator>().GenerateScript("20261114125030_CashierMoneyShifts","20261113125804_PaymentSessionBills"));
    File.WriteAllText(Path.Combine(path,"reporting.cashier.sql"),reporting.GetService<IMigrator>().GenerateScript("20261021151155_OrderFacts","20261114125038_ConfirmedCollectionInbox"));
    File.WriteAllText(Path.Combine(path,"reporting.cashier.down.sql"),reporting.GetService<IMigrator>().GenerateScript("20261114125038_ConfirmedCollectionInbox","20261021151155_OrderFacts"));
    File.WriteAllText(Path.Combine(path,"ordering.operations.sql"),order.GetService<IMigrator>().GenerateScript("20261113125751_OrderingSessionCheckout","20261114164841_DurableTableTransfer"));
    File.WriteAllText(Path.Combine(path,"ordering.operations.down.sql"),order.GetService<IMigrator>().GenerateScript("20261114164841_DurableTableTransfer","20261113125751_OrderingSessionCheckout"));
    File.WriteAllText(Path.Combine(path,"payment.operations.sql"),payment.GetService<IMigrator>().GenerateScript("20261114125030_CashierMoneyShifts","20261114164856_BillSplitPlans"));
    File.WriteAllText(Path.Combine(path,"payment.operations.down.sql"),payment.GetService<IMigrator>().GenerateScript("20261114164856_BillSplitPlans","20261114125030_CashierMoneyShifts"));
    File.WriteAllText(Path.Combine(path,"kitchen.operations.sql"),kitchen.GetService<IMigrator>().GenerateScript("20261114041318_KitchenBaristaWorkflow","20261114164849_TableTransferRoutingVersion"));
    File.WriteAllText(Path.Combine(path,"kitchen.operations.down.sql"),kitchen.GetService<IMigrator>().GenerateScript("20261114164849_TableTransferRoutingVersion","20261114041318_KitchenBaristaWorkflow"));
    File.WriteAllText(Path.Combine(path,"schema.json"),JsonSerializer.Serialize(models));
    Console.WriteLine("Offline baseline scripts generated; no database connection.");
    return;
}
if(args is ["test-printer"])
{
 var key=Environment.GetEnvironmentVariable("BARISTA_PRINT_TEST_KEY");if(key is not {Length:>=32})throw new InvalidOperationException("Isolated printer key required");
 var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://0.0.0.0:8080");var app=builder.Build();
 var jobs=new System.Collections.Concurrent.ConcurrentDictionary<Guid,string>();
 app.MapPost("/",async(HttpContext context)=>{
  var body=await context.Request.ReadFromJsonAsync<JsonElement>();var id=body.GetProperty("id").GetGuid();var hash=body.GetProperty("payloadHash").GetString()!;
  if(context.Request.Headers["X-Aicos-Print-Signature"]!=VerifiedPrintAgent.Sign(key,id,hash,"REQUEST"))return Results.StatusCode(403);
  var unknown=body.GetProperty("document").GetProperty("lines")[0].GetProperty("copies").GetInt32()==4;
  var status=jobs.GetOrAdd(id,unknown?"UNKNOWN":"TEST_PRINTED");
  return Results.Ok(new{jobId=id,status,signature=VerifiedPrintAgent.Sign(key,id,hash,status)});
 });
 await app.RunAsync();return;
}
if(args is ["health"])
{
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};
 foreach(var name in new[]{"identity","menu","table","order","kitchen","payment","reporting","workforce","gateway"})
 foreach(var endpoint in name=="gateway"?new[]{"/","/health/live"}:new[]{"/health/live","/health/ready","/swagger/v1/swagger.json"})
 {
  using var response=await http.GetAsync("http://"+name+":8080"+endpoint);
  if(!response.IsSuccessStatusCode)throw new InvalidOperationException(name+" "+endpoint+" "+(int)response.StatusCode);
 }
 Console.WriteLine("Health/live/ready/Swagger checks passed.");return;
}
if(args is not ["fixtures"]) throw new InvalidOperationException("Unknown test tool mode");
// Exact container DNS and cluster pin are supplied only by the disposable stack runner.
var connection=Environment.GetEnvironmentVariable("BILL_STACK_ADMIN_CONNECTION")??throw new InvalidOperationException("Missing test target");
var cs=new NpgsqlConnectionStringBuilder(connection);
if(cs.Host!="postgres" || cs.Username!="bill_stack_admin" || cs.Database!="aicos_billstack_identity") throw new InvalidOperationException("Test database guard failed");
await using(var sql=new NpgsqlConnection(connection))
{
 await sql.OpenAsync();await using var command=new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()",sql);
 if((string?)await command.ExecuteScalarAsync()!=Environment.GetEnvironmentVariable("BILL_STACK_CLUSTER")) throw new InvalidOperationException("Test cluster guard failed");
}
await using(var db=new IdentityOrganizationDbContext(Options<IdentityOrganizationDbContext>("identity",connection)))
{
 if(await db.Users.AnyAsync())throw new InvalidOperationException("Fixtures require empty test Identity");
 var admin=new User("Bill Test Admin","admin@billstack.invalid","admin",null,"SuperAdmin",null);
 admin.SetPassword(new PasswordService().Hash(admin,Environment.GetEnvironmentVariable("BILL_STACK_USER_PASSWORD")!),false);db.Users.Add(admin);await db.SaveChangesAsync();
}
cs.Database="aicos_billflow_test";
await using(var db=new PaymentsDbContext(Options<PaymentsDbContext>("payment",cs.ConnectionString)))
{
 if(await db.PaymentProviders.AnyAsync())throw new InvalidOperationException("Fixtures require empty test Payments");
 var mock=new PaymentProvider(PaymentProvider.MockCode,"Isolated test only",ProviderMode.Mock);mock.SetEnabled(true);db.PaymentProviders.Add(mock);await db.SaveChangesAsync();
}
cs.Database="aicos_billstack_ordering";
await using(var db=new OrderingDbContext(Options<OrderingDbContext>("ordering",cs.ConnectionString)))
{
 var sample=new AICOS.Ordering.Api.Domain.TableSession(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"BACKUP-PROBE",DateTime.UtcNow);
 sample.Close(AICOS.Ordering.Api.Domain.TableSessionCloseReason.Staff,DateTime.UtcNow);db.TableSessions.Add(sample);await db.SaveChangesAsync();
}
Console.WriteLine("Minimal isolated Identity/Mock and historical session fixtures created; credentials not displayed.");
