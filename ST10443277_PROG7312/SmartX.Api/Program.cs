using SmartX.Api.Endpoints;
using SmartX.Api.Stores;
using SmartX.Api.Validation;
using SmartX.Contracts;

var builder = WebApplication.CreateBuilder(args);

// The API is composed from small endpoint groups and singleton stores. This
// keeps routing, validation and persistence responsibilities separate, which
// supports the modular, example-driven implementation approach demonstrated in
// the supplied tutorial sources (YouTube, [s.a.]-a; YouTube, [s.a.]-b).
// Sources: https://youtu.be/A3ZUpyrnCbM; https://youtu.be/R94JHIXdTk0
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Smart-X IoT Gateway API",
        Version = "v1",
        Description = "Telemetry, sensor registration, device commands and predictive insights for the Smart-X gateway."
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    // Preserve the legacy reading endpoint's numeric-string compatibility while requiring
    // actual JSON numbers for the new float/int packet payloads (bool is already strict).
    var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
    resolver.Modifiers.Add(info =>
    {
        if (info.Type.IsGenericType && info.Type.GetGenericTypeDefinition() == typeof(TelemetryPacket<>) &&
            info.Type.GenericTypeArguments[0] != typeof(bool))
            info.Properties.First(p => p.Name == "Value").NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    });
    options.SerializerOptions.TypeInfoResolver = resolver;
});

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<SensorRegistrationStore>();
builder.Services.AddSingleton<PacketStore>();
builder.Services.AddSingleton<AttachmentStore>();
builder.Services.AddSingleton<CommandStore>();
builder.Services.AddSingleton<PredictiveActionEngine>();
builder.Services.AddSingleton<MeshStore>();

var app = builder.Build();

app.UseExceptionHandler();

app.UseSwagger();

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint(
        "/swagger/v1/swagger.json",
        "Smart-X IoT Gateway API v1");

    options.RoutePrefix = "swagger";
});

// Load persisted readings on startup; a corrupt data file must not silently reset history.
app.Services.GetRequiredService<TelemetryStore>();
app.Services.GetRequiredService<SensorRegistrationStore>();
app.Services.GetRequiredService<PacketStore>();
app.Services.GetRequiredService<AttachmentStore>();
app.Services.GetRequiredService<CommandStore>();
app.Services.GetRequiredService<PredictiveActionEngine>();
app.MapProfiles();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapGet("/api/commands", (CommandStore store) => Results.Ok(store.Read()));
app.MapGet("/api/device-states", (TelemetryStore store) => Results.Ok(store.ReadDeviceStates()));
app.MapGet("/api/device-dictionary", (TelemetryStore store) => Results.Ok(store.ReadDeviceDictionary()));
app.MapGet("/api/device-alerts", (TelemetryStore store) => Results.Ok(store.ReadActiveErrorStates()));
app.MapGet("/api/mesh", (string? sensorId, MeshStore store) => Results.Ok(store.Read(sensorId)));

app.MapPost("/api/insights/search", (SearchActivity activity, PredictiveActionEngine engine) =>
{
    engine.RecordSearch(activity);
    return Results.Accepted();
});

app.MapGet("/api/insights", (TelemetryStore telemetry, PredictiveActionEngine engine) =>
    Results.Ok(engine.Analyse(telemetry.ReadDeviceStates())));

app.MapPost("/api/commands", (SubmitCommandRequest request, CommandStore store, PredictiveActionEngine engine) =>
{
    // A command is validated at the API boundary before it reaches the queue.
    // This prevents invalid operator input from becoming persistent history.
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.DeviceId)) errors["DeviceId"] = ["A target device is required."];
    if (string.IsNullOrWhiteSpace(request.Command)) errors["Command"] = ["A command is required."];
    if (request.Command?.Length > 80) errors["Command"] = ["Command must be 80 characters or fewer."];
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var command = store.Add(request);
    engine.RecordCommand(command);
    return Results.Created("/api/commands", command);
});

app.MapPost("/api/commands/{id}/acknowledge", (string id, CommandStore store) =>
{
    var command = store.Acknowledge(id);
    return command is null ? Results.NotFound() : Results.Ok(command);
});

app.MapPost("/api/commands/undo", (CommandStore store, PredictiveActionEngine engine) =>
{
    var command = store.UndoLast();
    if (command is not null) engine.RecordCommand(command);
    return command is null ? Results.NoContent() : Results.Created("/api/commands", command);
});

app.MapGet("/api/telemetry/timeline", (int? limit, TelemetryStore store) =>
{
    if (limit.HasValue && (limit.Value < 1 || limit.Value > TelemetryStore.Capacity))
        return Results.BadRequest(new { Error = "limit must be between 1 and 10000." });
    var timeline = store.ReadTimeline();
    return Results.Ok(limit.HasValue ? timeline.TakeLast(limit.Value).ToArray() : timeline);
});
app.MapGet("/api/telemetry/timeline/summary", (TelemetryStore store) => Results.Ok(store.ReadTimelineSummary()));

app.MapGet("/api/sensors", (SensorRegistrationStore store) => Results.Ok(store.Read()));

app.MapGet("/api/sensors/{id}", (string id, SensorRegistrationStore store) =>
{
    var sensor = store.Read().FirstOrDefault(s => s.Id == id);
    return sensor is null ? Results.NotFound() : Results.Ok(sensor);
});

app.MapPost("/api/sensors", (RegisterSensorRequest request, SensorRegistrationStore store) =>
{
    var errors = SensorRegistrationRules.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try
    {
        var sensor = store.Add(request);
        return Results.Created($"/api/sensors/{sensor.Id}", sensor);
    }
    catch (InvalidOperationException error)
    {
        return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapPost("/api/telemetry", (SubmitTelemetryRequest request, TelemetryStore store) =>
{
    var errors = TelemetryValidation.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try
    {
        var reading = store.Add(request);
        return Results.Created($"/api/telemetry/{reading.Id}", reading);
    }
    catch (InvalidOperationException error)
    {
        return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapGet("/api/telemetry/{id}", (string id, TelemetryStore store) =>
{
    var reading = store.Read().FirstOrDefault(r => r.Id == id);
    return reading is null ? Results.NotFound() : Results.Ok(reading);
});

app.MapGet("/api/telemetry", (HttpRequest request, TelemetryStore store) =>
{
    var query = request.Query;
    var errors = new Dictionary<string, string[]>();
    var from = DateTimeOffset.MinValue;
    var to = DateTimeOffset.MaxValue;
    int limit = 1000;
    if (query.ContainsKey("from") && !TelemetryValidation.TryTimestamp(query["from"], out from))
        errors["from"] = ["Use an ISO 8601 timestamp with a UTC offset."];
    if (query.ContainsKey("to") && !TelemetryValidation.TryTimestamp(query["to"], out to))
        errors["to"] = ["Use an ISO 8601 timestamp with a UTC offset."];
    if (from > to) errors["from"] = ["from must be before or equal to to."];
    if (query.ContainsKey("limit") && (!int.TryParse(query["limit"], out limit) || limit < 1 || limit > TelemetryStore.Capacity))
        errors["limit"] = ["limit must be between 1 and 10000."];
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    // A sensor-specific time window is a historical query. Route it through
    // the per-sensor AVL index instead of scanning the retained list. The
    // general multi-sensor/filter view below remains available for dashboard
    // snapshots that do not identify one timestamp range.
    if (query.ContainsKey("sensorId") && query.ContainsKey("from") && query.ContainsKey("to"))
    {
        var indexed = store.ReadRange(query["sensorId"].ToString(), from, to);
        return Results.Ok(indexed.TakeLast(limit).ToArray());
    }

    var readings = store.Read().Where(r =>
        (!query.ContainsKey("sensorId") || r.SensorId == query["sensorId"].ToString()) &&
        (!query.ContainsKey("location") || r.Location == query["location"].ToString()) &&
        (!query.ContainsKey("category") || r.Category == query["category"].ToString()) &&
        DateTimeOffset.Parse(r.Timestamp) >= from && DateTimeOffset.Parse(r.Timestamp) <= to)
        .TakeLast(limit).ToArray();
    return Results.Ok(readings);
});

app.MapGet("/api/telemetry/range", (HttpRequest request, TelemetryStore store) =>
{
    string sensorId = request.Query["sensorId"].ToString();
    if (string.IsNullOrWhiteSpace(sensorId) ||
        !DateTimeOffset.TryParse(request.Query["from"], out var from) ||
        !DateTimeOffset.TryParse(request.Query["to"], out var to) || from > to)
        return Results.BadRequest(new { Error = "sensorId, from and to are required; from must be before to." });
    return Results.Ok(store.ReadRange(sensorId, from, to));
});

app.Run();
