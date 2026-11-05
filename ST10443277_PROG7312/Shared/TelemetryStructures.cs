using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartX.Contracts
{
    /*Represents a single telemetry packet.
    A generic type allows one packet model to support different telemetry
    payload types while retaining the concrete type supplied by the caller
    (Nakov et al., 2013).
    */
    public class TelemetryPacket<T> where T : struct
    {
        public string Id { get; set; } = "";
        public string SensorId { get; set; } = "";
        public string Location { get; set; } = "";
        public string Timestamp { get; set; } = "";

        // Current telemetry payloads use C# value types such as float,
        // int and bool, so T is restricted to value types (Jamro, 2018).
        public T? Value { get; set; }
    }

    // Stores historical packets for each supported telemetry category.
    public class PacketHistory
    {
        public List<TelemetryPacket<float>> Temperature { get; set; }
            = new List<TelemetryPacket<float>>();

        public List<TelemetryPacket<int>> Power { get; set; }
            = new List<TelemetryPacket<int>>();

        public List<TelemetryPacket<bool>> Actuator { get; set; }
            = new List<TelemetryPacket<bool>>();

        // Projects strongly typed telemetry packets into the common numeric
        // representation required by the charting layer.
        public IEnumerable<TelemetryReading> ChartReadings()
        {
            foreach (var p in Temperature)
            {
                yield return Project(
                    p.Id,
                    p.SensorId,
                    p.Location,
                    p.Timestamp,
                    "Temperature",
                    Required(p.Value));
            }

            foreach (var p in Power)
            {
                yield return Project(
                    p.Id,
                    p.SensorId,
                    p.Location,
                    p.Timestamp,
                    "Power",
                    Required(p.Value));
            }

            // Actuator state is converted to 1 or 0 only for charting.
            // The stored packet retains its original Boolean payload.
            foreach (var p in Actuator)
            {
                yield return Project(
                    p.Id,
                    p.SensorId,
                    p.Location,
                    p.Timestamp,
                    "Actuator",
                    Required(p.Value) ? 1 : 0);
            }
        }

        private static T Required<T>(T? value) where T : struct =>
            value ?? throw new InvalidOperationException(
                "A stored packet has no payload.");

        private static TelemetryReading Project(
            string id,
            string sensor,
            string location,
            string timestamp,
            string category,
            double value)
        {
            return new TelemetryReading
            {
                Id = id,
                SensorId = sensor,
                Location = location,
                Timestamp = timestamp,
                Category = category,
                Value = value
            };
        }
    }

    // Provides reusable batching operations for telemetry packets.
    public static class TelemetryBatches<T> where T : struct
    {
    
        /* 
        Splits telemetry into batches.
        A jagged array is appropriate because its inner arrays can have
        different lengths, allowing the final batch to be shorter without
        padding unused elements (Nakov et al., 2013).
        */
    
        public static TelemetryPacket<T>[][] Stage(
            IEnumerable<TelemetryPacket<T>> source,
            int batchSize = 256)
        {
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize));

            var packets = source.ToArray();

            var batches =
                new TelemetryPacket<T>[
                    (packets.Length + batchSize - 1) / batchSize
                ][];

            for (int i = 0; i < batches.Length; i++)
            {
                int length = Math.Min(
                    batchSize,
                    packets.Length - i * batchSize);

                batches[i] = new TelemetryPacket<T>[length];

                Array.Copy(
                    packets,
                    i * batchSize,
                    batches[i],
                    0,
                    length);
            }

            return batches;
        }

    
        // Reconstructs a single history list from the staged batches.
    
        public static List<TelemetryPacket<T>> ToHistory(
            TelemetryPacket<T>[][] batches)
        {
            var history =
                new List<TelemetryPacket<T>>(
                    batches.Sum(batch => batch.Length));

            foreach (var batch in batches)
                history.AddRange(batch);

            return history;
        }
    }


    /* Represents a power measurement in watts.
    PowerReading is modelled as a struct because it represents a
    value-oriented type; structs form part of the C# value-type category
    (Jamro, 2018).*/

    public struct PowerReading
    {
        public long Watts { get; }

        public PowerReading(long watts)
        {
            Watts = watts;
        }

        // checked prevents arithmetic overflow from being accepted silently.
        public static PowerReading operator +(
            PowerReading a,
            PowerReading b) =>
            new PowerReading(checked(a.Watts + b.Watts));

        public static PowerReading operator -(
            PowerReading a,
            PowerReading b) =>
            new PowerReading(checked(a.Watts - b.Watts));

        public static bool operator >(
            PowerReading a,
            PowerReading b) =>
            a.Watts > b.Watts;

        public static bool operator <(
            PowerReading a,
            PowerReading b) =>
            a.Watts < b.Watts;
    }
}

// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
