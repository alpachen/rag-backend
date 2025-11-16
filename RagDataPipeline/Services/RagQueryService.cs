using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagPipeline.Embeddings;      // ✅ VoyageEmbedder
using RagPipeline.VectorDb;

namespace RagPipeline.Services
{
    public class RagQueryService
    {
        private readonly VoyageEmbedder _embedder;   // ✅ Voyage embedder
        private readonly QdrantIndexer _indexer;
        private readonly HttpClient _groqClient;
        private readonly string _groqModel = "llama-3.3-70b-versatile";


        public RagQueryService(VoyageEmbedder embedder, QdrantIndexer indexer)  // ✅ 接收 VoyageEmbedder
        {
            _embedder = embedder;
            _indexer = indexer;

            var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("環境變數 GROQ_API_KEY 未設定。");

            _groqClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.groq.com/openai/v1/"),
                Timeout = TimeSpan.FromSeconds(60)
            };

            _groqClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        public async Task<string> AskAsync(string query, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "請輸入查詢內容。";

            Console.WriteLine($"\n🔍 處理查詢: {query}");

            // ✅ 1) Query embedding（使用 Voyage）
            var queryEmbedding = await _embedder.EmbedAsync(query);
            Console.WriteLine($"[DEBUG] query embedding length = {queryEmbedding.Length}");

            // ✅ 2) Qdrant search
            var results = await _indexer.SearchAsync(queryEmbedding, topK);

            // 🆕 添加詳細檢索日誌
            Console.WriteLine($"📊 檢索結果: {results.Count} 個");
            if (results.Any())
            {
                Console.WriteLine("📋 檢索結果詳情:");
                foreach (var result in results.OrderByDescending(r => r.Score))
                {
                    var preview = result.Payload.TryGetProperty("content", out var content)
                        ? content.ToString().Length > 100
                            ? content.ToString().Substring(0, 100) + "..."
                            : content.ToString()
                        : "無內容";
                    Console.WriteLine($"   Score: {result.Score:F3} -> {preview}");
                }
            }
            else
            {
                Console.WriteLine("❌ 沒有檢索到任何結果");
                return "在知識庫中找不到相關內容。";
            }

            var context = BuildContext(results);
            var prompt = BuildPrompt(query, context);

            // ✅ 3) Groq 回答
            return await CallGroqAsync(prompt);
        }

        private string BuildContext(List<QdrantSearchResult> results)
        {
            var sb = new StringBuilder();

            if (results == null || !results.Any())
            {
                return "無相關文件內容。";
            }

            foreach (var r in results.OrderByDescending(x => x.Score))
            {
                // 🆕 正確讀取 JsonElement Payload
                if (r.Payload.ValueKind == JsonValueKind.Object &&
                    r.Payload.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.ValueKind == JsonValueKind.String
                        ? contentElement.GetString()
                        : contentElement.ToString();

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sb.AppendLine(content);
                        sb.AppendLine("\n---\n");
                    }
                }
            }

            var context = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(context))
            {
                return "文件內容為空或無法讀取。";
            }

            return context;
        }

        private string BuildPrompt(string query, string context)
        {
            return $@"
# 角色設定
您是一名醫療院所資訊安全管理系統（ISMS）與醫療四階文件的專業顧問。
您需要基於『提供的文件內容』回答問題，並維持高度可稽核性。

# 回答核心原則（請務必遵守）
1. 回答必須完全建立於提供的文件內容，不得推測、不得臆造、不引用外部知識。
2. 如文件內容不足以回答，請明確說明「文件未提供足夠資訊」並停止推論。
3. 回答不得概括為一般資安常識或 ISO 27001 的常見觀念，除非該內容**確實出現在 context 中**。
4. 回答語氣需正式、客觀、嚴謹，並使用醫療院所常用用語（應、需、不得、依據、確保）。
5. 引用文件時，請精準指出出處，例如：「文件依據：ISMS-2-07，第 6.6.2.2」。

# 重要要求：動態架構生成
請根據『問題性質』自動選擇最合適的回答結構，而 **不要強制套用固定模板**。

範例（僅供模型決定，非強制）：
- 若問題是「流程／程序」：可自然形成目的、步驟、責任、紀錄等結構
- 若問題是「定義」：可自然形成定義、特性、文件引用
- 若問題是「要求／規定」：可自然形成要求、依據、執行重點
- 若問題是「比較／差異」：可自然形成比較維度、差異點、適用性
- 若問題是「範圍／原則」：可自然形成判定基準、引用依據、實施範疇

請**完全由模型自行決定最適合的架構**，但必須符合：
- 條理清晰
- 結構合理
- 不重複
- 可稽核
- 每段內容都能追溯到文件

# 提供之文件內容（僅能使用以下資訊）
{context}

# 問題
{query}

# 回答要求
- 回答需簡潔但具有專業深度
- 每一重要敘述都需附上明確文件依據（若 context 中有對應段落）
- 結構可自行生成，但需保持清楚可讀
- 若查無內容需明確說明

請開始依據文件內容作答。
";
        }



        private async Task<string> CallGroqAsync(string prompt)
        {
            var body = new
            {
                model = _groqModel,
                messages = new[]
                {
                    new { role = "system", content = "你是醫院資訊安全 RAG 模型。" },
                    new { role = "user", content = prompt }
                },
                temperature = 0.2,
                max_tokens = 1024
            };

            var response = await _groqClient.PostAsJsonAsync("chat/completions", body);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return $"❌ Groq API 錯誤：{json}";

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "(無回應)";
        }

        // ============================================
        // 🆕 取得前端需要的 Top 文件來源
        // ============================================
        public async Task<List<RagSource>> GetTopSourcesAsync(string query, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RagSource>();

            // 1) 文字轉 embedding（Voyage）
            var embedding = await _embedder.EmbedAsync(query);

            // 2) 搜尋向量資料庫（Qdrant）
            var results = await _indexer.SearchAsync(embedding, topK);

            // 3) 整理成前端可用格式
            return results.Select(r =>
            {
                // Payload = JsonElement
                string fileName =
                    r.Payload.TryGetProperty("fileName", out var f)
                        ? f.GetString() ?? ""
                        : "unknown";

                string preview =
                    r.Payload.TryGetProperty("content", out var p)
                        ? p.GetString()?.Substring(0, Math.Min(120, p.GetString()!.Length)) + "..."
                        : "(無內容)";

                return new RagSource
                {
                    FileName = fileName,
                    PreviewText = preview,
                    Score = r.Score
                };
            }).ToList();
        }
    }

    // ============================================
    // 🆕 給前端的模型
    // ============================================
    public class RagSource
    {
        public string FileName { get; set; } = "";
        public string PreviewText { get; set; } = "";
        public double Score { get; set; }
    }
}
