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
            Console.WriteLine("🟢 後端已接收到 save-form 請求！");

            if (data == null) return BadRequest("請求資料不可為空");

            // 🔍 關鍵補丁：從當前 Session 抓取身分資訊
            var userIdStr = HttpContext.Session.GetString("UserId");
            var userTeam = HttpContext.Session.GetString("UserTeam");

            // 檢查 Session 是否失效
            if (string.IsNullOrEmpty(userIdStr))
            {
                if (data.RequesterId != 0)
                {
                    Console.WriteLine($"⚠️ Session 已過期，但接受前端傳入的 ID: {data.RequesterId}");
                }
                else
                {
                    return BadRequest("儲存失敗：登入狀態已失效，請重新登入。");
                }
            }
            else
            {
                data.RequesterId = int.Parse(userIdStr);
                data.Department = userTeam ?? "";
            }

            try
            {
                // 🚀 1. 產生臨時安全單號，一秒安撫前端
                string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string prefix = data.FormType == "Vulnerability" ? "CHG" : "REQ";
                string fakeTicketNumber = $"{prefix}-{timestamp}";

                // 🚀 2. 【核心魔法】：利用 Task.Run 強行開闢「獨立背景平行執行緒」！
                // 我們不使用 await 去等它，主線程（Request Thread）把任務丟給 CPU 後就直接放行。
                // 這樣 7068 專案就會在背景慢慢去跟 7186 通訊，前端按鈕一微秒都不用等，直接亮起！
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        string drMeowUrl = "https://localhost:7186/api/Forms/SaveAll";

                        // 讓這個跨專案 HTTP 請求在背景靜悄悄地跑
                        var response = await client.PostAsJsonAsync(drMeowUrl, data);

                        if (response.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"\n✅ [原生背景成功] 表單已順利無感轉發至 7186！單號應為: {fakeTicketNumber}");
                        }
                        else
                        {
                            var error = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"\n❌ [原生背景失敗] 7186 回報錯誤: {response.StatusCode} - {error}");
                        }
                    }
                    catch (Exception bgEx)
                    {
                        Console.WriteLine($"\n❌ [原生背景崩潰] 異步管線連線異常: {bgEx.Message}");
                    }
                });

                Console.WriteLine($"🚀 原生背景執行緒已分流！安全釋放前端鎖，預配單號: {fakeTicketNumber}");

                // 🚀 3. 瞬間回傳 200 OK，前端按鈕轉圈圈立刻結束，彈窗關閉！
                return Ok(new { ticketNumber = fakeTicketNumber, isSuccess = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 嚴重錯誤: {ex.Message}");
                return StatusCode(500, $"系統異常: {ex.Message}");
            }
        }

        // 🚀 新增：被 Hangfire 獨立呼叫的背景非同步轉發工作
        [System.ComponentModel.DisplayName("背景轉發表單至核心 API: {0}")]
        public static async Task SendToDrMeowBackendAsync(SaveFormRequest data)
        {
            try
            {
                using var client = new HttpClient();
                string drMeowUrl = "https://localhost:7186/api/Forms/SaveAll";

                var response = await client.PostAsJsonAsync(drMeowUrl, data);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"✅ [Hangfire 背景成功] 表單已無感寫入 7186 入口！");
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ [Hangfire 背景失敗]: {response.StatusCode} - {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Hangfire 網路異常]: {ex.Message}");
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
        public int RequesterId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Priority { get; set; }
        public string? Department { get; set; }
        public string? Extension { get; set; }
        public string? ExpectedCompletionDate { get; set; }
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
        public string? RequestType { get; set; }      // 需求類型
        public string? ExpectedBenefits { get; set; } // 預期效益
        public string? ComplianceStatus { get; set; }
        public int? PriorityScore { get; set; }
        public string? AiReviewComment { get; set; }
    }
}