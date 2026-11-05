using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartX.Contracts
{
    // Metadata shared by the attachment store, API and sensor profile UI.
    public class SensorAttachment
    {
        public string Id { get; set; } = "";
        public string SensorId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Kind { get; set; } = "";
        public long Size { get; set; }
        public string UploadedAt { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    /*Represents one node in the hierarchical sensor deployment.
     Each node can contain child nodes, forming the parent-child hierarchy
     characteristic of a tree structure (Nakov et al., 2013).*/
    public class DeploymentNode
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string SensorId { get; set; } = "";

        public List<DeploymentNode> Children { get; set; }
            = new List<DeploymentNode>();
    }


    // Contains the result of validating a deployment hierarchy.
  
    public class DeploymentValidationResult
    {
        public bool IsValid => Errors.Count == 0;

        public List<string> Errors { get; set; }
            = new List<string>();

        public List<string> Paths { get; set; }
            = new List<string>();
    }


    /* Validates a hierarchical sensor deployment.
     The deployment is processed recursively by visiting each node and then
     its children, following the general depth-first traversal approach used
     with tree structures (Nakov et al., 2013).*/
  
    public static class DeploymentValidator
    {
        public static DeploymentValidationResult Validate(
            DeploymentNode root,
            IEnumerable<SensorRegistration> sensors,
            string targetDeviceId)
        {
            var result = new DeploymentValidationResult();

            // Registered sensors are indexed by DeviceId because the
            // validation process repeatedly retrieves registrations by key.
            // Dictionaries model key-value associations (Nakov et al., 2013).
            var registered = sensors.ToDictionary(
                s => s.DeviceId,
                StringComparer.OrdinalIgnoreCase);


            // duplicate sensor assignments. HashSet<T> is designed for
            // collections of unique elements and membership checking
            // (Nakov et al., 2013).

            // Sets are used for state that must remain unique. 'visited'
            var visited = new HashSet<DeploymentNode>();

            // detects repeated node references, while 'assigned' detects
            var assigned = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            int count = 0;

            Visit(
                root,
                "",
                true,
                0,
                registered,
                visited,
                assigned,
                result,
                ref count);

            if (!assigned.Contains(targetDeviceId))
            {
                result.Errors.Add(
                    "The selected sensor must appear in the deployment tree.");
            }

            return result;
        }

    
        // Validates the current node and recursively continues through its
        // child nodes.
      
        private static void Visit(
            DeploymentNode node,
            string parent,
            bool ancestorsEnabled,
            int depth,
            Dictionary<string, SensorRegistration> sensors,
            HashSet<DeploymentNode> visited,
            HashSet<string> assigned,
            DeploymentValidationResult result,
            ref int count)
        {
            if (node == null)
            {
                result.Errors.Add(
                    "A deployment node cannot be null.");
                return;
            }

            // Bound the size and depth of externally supplied deployment data.
            if (depth > 16 || ++count > 512)
            {
                result.Errors.Add(
                    "Deployment trees allow at most 17 levels and 512 nodes.");
                return;
            }

            // A repeated object reference indicates either a cycle or reuse
            // of the same deployment node.
            if (!visited.Add(node))
            {
                result.Errors.Add(
                    "Cycle or repeated node detected.");
                return;
            }

            string name = (node.Name ?? "").Trim();

            if (name.Length == 0 ||
                name.Length > 80 ||
                name.Any(char.IsControl) ||
                name.Contains("/"))
            {
                result.Errors.Add(
                    "Each node needs a name of 1–80 characters " +
                    "without slashes or control characters.");
            }

            string path =
                parent.Length == 0
                    ? name
                    : parent + " / " + name;

            // A node is usable only when it and all of its ancestors
            // remain enabled.
            bool enabled = ancestorsEnabled && node.Enabled;

            var children = node.Children;

            if (children == null)
            {
                result.Errors.Add(
                    path + ": Children must be an array.");
                return;
            }

            if (children.Count > 512)
            {
                result.Errors.Add(
                    path + ": too many child nodes.");
                return;
            }

            // Sibling names must remain unique within the same parent.
            if (children
                .Where(c => c != null)
                .GroupBy(
                    c => (c.Name ?? "").Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1))
            {
                result.Errors.Add(
                    path + ": sibling names must be unique.");
            }

            if (!string.IsNullOrWhiteSpace(node.SensorId))
            {
                // Reuse the registration normalisation rules so deployment
                // identifiers are compared in the same canonical form.
                var identity = new RegisterSensorRequest
                {
                    DeviceId = node.SensorId,
                    Location = "validation",
                    Category = "Environmental"
                };

                SensorRegistrationRules.Validate(identity);

                string id = identity.DeviceId;

                if (!assigned.Add(id))
                {
                    result.Errors.Add(
                        id + ": assigned more than once.");
                }

                if (!sensors.TryGetValue(id, out var sensor))
                {
                    result.Errors.Add(
                        id + ": sensor is not registered.");
                }
                else if (!string.Equals(
                    NormalizePath(sensor.Location),
                    path,
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add(
                        id +
                        ": path must match its registered location (" +
                        sensor.Location +
                        ").");
                }

                if (!enabled)
                {
                    result.Errors.Add(
                        id +
                        ": a deployment ancestor or the sensor node is disabled.");
                }

                // Sensor assignments are restricted to terminal deployment
                // nodes rather than grouping nodes.
                if (children.Count > 0)
                {
                    result.Errors.Add(
                        id +
                        ": sensor assignments must be at leaf nodes.");
                }

                result.Paths.Add(id + " → " + path);
            }

            foreach (var child in children)
            {
                if (count >= 512)
                {
                    result.Errors.Add(
                        "Deployment exceeds 512 nodes.");
                    break;
                }

                Visit(
                    child,
                    path,
                    enabled,
                    depth + 1,
                    sensors,
                    visited,
                    assigned,
                    result,
                    ref count);
            }
        }

    
        // Converts alternate path spacing to the canonical deployment format.
      
        private static string NormalizePath(string path) =>
            string.Join(
                " / ",
                path.Split('/')
                    .Select(part => part.Trim()));
    }
}


// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
