using System.Collections.Generic;

namespace SmartX.Contracts
{
    public class SearchActivity
    {
        public string Query { get; set; } = "";
        public string Category { get; set; } = "";
        public bool AlertsOnly { get; set; }
    }

    public class SuggestedAction
    {
        public string DeviceId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Explanation { get; set; } = "";
        public string SuggestedCommand { get; set; } = "";
        public string Parameters { get; set; } = "";
        public string Severity { get; set; } = "Info";
        public int Confidence { get; set; }
    }

    public class InsightResponse
    {
        public List<SuggestedAction> Suggestions { get; set; } = new List<SuggestedAction>();
        public int SearchEvents { get; set; }
        public string PatternSummary { get; set; } = "";
    }
}
