using System.Text.Json.Serialization;

namespace RagPipeline.Models
{
    // 新增：用於巢狀結構，儲存 AI 的建議值與推理原因
    public class AnalysisField
    {
        public string Value { get; set; } // AI 推薦的選項值 (例如: "高風險")
        public string Description { get; set; } // 該選項的合規描述 (例如: "涉及跨部門多系統...")
        public string Reasoning { get; set; } // AI 判斷該選項的理由
        public string? Detail { get; set; } // 僅用於 SystemCategory, 填寫 "Other" 時的具體說明

        [JsonPropertyName("confidenceScore")]
        public double ConfidenceScore { get; set; } // 新增：AI 對此建議的信心程度
    }

    public class ChangeRequestAnalysisResult
    {
        [JsonPropertyName("systemCategory")]
        public AnalysisField SystemCategory { get; set; } = new();

        [JsonPropertyName("ticketCategory")]
        public AnalysisField TicketCategory { get; set; } = new();

        [JsonPropertyName("changeType")]
        public AnalysisField ChangeType { get; set; } = new();

        [JsonPropertyName("severity")]
        public AnalysisField Severity { get; set; } = new();

        [JsonPropertyName("impactLevel")]
        public AnalysisField ImpactLevel { get; set; } = new();

        [JsonPropertyName("dependency")]
        public AnalysisField Dependency { get; set; } = new();

        [JsonPropertyName("testPlan")]
        public AnalysisField TestPlan { get; set; } = new();

        [JsonPropertyName("recoveryPlan")]
        public AnalysisField RecoveryPlan { get; set; } = new();

        [JsonPropertyName("summaryReasoning")]
        public string SummaryReasoning { get; set; } = "";

        [JsonPropertyName("rawJson")]
        public string? RawJson { get; set; }

        [JsonPropertyName("complianceStatus")]
        public string ComplianceStatus { get; set; } = "Pending"; // 新增：合規狀態 (Pass/Fail/Review)

        [JsonPropertyName("priorityScore")]
        public int PriorityScore { get; set; } // 新增：綜合風險分數 (0-100)
    }

    // AnalyzeRequest 保持不變
    public class AnalyzeRequest
    {
        public string Content { get; set; }
    }
}