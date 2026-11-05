using Microsoft.AspNetCore.Http.Features;
using SmartX.Api.Stores;
using SmartX.Contracts;

namespace SmartX.Api.Endpoints;

public static class ProfileEndpoints
{
    // Maps the packet, deployment and attachment HTTP endpoints used by
    // the SmartX API.
    public static void MapProfiles(this WebApplication app)
    {
        app.MapGet(
            "/api/packets",
            (PacketStore store) =>
                Results.Ok(store.Read()));

        app.MapGet(
            "/api/packets/{kind}/{id}",
            (string kind, string id, PacketStore store) =>
            {
                var data = store.Read();

                return kind switch
                {
                    "temperature" => Find(data.Temperature, id),
                    "power" => Find(data.Power, id),
                    "actuator" => Find(data.Actuator, id),
                    _ => Results.NotFound()
                };
            });

        app.MapPost(
            "/api/packets/temperature",
            (
                TelemetryPacket<float> packet,
                PacketStore store,
                SensorRegistrationStore sensors) =>
                    Accept(
                        packet,
                        store,
                        sensors,
                        "Environmental",
                        "temperature",
                        v =>
                            float.IsFinite(v) &&
                            v >= -273.15f &&
                            v <= 10000,
                        "Temperature must be a finite float from -273.15 to 10000 °C.",
                        h => h.Temperature,
                        (h, p) => h.Temperature = p));

        app.MapPost(
            "/api/packets/power",
            (
                TelemetryPacket<int> packet,
                PacketStore store,
                SensorRegistrationStore sensors) =>
                    Accept(
                        packet,
                        store,
                        sensors,
                        "Power Consumption",
                        "power",
                        v => v >= 0,
                        "Power must be a non-negative 32-bit integer in watts.",
                        h => h.Power,
                        (h, p) => h.Power = p));

        app.MapPost(
            "/api/packets/actuator",
            (
                TelemetryPacket<bool> packet,
                PacketStore store,
                SensorRegistrationStore sensors) =>
                    Accept(
                        packet,
                        store,
                        sensors,
                        "Actuator",
                        "actuator",
                        _ => true,
                        "Actuator state must be true or false.",
                        h => h.Actuator,
                        (h, p) => h.Actuator = p));

        app.MapPost(
            "/api/sensors/{id}/deployment/validate",
            (
                string id,
                DeploymentNode tree,
                SensorRegistrationStore sensors) =>
            {
                var sensor = sensors.Read()
                    .FirstOrDefault(s => s.Id == id);

                if (sensor == null)
                    return Results.NotFound();

                return Results.Ok(
                    DeploymentValidator.Validate(
                        tree,
                        sensors.Read(),
                        sensor.DeviceId));
            });

        app.MapGet(
            "/api/sensors/{id}/attachments",
            (
                string id,
                SensorRegistrationStore sensors,
                AttachmentStore store) =>
                    sensors.Read().Any(s => s.Id == id)
                        ? Results.Ok(store.Read(id))
                        : Results.NotFound());

        app.MapGet(
            "/api/sensors/{id}/attachments/{attachmentId}",
            (
                string id,
                string attachmentId,
                AttachmentStore store,
                HttpResponse response) =>
            {
                var file = store.Read(id)
                    .FirstOrDefault(a => a.Id == attachmentId);

                if (file == null)
                    return Results.NotFound();

                // Prevent the browser from attempting to infer a different
                // content type for the downloaded binary attachment.
                response.Headers.XContentTypeOptions = "nosniff";

                return Results.File(
                    store.PathFor(file.Id),
                    "application/octet-stream",
                    file.FileName);
            });

        app.MapPost(
            "/api/sensors/{id}/attachments",
            async (
                string id,
                string kind,
                HttpRequest request,
                SensorRegistrationStore sensors,
                AttachmentStore store,
                CancellationToken cancellation) =>
            {
                var sensor = sensors.Read()
                    .FirstOrDefault(s => s.Id == id);

                if (sensor == null)
                    return Results.NotFound();

                var feature =
                    request.HttpContext.Features
                        .Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize =
                        AttachmentStore.MaxFileBytes + 65536;
                }

                try
                {
                    var file = await store.UploadAsync(
                        request,
                        sensor,
                        sensors,
                        kind,
                        cancellation);

                    return Results.Created(
                        $"/api/sensors/{id}/attachments/{file.Id}",
                        file);
                }
                catch (UploadRejectedException error)
                {
                    return Results.Problem(
                        error.Message,
                        statusCode: error.Status);
                }
                catch (InvalidDataException)
                {
                    return Results.Problem(
                        "Invalid multipart upload.",
                        statusCode: 400);
                }
            });
    }

    // Searches a typed packet collection for a packet identifier.

    private static IResult Find<T>(
        List<TelemetryPacket<T>> packets,
        string id)
        where T : struct
    {
        var packet = packets.FirstOrDefault(p => p.Id == id);

        return packet == null
            ? Results.NotFound()
            : Results.Ok(packet);
    }

    /* Provides one generic packet-submission algorithm for all supported payload types.
     Generic delegates allow validation, channel selection and channel
     replacement behaviour to be supplied to the method without duplicating
     the entire submission process for each packet type
     (Nakov et al., 2013).*/

    private static IResult Accept<T>(
        TelemetryPacket<T> packet,
        PacketStore store,
        SensorRegistrationStore sensors,
        string category,
        string kind,
        Func<T, bool> valid,
        string range,
        Func<PacketHistory, List<TelemetryPacket<T>>> select,
        Action<PacketHistory, List<TelemetryPacket<T>>> replace)
        where T : struct
    {
        var errors = PacketStore.Validate(
            packet,
            sensors,
            category,
            valid,
            range);

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        try
        {
            var saved = store.Add(
                packet,
                select,
                replace);

            return Results.Created(
                $"/api/packets/{kind}/{saved.Id}",
                saved);
        }
        catch (InvalidOperationException error)
        {
            return Results.Problem(
                error.Message,
                statusCode: 409);
        }
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 

