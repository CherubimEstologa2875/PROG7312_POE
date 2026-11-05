using System.Globalization;
using System.Text.RegularExpressions;
using SmartX.Contracts;

namespace SmartX.Api.Validation;

public static class TelemetryValidation
{
    /* Normalises and validates a submitted telemetry request.
     Regular expressions are used where identifiers and timestamps must
     conform to defined textual patterns. Pattern matching and data
     validation are recognised applications of regular expressions
     (Nakov et al., 2013).*/
    public static Dictionary<string, string[]> Validate(
        SubmitTelemetryRequest request)
    {
        var errors =
            new Dictionary<string, string[]>();

        // Remove surrounding whitespace before performing validation.
        request.SensorId =
            request.SensorId?.Trim() ?? "";

        request.Location =
            request.Location?.Trim() ?? "";

        request.Category =
            request.Category?.Trim() ?? "";

        if (!Regex.IsMatch(
            request.SensorId,
            @"^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$"))
        {
            errors["SensorId"] =
                ["Use 1–40 letters, digits, hyphens or underscores, starting with a letter or digit."];
        }

        if (string.IsNullOrWhiteSpace(request.Location) ||
            request.Location.Length > 80 ||
            request.Location.Any(char.IsControl))
        {
            errors["Location"] =
                ["Enter a location of 1–80 characters without control characters."];
        }

        // Convert accepted category casing to one canonical representation.
        if (request.Category.Equals(
            "Temperature",
            StringComparison.OrdinalIgnoreCase))
        {
            request.Category = "Temperature";
        }
        else if (request.Category.Equals(
            "Humidity",
            StringComparison.OrdinalIgnoreCase))
        {
            request.Category = "Humidity";
        }
        else
        {
            errors["Category"] =
                ["Category must be Temperature or Humidity."];
        }

        if (!request.Value.HasValue ||
            !double.IsFinite(request.Value.Value))
        {
            errors["Value"] =
                ["A finite numeric value is required."];
        }
        else if (
            request.Category == "Humidity" &&
            (request.Value < 0 ||
             request.Value > 100))
        {
            errors["Value"] =
                ["Humidity must be between 0 and 100 percent."];
        }
        else if (
            request.Value < -273.15 ||
            request.Value > 10000)
        {
            errors["Value"] =
                ["Temperature must be between -273.15 and 10000 degrees Celsius."];
        }

        if (!TryTimestamp(
            request.Timestamp,
            out var time))
        {
            errors["Timestamp"] =
                ["Use an ISO 8601 timestamp with Z or an explicit UTC offset."];
        }
        else if (time > DateTimeOffset.UtcNow)
        {
            errors["Timestamp"] =
                ["Readings cannot be dated in the future."];
        }
        else
        {
            // Valid timestamps are converted to one UTC representation
            // before they are persisted or compared.
            request.Timestamp =
                time
                    .ToUniversalTime()
                    .ToString(
                        "O",
                        CultureInfo.InvariantCulture);
        }

        return errors;
    }

    // Checks that a timestamp has the expected textual structure and can
    // also be parsed as a DateTimeOffset.
    public static bool TryTimestamp(
        string? value,
        out DateTimeOffset time)
    {
        time = default;

        return value != null &&
            Regex.IsMatch(
                value,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") &&
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out time);
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
