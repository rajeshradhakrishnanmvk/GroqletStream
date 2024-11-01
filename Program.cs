
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var configuration = builder.Configuration;
builder.Services.Configure<GroqApiSettings>(configuration.GetSection("GroqApiSettings"));

builder.Services.AddCors(o => o.AddPolicy("Policy", builder => {
  builder.AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader();
}));

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

builder.Services.AddTransient<GroqApiSettings>((provider) =>
{
    var config = provider.GetService<IConfiguration>();
        return new GroqApiSettings()
    {
        ApiKey = config.GetValue<string>("GroqApiSettings:ApiKey")
    };

});

builder.Services.AddTransient<Groqlet>(provider =>
{
    var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
    var apiSettings = provider.GetRequiredService<GroqApiSettings>();
    var logger = provider.GetService<ILogger<Groqlet>>();
    return new Groqlet(httpClient, apiSettings.ApiKey, logger);
});


builder.Services.AddTransient<IGroqAgentService, GroqAgentService>();



var app = builder.Build();

app.UseStaticFiles();  // For the wwwroot folder

app.UseCors("Policy");

app.MapGet("/api/agent/ask", async (HttpContext httpContext) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    var query = httpContext.Request.Query["query"].ToString();

    if (string.IsNullOrWhiteSpace(query))
    {
        await httpContext.Response.WriteAsync("data: ERROR: Query parameter is required.\n\n");
        await httpContext.Response.Body.FlushAsync();
        return;
    }

    var agentService = httpContext.RequestServices.GetRequiredService<IGroqAgentService>() as GroqAgentService;
    var cancellationToken = httpContext.RequestAborted;

    try
    {
        // Iterate through the asynchronous enumerable returned by AskAgent
        int idx = 0;
        await foreach (var result in agentService.AskAgent(query, cancellationToken))
        {
            var sanitizedResult = result.Replace("\n", "||");
            //Console.WriteLine($"Batch: {idx}, Sending result: {sanitizedResult}");
            // Write each result to the response in the correct SSE format
            await httpContext.Response.WriteAsync($"data: {sanitizedResult}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
        // Indicate end of stream
        await httpContext.Response.WriteAsync("data: END||\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
    catch (Exception ex)
    {
        var errorMessage = ex.Message.Replace("\n", "||");
        // Handle errors by sending them as SSE messages and then flushing
        await httpContext.Response.WriteAsync($"data: ERROR: {ex.Message}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
});




app.Run();