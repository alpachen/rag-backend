using Microsoft.AspNetCore.Mvc;
using WebAPI.Models;
using RagPipeline.Services; // 引用你的 RAG 服務 namespace
using RagPipeline.Models;
using DocumentFormat.OpenXml.InkML;




namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/rag")] // API 路徑會是: api/rag
    public class RagController : ControllerBase
    {
        private readonly RagQueryService _ragService;


        // 這裡為了簡單演示，暫時用靜態變數存歷史紀錄 (Server 重啟會消失)
        // 實際產品建議由前端傳送完整的 history，或是存入 Redis/資料庫
        private static List<ChatMessage> _serverHistory = new List<ChatMessage>();


        public RagController(RagQueryService ragService)
        {
            _ragService = ragService;
        }

        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] WebAPI.Models.RagQueryRequest request)
        {

            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest("Query cannot be empty.");
            }

            try
            {
                // 1. 根據 Mode 選擇不同的處理路徑
                // Mode 可能的值: "consult" (預設), "request" (需求單), "vulnerability" (變更單)
                string currentMode = request.Mode?.ToLower() ?? "consult";
                Console.WriteLine($"🔥 Current Mode: {currentMode}");

                // ✅ 2. 判斷是否為「填表/分析」模式
                if (currentMode == "forms" || currentMode == "request" || currentMode == "vulnerability")
                {
                    // 1. 執行 AI 分析
                    var agentResult = await _ragService.ProcessSmartFormAsync(request.Query, currentMode, _serverHistory);

                    // 2. 更新紀錄
                    UpdateHistory(request.Query, agentResult.Answer);

                    // 3. ✅ 使用「匿名型別」直接回傳，這會強制產生 JSON 欄位
                    // 這裡的名稱請務必跟前端 JS 抓的 data.isFormReady 對上
                    return Ok(new
                    {
                        answer = agentResult.Answer,
                        isFormReady = agentResult.IsComplete,
                        formDataJson = agentResult.FormDataJson,
                        isComplete = agentResult.IsComplete // 多給一個備用
                    });
                }

                // ✅ 3. 原本的「一般顧問」RAG 模式
                var result = await _ragService.AskAsync(request.Query, _serverHistory);
                UpdateHistory(request.Query, result.Answer);

                var response = new WebAPI.Models.RagQueryResponse
                {
                    Answer = result.Answer,
                    Sources = result.Sources.Select(s => new WebAPI.Models.RagSourceDoc
                    {
                        FileName = s.FileName,
                        Pages = s.Pages,
                        PageContents = s.PageContents,
                        Score = s.Score
                    }).ToList()
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }

        }

        [HttpPost("reset-history")]
        public IActionResult ResetHistory()
        {
            // 清空存放在 Session 裡的對話歷史紀錄
            HttpContext.Session.Remove("ChatHistory");
            // 如果妳是用靜態變數或快取存，也要一併清空
            return Ok(new { success = true, message = "History Cleared" });
        }

        [HttpPost("save-form")]
        public async Task<IActionResult> SaveForm([FromBody] SaveFormRequest data)
        {
            // Nikki，這裡加個 Log，如果連這行都沒印出來，代表真的是 CORS 或路由擋掉了
            Console.WriteLine("🟢 後端已接收到 save-form 請求！");

            if (data == null) return BadRequest("請求資料不可為空");

            try
            {
                using var client = new HttpClient();

                // ✅ 修正：統一呼叫 Dr.meow 的單一入口 SaveAll
                // 確保 7186 是妳前端專案啟動後的正確埠號
                string drMeowUrl = "https://localhost:7186/api/Forms/SaveAll";

                var response = await client.PostAsJsonAsync(drMeowUrl, data);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<dynamic>();
                    return Ok(result);
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ 轉發失敗: {response.StatusCode} - {error}");
                    return StatusCode((int)response.StatusCode, $"前端 API 儲存失敗: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 嚴重錯誤: {ex.Message}");
                return StatusCode(500, $"系統異常: {ex.Message}");
            }
        }

        // 新增這個 POST 方法
        [HttpPost("analyze-change")] // API 路徑: POST api/rag/analyze-change
        public async Task<IActionResult> AnalyzeChange([FromBody] AnalyzeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("Content cannot be empty.");
            }

            try
            {
                // 呼叫剛剛寫好的 Service 方法
                var result = await _ragService.AnalyzeChangeRequestAsync(request.Content);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Analysis Error: {ex.Message}");
            }
        }
        // 輔助方法：統一更新紀錄
        private void UpdateHistory(string q, string a)
        {
            _serverHistory.Add(new ChatMessage { Role = "user", Content = q });
            _serverHistory.Add(new ChatMessage { Role = "assistant", Content = a });
        }
    }
    public class SaveFormRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Priority { get; set; }
        public string? Department { get; set; }
        public string? Extension { get; set; }
        public string? ExpectedDate { get; set; }
        public string? FormType { get; set; }

        // 新增這些給變更單用
        public string? SystemCategory { get; set; }
        public string? TicketCategory { get; set; }
        public string? ChangeType { get; set; }
        public string? Severity { get; set; }
        public string? ImpactLevel { get; set; }
        public string? Dependency { get; set; }
        public string? TestPlan { get; set; }
        public string? RecoveryPlan { get; set; }
    }
}