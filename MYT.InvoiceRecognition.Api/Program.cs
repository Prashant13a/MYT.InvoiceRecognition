using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using MYT.InvoiceRecognition.Api.Options;
using MYT.InvoiceRecognition.Api.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureDocumentIntelligenceOptions>(builder.Configuration.GetSection("AzureDocumentIntelligence"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.Configure<LimitsOptions>(builder.Configuration.GetSection("Limits"));
builder.Services.Configure<FormOptions>(options => { options.MultipartBodyLengthLimit = 22000000; });
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "MYT Invoice Parser API", Version = "v1" });
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-API-Key", Description = "Enter the configured internal API key, if enabled." });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }, Array.Empty<string>() }
    });
});
builder.Services.AddHttpClient<AzureDocumentService>(http => http.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<OpenAiService>(http => http.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddScoped<InvoiceRecognitionService>();
builder.Services.AddScoped<SpreadsheetService>();
builder.Services.AddScoped<BankReconciliationService>();

var app = builder.Build();
app.UseSwagger();
app.MapGet("/openapi.json", () => Results.Redirect("/swagger/v1/swagger.json")).ExcludeFromDescription();
app.UseSwaggerUI(options => { options.SwaggerEndpoint("/openapi.json", "MYT Invoice Parser API"); options.RoutePrefix = "docs"; });
app.MapGet("/redoc", () => Results.Content("""
<!DOCTYPE html><html><head><meta charset="utf-8"><title>MYT Invoice Parser - ReDoc</title></head>
<body><redoc spec-url="/openapi.json"></redoc><script src="https://cdn.redoc.ly/redoc/latest/bundles/redoc.standalone.js"></script></body></html>
""", "text/html")).ExcludeFromDescription();
app.Use(async (context, next) =>
{
    var required = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
    var protectedPath = context.Request.Path.StartsWithSegments("/parse_invoice") || context.Request.Path.StartsWithSegments("/agent_invoice") ||
        context.Request.Path.StartsWithSegments("/supplier_validation") || context.Request.Path.StartsWithSegments("/bank_statement");
    if (!string.IsNullOrWhiteSpace(required) && protectedPath)
    {
        var supplied = context.Request.Headers["X-API-Key"].ToString();
        var requiredBytes = SHA256.HashData(Encoding.UTF8.GetBytes(required));
        var suppliedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(requiredBytes, suppliedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Unauthorized", errors = "A valid X-API-Key header is required.", details = "Authentication failed", status = 401 });
            return;
        }
    }
    await next();
});
app.MapControllers();
app.Run();

public partial class Program { }
