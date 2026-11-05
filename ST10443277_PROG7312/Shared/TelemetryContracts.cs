namespace SmartX.Contracts
{
    // Defines the information submitted to the API for a telemetry reading.
    public class SubmitTelemetryRequest
    {
        public string SensorId { get; set; } = "";
        public string Location { get; set; } = "";
        public string Category { get; set; } = "";
        public double? Value { get; set; }

        // Kept as a string to preserve the wire format used between
        // SmartX components.
        public string Timestamp { get; set; } = "";
    }

    /* Represents a telemetry reading after it has been stored.
    Inheritance avoids duplicating the submitted telemetry properties:
    the stored reading reuses those properties and adds its generated Id.
    Inheritance is a fundamental object-oriented mechanism
    (Nakov et al., 2013).*/
    public class TelemetryReading : SubmitTelemetryRequest
    {
        public string Id { get; set; } = "";
    }
}

// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
