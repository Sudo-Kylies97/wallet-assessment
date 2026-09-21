using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;
using Wallet.Api.Features;
using Wallet.Api.Http;
using Wallet.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<WalletDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Wallet")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<WithdrawalService>();
builder.Services.AddSingleton<IEventPublisher, RabbitMqPublisher>();
builder.Services.AddScoped<OutboxDispatcher>();
if (builder.Configuration.GetValue("Outbox:Enabled", true))
    builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict)
    .ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState)
    {
        Status = 400, Title = "invalid_request", Instance = context.HttpContext.Request.Path,
        Extensions = { ["code"] = "invalid_request", ["traceId"] = context.HttpContext.TraceIdentifier }
    }) { ContentTypes = { "application/problem+json" } };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Wallet API", Version = "v1",
        Description = "An assessment API with atomic withdrawals and durable events. Amounts are ZAR cents. " +
            "Seed wallet: 11111111-1111-1111-1111-111111111111. Use Try it out below; " +
            "reuse the idempotency key only when retrying the same withdrawal."
    });
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options => options.DocumentTitle = "Wallet API — Assessment");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<WalletDbContext>());

app.Run();

public partial class Program;
