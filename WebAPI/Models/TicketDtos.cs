namespace Web_API.Models
{
    public class VulnerabilitySubmitDto
    {
        public string Title { get; set; } = string.Empty;
        public string? TicketNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public string SystemCategory { get; set; } = "Other";
        public string TicketCategory { get; set; } = "DevOps";
        public string ChangeType { get; set; } = "標準";
        public string Severity { get; set; } = "低風險";
        public string ImpactLevel { get; set; } = "低";
        public string? Dependency { get; set; }
        public string? TestPlan { get; set; }
        public string? RecoveryPlan { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public int RequesterId { get; set; } = 5; // 預設為 gmember1
        public string FormType { get; set; } = "Change";
        public string? Summary { get; set; } // AI 產生的摘要
    }
    public class RequestTicketSubmitDto
    {
        // === 1. 主表 (RequestTicket) 必備 ===
        public string Title { get; set; } = string.Empty;       // 需求標題
        public string Description { get; set; } = string.Empty; // 原始描述
        public string TicketNumber { get; set; } = string.Empty; // 單號 (REQ-xxxx)
        public int RequesterId { get; set; } = 5;               // 申請人 ID
        public byte Status { get; set; } = 0;                   // 狀態 (0: PendingAI)

        // === 2. AI 分析詳情 (RequestAiDetail) ===
        public bool IsITRelated { get; set; } = true;           // 是否與 IT 相關
        public string? RefinedTitle { get; set; }               // AI 優化後的標題
        public string? RefinedDescription { get; set; }         // AI 優化後的描述
        public string? SecurityAssessment { get; set; }         // 資安評估建議
        public string? AiReason { get; set; }                   // AI 判斷理由
        public bool IsProcessed { get; set; } = true;           // 是否已處理完成

        // === 3. 使用者額外輸入 (RequestUserInput) ===
        // 這些是從前端 Modal 抓到的欄位
        public string? Department { get; set; }                 // 申請部門
        public string? Extension { get; set; }                  // 分機
        public string? ExpectedDate { get; set; }               // 期望完成日期
        public string? Priority { get; set; }                   // 優先等級 (高/中/低)

        // === 4. 系統邏輯 ===
        public string FormType { get; set; } = "General";       // 區分是需求還是變更
        public string? ChatSnapshot { get; set; }               // 對話紀錄快照
    }
}
