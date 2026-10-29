using System.Reflection;
using GameOfLife.Api;
using GameOfLife.Core;
using GameOfLife.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Kestrel reads only its endpoints from configuration, so limits such as the maximum
// request body size are bound here.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    context.Configuration.GetSection("Kestrel:Limits").Bind(kestrel.Limits));

builder.Services.AddOptions<GameOfLifeOptions>()
    .Bind(builder.Configuration.GetSection(GameOfLifeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ILifeEngine, LifeEngine>();
builder.Services.AddSingleton<LifeSimulator>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddBoardStorage(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BoardExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
    swagger.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml")));

var app = builder.Build();

app.Services.InitializeBoardStorage();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// The browser UI in wwwroot, served from the same origin as the API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>.
public partial class Program;
