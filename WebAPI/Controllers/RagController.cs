using Microsoft.AspNetCore.Mvc;
using WebAPI.Models;
using RagPipeline.Services; // 引用你的 RAG 服務 namespace
using RagPipeline.Models;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // API 路徑會是: api/rag
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

        [HttpPost("ask")] // 路徑: POST api/rag/ask
        public async Task<IActionResult> Ask([FromBody] RagQueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest("Query cannot be empty.");
            }

            try
            {
                // 呼叫核心 RAG 邏輯
                // 注意：這裡我們回傳的是 RagPipeline.Services.RagResponse
                // 我們需要把它轉成 WebAPI.Models.RagQueryResponse (或是讓兩邊共用 Model)
                var result = await _ragService.AskAsync(request.Query, _serverHistory);

                // 更新伺服器端歷史紀錄
                _serverHistory.Add(new ChatMessage { Role = "user", Content = request.Query });
                _serverHistory.Add(new ChatMessage { Role = "assistant", Content = result.Answer });

                // 轉換模型 (Mapping)
                var response = new RagQueryResponse
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
                return StatusCode(500, $"Internal Server Error: {ex.Message}");
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
    }
}