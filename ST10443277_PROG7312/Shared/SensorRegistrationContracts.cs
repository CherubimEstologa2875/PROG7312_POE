using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartX.Contracts
{
    // Defines the information required to register a sensor.
    public class RegisterSensorRequest
    {
        public string DeviceId { get; set; } = "";
        public string Location { get; set; } = "";
        public string Category { get; set; } = "";
    }

    // Represents a sensor after successful registration.
    public class SensorRegistration : RegisterSensorRequest
    {
        public string Id { get; set; } = "";
        public string RegisteredAt { get; set; } = "";
    }


    // Centralises sensor-registration validation and normalisation.

    public static class SensorRegistrationRules
    {
        public static string[] Categories =>
            new[]
            {
                "Environmental",
                "Power Consumption",
                "Actuator"
            };

    
        /* Validates the request and returns errors associated with each
        invalid field.
        A dictionary is used because validation messages are associated
        with named fields through key-value pairs (Nakov et al., 2013).*/
    
        public static Dictionary<string, string[]> Validate(
            RegisterSensorRequest request)
        {
            var errors = new Dictionary<string, string[]>();

            // Normalise surrounding whitespace before applying validation.
            request.DeviceId = (request.DeviceId ?? "").Trim();
            request.Location = (request.Location ?? "").Trim();
            request.Category = (request.Category ?? "").Trim();

            /* Device identifiers are validated against defined text patterns.
            Regular expressions support pattern matching and data validation
            (Nakov et al., 2013).*/
            if (Regex.IsMatch(
                request.DeviceId,
                @"\A(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}|(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2})\z"))
            {
                string hex = request.DeviceId
                    .Replace(":", "")
                    .Replace("-", "")
                    .ToUpperInvariant();

                // Convert accepted MAC-address variants to one canonical form.
                request.DeviceId = string.Join(
                    ":",
                    Enumerable.Range(0, 6)
                        .Select(i => hex.Substring(i * 2, 2)));
            }
            else if (!Regex.IsMatch(
                request.DeviceId,
                @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
            {
                errors["DeviceId"] = new[]
                {
                    "Enter a valid six-pair MAC address or a unique ID of " +
                    "1–64 letters, digits, dots, hyphens or underscores, " +
                    "starting with a letter or digit."
                };
            }

            if (request.Location.Length == 0 ||
                request.Location.Length > 80 ||
                request.Location.Any(char.IsControl))
            {
                errors["Location"] = new[]
                {
                    "Enter a deployment location of 1–80 characters " +
                    "without control characters."
                };
            }

            // Resolve case-insensitive user input to the canonical category.
            var category = Categories.FirstOrDefault(
                c => c.Equals(
                    request.Category,
                    StringComparison.OrdinalIgnoreCase));

            if (category == null)
            {
                errors["Category"] = new[]
                {
                    "Select Environmental, Power Consumption or Actuator."
                };
            }
            else
            {
                request.Category = category;
            }

            return errors;
        }
    }
}


// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
