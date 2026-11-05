using System;
using System.Collections.Generic;

namespace SmartX.Contracts
{
    public class SubmitCommandRequest
    {
        public string DeviceId { get; set; } = "";
        public string Command { get; set; } = "";
        public string Parameters { get; set; } = "";
    }

    public class DeviceCommand : SubmitCommandRequest
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "Queued";
        public string CreatedAt { get; set; } = "";
        public string CompletedAt { get; set; } = "";
        public string Source { get; set; } = "Manual";
        public string RevertedCommandId { get; set; } = "";
    }

    public class CommandHistory
    {
        public List<DeviceCommand> Items { get; set; } = new List<DeviceCommand>();
        public int Pending { get; set; }
    }

    public class DeviceState
    {
        public string DeviceId { get; set; } = "";
        public string Location { get; set; } = "";
        public string Category { get; set; } = "";
        public double Value { get; set; }
        public string LastSeen { get; set; } = "";
        public bool Online { get; set; }
        public bool ActiveAlert { get; set; }
    }
}
