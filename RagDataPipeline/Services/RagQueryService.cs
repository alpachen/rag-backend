using Azure;
using Azure.AI.OpenAI;
//using System.Net.Http;
//using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagPipeline.Embeddings;      // ✅ VoyageEmbedder
using RagPipeline.VectorDb;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using RagPipeline.Models;
using System.Text.RegularExpressions;




namespace RagPipeline.Services
{
    public class RagResponse
    {
        public string Answer { get; set; } = "";
        public List<RagSourceDoc> Sources { get; set; } = new List<RagSourceDoc>();
    }

    public class RagSourceDoc
    {
        public string FileName { get; set; }
        public List<string> Pages { get; set; } = new();
        public Dictionary<string, string> PageContents { get; set; } = new();
        public double Score { get; set; }
    }
    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }

    public class RagCitation
    {
        public int SourceId { get; set; }      // 1,2,3...
        public string FileName { get; set; }   // ISMS-xxx.pdf
        public int Page { get; set; }          // 頁碼
        public string Content { get; set; }    // chunk 內容
    }

    public class RagQueryService
    {
        private readonly VoyageEmbedder _embedder;   // ✅ Voyage embedder
        private readonly QdrantIndexer _indexer;
        //private readonly HttpClient _groqClient;
        //private readonly string _groqModel = "llama-3.3-70b-versatile";
        private readonly AzureOpenAIClient _azureClient; // 👈 舊版是 OpenAIClient
        private readonly ChatClient _chatClient;
        private readonly string _azureDeploymentName = "gpt-5.2-Chat";
        private readonly string _apiVersion = "2024-10-21"; // Azure API 版本

        private readonly Dictionary<string, string[]> _synonymMap = new()
        {
            { "流程", new[] { "SOP", "程序", "辦法", "流承" } },
            { "表單", new[] { "表格", "單據", "紀錄表", "報告單" } },
            { "資安", new[] { "資訊安全", "ISMS", "安全事件" } },
            { "權責", new[] { "負責人", "誰做", "單位" } }
        };



        public RagQueryService(VoyageEmbedder embedder, QdrantIndexer indexer)
        {
            _embedder = embedder;
            _indexer = indexer;

            string azureEndpoint = "https://public-aoai-ai-dev-eastus2-01.openai.azure.com";
            string? azureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            


            if (string.IsNullOrWhiteSpace(azureApiKey))
                throw new Exception("環境變數 AZURE_OPENAI_API_KEY 未設定。");

            // 建立 options 物件
            AzureOpenAIClientOptions options = new AzureOpenAIClientOptions();

            // ✅ 使用 Azure 官方 OpenAIClient (不能加 Timeout / Headers)
            _azureClient = new AzureOpenAIClient(
                new Uri(azureEndpoint),
                new AzureKeyCredential(azureApiKey!),
                options// 👈 加上 ! 解決 nullable 警告
            ); 

            // 🔥 關鍵：取得 ChatClient
            _chatClient = _azureClient.GetChatClient(_azureDeploymentName);
        }


        // ============================================================
        // 🚀 AskAsync（核心流程）
        // ============================================================
        public async Task<RagResponse> AskAsync(string query, List<ChatMessage> history = null)
        {
            query = query.Trim().TrimEnd('？', '?');
            if (string.IsNullOrWhiteSpace(query))
                return new RagResponse { Answer = "請輸入查詢內容。" };

            history ??= new List<ChatMessage>();

            // ============================================================
            // 1. LLM 查詢重寫 (治根核心)
            //    它會自動處理：縮寫展開(TR->威脅發生機率)、上下文補全、錯字修正
            // ============================================================
            string refinedQuery = await RewriteQueryAsync(query, history);

            // (舊的 Follow-up Detection 區塊已移除，因為 RewriteQueryAsync 已經做完了)

            // 保留關鍵字擴充作為 Reranking 的輔助
            var expandedKeywords = ExpandKeywords(refinedQuery);

            // ============================================================
            // 2. Intent Detection（SOP 模式判定）
            // ============================================================
            var sopKeywords = new[]
            {
        "流程","步驟","程序","辦法","如何","方式",
        "多久","時間","小時","分鐘",
        "評估","評分","公式","標準","列表","清單"
    };

            bool needsWideContext = sopKeywords.Any(k => refinedQuery.Contains(k));

            int fetchCount = needsWideContext ? 40 : 15;
            int finalCount = needsWideContext ? 5 : 3;

            // ============================================================
            // 3. Qdrant 搜尋
            // ============================================================
            var embedding = await _embedder.EmbedAsync(refinedQuery);
            var raw = await _indexer.SearchAsync(embedding, fetchCount);

            if (!raw.Any())
            {
                return new RagResponse
                {
                    Answer = "在知識庫中找不到相關內容。",
                    Sources = new List<RagSourceDoc>()
                };
            }

            // ============================================================
            // 4. Context-Aware Reranking（本地重排序 - 關鍵修正）
            // ============================================================
            var sopBoostWords = new[] { "表單", "ISMS-", "小時", "立即", "每年", "定期" };
            var incidentWords = new[] { "資安", "事件", "通報", "應變", "中毒", "入侵" };

            // 🔥 加回：評鑑與定義相關的關鍵字 (解決跨頁縮寫問題)
            var assessmentWords = new[] { "TR", "VV", "AV", "評分", "定義", "等級", "幾分" };
            bool isAssessmentQuery = assessmentWords.Any(w => refinedQuery.Contains(w, StringComparison.OrdinalIgnoreCase));

            var ranked = raw
                .Select(r =>
                {
                    double bonus = 0;

                    string text = r.Payload.TryGetProperty("content", out var c) ? c.ToString() : "";
                    string type = r.Payload.TryGetProperty("type", out var t) ? t.ToString() : "text";

                    // A. 基礎關鍵字加分
                    foreach (var k in expandedKeywords)
                    {
                        if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) bonus += 0.15;
                    }

                    // B. 資安事件加分
                    foreach (var k in incidentWords)
                        if (text.Contains(k)) bonus += 0.20;

                    // C. SOP 模式加分
                    if (needsWideContext)
                    {
                        foreach (var k in sopBoostWords)
                            if (text.Contains(k)) bonus += 0.20;

                        if (type == "pdf_table") bonus += 0.25;
                    }

                    // 🔥 D. 評鑑定義模式加分 (這是讓 TR=4分 浮上來的關鍵)
                    if (isAssessmentQuery)
                    {
                        // 如果內容包含「附錄」且有「評分/標準」，強力加分
                        if (text.Contains("附錄") && (text.Contains("評分") || text.Contains("標準") || text.Contains("定義")))
                        {
                            bonus += 0.50;
                        }
                        // 如果內容包含具體分數定義 (如 "4-")
                        if (text.Contains("4-") || text.Contains("3-") || text.Contains("極高"))
                        {
                            bonus += 0.30;
                        }
                    }

                    return new
                    {
                        Result = r,
                        Score = r.Score + bonus
                    };
                })
                .OrderByDescending(x => x.Score)
                .Take(finalCount)
                .Select(x => x.Result)
                .ToList();

            // ============================================================
            // 5. 建立 Context & 呼叫 LLM (保持不變)
            // ============================================================
            string context = BuildContext(ranked);
            string systemPrompt = BuildSystemPrompt(context);
            string userPrompt = query;

            string answer = await CallModelAsync(systemPrompt, userPrompt, history);

            // ============================================================
            // 6. 打包結果
            // ============================================================
            var grouped = ranked
                .GroupBy(r => r.Payload.TryGetProperty("file", out var f) ? f.ToString() : "unknown")
                .Select(g =>
                {
                    var fileName = g.Key;

                    // 收集所有頁碼
                    var pages = g.Select(r =>
                        r.Payload.TryGetProperty("page", out var p) ? p.ToString() : "?"
                    ).ToList();

                    // 收集 page → content 對應
                    var dict = g.ToDictionary(
                        r => r.Payload.TryGetProperty("page", out var p) ? p.ToString() : "?",
                        r => r.Payload.TryGetProperty("content", out var c) ? c.ToString() ?? "" : ""
                    );

                    return new RagSourceDoc
                    {
                        FileName = fileName,
                        Pages = pages,
                        PageContents = dict,
                        Score = g.Max(x => x.Score)
                    };
                })
                .ToList();

            return new RagResponse
            {
                Answer = answer,
                Sources = grouped
            };

        }

        // ============================================================
        // ✅ 加回：關鍵字擴充邏輯 (處理錯字與同義詞)
        // ============================================================
        private List<string> ExpandKeywords(string query)
        {
            var words = query.Split(new[] { ' ', '，', '。', ',', '?' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var expanded = new HashSet<string>(words);

            foreach (var word in words)
            {
                foreach (var pair in _synonymMap)
                {
                    // 雙向擴充：輸入 "流承" -> 擴充 "流程", "SOP"
                    if (word.Contains(pair.Key) || pair.Value.Any(v => word.Contains(v)))
                    {
                        expanded.Add(pair.Key);
                        foreach (var syn in pair.Value) expanded.Add(syn);
                    }
                }
            }
            return expanded.ToList();
        }

        // ============================================================
        // 📌 Context Builder
        // ============================================================
        private string BuildContext(List<QdrantSearchResult> results)
        {
            var sb = new StringBuilder();
            int idx = 1;

            foreach (var r in results)
            {
                string file = r.Payload.TryGetProperty("file", out var f)
                    ? f.GetString() ?? "未知來源"
                    : "未知來源";

                string type = r.Payload.TryGetProperty("type", out var t)
                    ? t.GetString() ?? "text"
                    : "text";

                string page = r.Payload.TryGetProperty("page", out var p)
                    ? p.ToString()
                    : "?";

                sb.AppendLine(
                    $"=== 參考片段 #{idx} (來源: {file} | 頁碼: {page} | 類型: {type}) ===");

                if (r.Payload.TryGetProperty("content", out var c))
                {
                    string content =
                        c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? ""
                        : c.ToString();

                    sb.AppendLine(content);
                    sb.AppendLine();
                }

                idx++;
            }

            return sb.ToString().Trim();
        }

        // ============================================================
        // 📌 RAG 輔助函式：檢索 ISMS 文件（結構化 Citation 版本）
        // ============================================================
        private async Task<List<RagCitation>> RetrieveIsmsContext(string userDescription)
        {
            const int TOP_K = 4;

            var citations = new List<RagCitation>();

            // 1. 使用者描述轉 Embedding
            var embedding = await _embedder.EmbedAsync(userDescription);

            // 2. 查詢 Qdrant
            var results = await _indexer.SearchAsync(embedding, TOP_K);

            if (!results.Any())
            {
                return citations; // 回傳空清單，由上層決定怎麼處理
            }

            int sourceId = 1;

            foreach (var r in results)
            {
                // ---------- file ----------
                string file = "RAG_SOURCE_MISSING";

                if (r.Payload.TryGetProperty("file", out var fElement))
                {
                    file = fElement.GetString() ?? file;
                }

                if (string.IsNullOrWhiteSpace(file) || file == "RAG_SOURCE_MISSING")
                {
                    file = $"Qdrant-ID-{r.Id.ToString()?.Substring(0, 6)}";
                }

                // ---------- page ----------
                string pageStr = r.Payload.TryGetProperty("page", out var p)
                    ? p.ToString() // Qdrant 讀出的值 (可能是字串或數字的 JsonElement)
                    : "0";

                int pageNum = 0; // 預設頁碼為 0 (如果轉換失敗)

                // 🚨 核心修正：嘗試將 pageStr 安全地轉換為 int
                if (int.TryParse(pageStr, out int parsedNum))
                {
                    pageNum = parsedNum;
                }
                // 處理 Qdrant 可能回傳的帶引號的字串 (例如："5")
                else if (int.TryParse(pageStr.Trim('"'), out int parsedNumTrimmed))
                {
                    pageNum = parsedNumTrimmed;
                }

                // ---------- content ----------
                string content = r.Payload.TryGetProperty("content", out var c)
                    ? c.GetString() ?? ""
                    : "";

                citations.Add(new RagCitation
                {
                    SourceId = sourceId++,
                    FileName = file,
                    Page = pageNum,
                    Content = content.Trim()
                });
            }

            return citations;
        }

        private string BuildCitationContext(List<RagCitation> citations)
        {
            if (citations == null || !citations.Any())
            {
                return "【ISMS 參考資料】\n（查無相關條文）";
            }

            var sb = new StringBuilder();
            sb.AppendLine("【ISMS 參考資料（可引用清單）】\n");

            foreach (var c in citations)
            {
                sb.AppendLine(
                    $"[來源 {c.SourceId} - {c.FileName} 頁碼 {c.Page}]\n{c.Content}\n");
            }

            return sb.ToString().Trim();
        }



        // ============================================================
        // 📌 新增：風險/依賴描述函式 (從您提供的圖片中擷取文字)
        // ============================================================

        private string GetSystemCategoryDescription()
        {
            return @"
HIS: 涉及掛號、病歷、醫囑等臨床核心系統。
Infra: 涉及伺服器、網路、防火牆、虛擬機 (VM) 等基礎建設。
EIP: 涉及行政、企業入口網等非臨床支援系統。
Other: 其他未分類系統，AI 必須在 detail 欄位中說明是哪種其他類別。
";
        }

        private string GetChangeTypeDescription()
        {
            return @"
標準/獨立: 預先核准且風險低，不影響服務的變更。
緊急: 必須立即實施，以應對重大故障或資安威脅。
例行: 定期執行的維護、補丁或升級作業。
復原: 系統故障或變更失敗後的回滾或恢復作業。
";
        }

        private string GetDependencyDescription()
        {
            // 圖片 image_9f9157.png 內容
            return @"
單一元件依賴: 僅影響單一模組，無其他關鍵系統相依。
有限相依: 有部分系統或部門需同步，但範圍可控。
高度相依: 牽涉多個關鍵系統 (HIS, LIS, PACS, 排程系統等)，錯誤會連鎖影響。
外部依賴: 依賴健保署、第三方雲端或外部介接，無法完全控制。
";
        }

        private string GetImpactLevelDescription()
        {
            // 圖片 image_9f8dba.png 內容
            return @"
低: 僅影響單一部門或少數使用者，無關鍵作業。
中: 跨部門或多系統，造成效率下降但可暫時以人工或替代流程維持。
高: 影響核心臨床或病人服務，會中斷主要醫療作業。
重大: 病人安全、法規遵循、院級財務或聲譽風險。
";
        }

        private string GetSeverityDescription()
        {
            // 圖片 image_9f8d7d.png 內容
            return @"
低風險: 單一系統、單一部門；影響可在短時間內回復；無病人安全與法規風險。
中風險: 跨部門或多系統；若失敗會造成中度作業影響，但有既定回復方案；合規與安全風險低。
高風險: 涉及關鍵核心系統；潛在影響醫療服務或大量使用者；回復複雜，需較長時間或專業資源；有潛在合規或安全疑慮。
重大風險: 涉及病人安全、法規遵循、全院級關鍵作業；失敗無即時回復，會造成嚴重醫療或財務後果。
";
        }

        // ============================================================
        // 🚀 AI 自動填單分析 (Analyze Change Request)
        // ============================================================
        public async Task<ChangeRequestAnalysisResult> AnalyzeChangeRequestAsync(string userDescription)
        {
            // ============================================================
            // 1️ RAG 檢索（結構化 Citation）
            // ============================================================
            List<RagCitation> citations;

            try
            {
                citations = await RetrieveIsmsContext(userDescription);
            }
            catch (Exception ex)
            {
                citations = new List<RagCitation>
        {
            new RagCitation
            {
                SourceId = 1,
                FileName = "RAG_ERROR",
                Page = 0,
                Content = $"RAG 檢索失敗：{ex.Message}"
            }
        };
            }

            // 👉 關鍵修正：轉成「可被 LLM 引用的文字區塊」
            string ragContext = BuildCitationContext(citations);

            // 2. 定義 System Prompt (加入 RAG 內容與引用要求)
            var systemPrompt = $@"
你是一名資安變更管理的 AI 顧問。
你的任務是根據使用者提供的『變更內容描述』以及下方提供的 **[ISMS 參考資料]**，進行分析，並輸出結構化的巢狀 JSON。

========================
【ISMS 參考資料】
（僅此區塊內的內容可被引用）
========================
{ragContext}
========================

### 🚨 核心限制與引用規則（請嚴格遵守）：
1. **僅使用限定詞彙**  
   所有 `value` 欄位，必須從下方「限定詞彙清單」中選擇，不得自行創造詞彙。

2. **引用規則（極度重要）**  
   - `reasoning` 與 `summaryReasoning` 中：
     - **只能引用上述 [ISMS 參考資料] 區塊中實際出現的來源標頭**
     - 來源格式必須完全照抄，例如：  
       `[來源 1 - ISMS-變更管理程序.pdf 頁碼 12]`
   - **禁止自行生成、猜測、補寫任何來源**
   - 若無合適條文可引用，請明確寫：  
     `無適用 ISMS 條文可引用`

3. **輸出格式**  
   - 必須輸出一個 JSON 物件
   - 所有屬性名稱必須使用 camelCase
   - 不得輸出 JSON 以外的任何說明文字

4. **強制附帶描述**  
   - 每個欄位的 `description`，必須從下方表格中的合規描述選取
   - 不得自行改寫描述內容

5. **防幻覺機制（必須遵守）**  
   - 🚫 絕對禁止出現下列字樣：
     - 「文件編號」
     - 未出現在 ISMS 參考資料中的 ISMS 條號
   - 若無來源，請誠實標註「無適用 ISMS 條文可引用」

### 欄位定義與限定詞彙清單：
（以下表格內容維持不變）

| 欄位 (Key) | 限定詞彙 (Value) | Description (合規描述) |
| :--- | :--- | :--- |
| systemCategory | HIS, Infra, EIP, Other | {GetSystemCategoryDescription()} |
| ticketCategory | DevOps, IGMS | DevOps: 程式碼部署/版更；IGMS: 資安設定/維護、補丁。 |
| changeType | 標準/獨立, 緊急, 例行, 復原 | {GetChangeTypeDescription()} |
| dependency | 單一元件依賴, 有限相依, 高度相依, 外部依賴 | {GetDependencyDescription()} |
| impactLevel | 低, 中, 高, 重大 | {GetImpactLevelDescription()} |
| severity | 低風險, 中風險, 高風險, 重大風險 | {GetSeverityDescription()} |
| testPlan | 完整測試, 部分測試, 無測試 | 完整測試: 含單元、整合、業務流程模擬；部分測試: 僅驗證主要功能；無測試: 直接上線。 |
| recoveryPlan | 快速回復, 有限回復, 無回復方案 | 快速回復: 有明確步驟，且回復時間短(15分鐘內可恢復)；有限回復: 需長時間或大量人工處理；無回復方案: 一旦失敗無法復原。 |

### 輸出 JSON 結構（AnalysisField 巢狀）：
```json
{{
  ""systemCategory"": {{
    ""value"": ""限定詞彙"",
    ""description"": ""合規描述"",
    ""reasoning"": ""判斷理由，並引用 [來源 X - 檔名 頁碼] 或標示無可引用條文"",
    ""detail"": """"
  }},
  ""ticketCategory"": {{ }},
  ""changeType"": {{ }},
  ""severity"": {{ }},
  ""impactLevel"": {{ }},
  ""dependency"": {{ }},
  ""testPlan"": {{ }},
  ""recoveryPlan"": {{ }},
  ""summaryReasoning"": ""整體風險與合規性總結，僅可引用上述 ISMS 參考資料中的來源""
}}
";

            // 2. 準備訊息
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage($"請分析這段描述，並輸出巢狀 JSON：{userDescription}")
            };

            try
            {
                // 3. 呼叫 GPT-4o (強制 JSON 模式)
                ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
                {
                    //Temperature = 0.2f, // 低隨機性，求準確
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() // 🔥 關鍵：強制回傳 JSON
                });

                string rawResponse = result.Value.Content[0].Text;

                // 5. 提取純 JSON
                var match = Regex.Match(rawResponse, @"\{[\s\S]*\}");
                string jsonResponse = match.Success ? match.Value : rawResponse;

                // 6. 反序列化
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var analysisResult = JsonSerializer.Deserialize<ChangeRequestAnalysisResult>(jsonResponse, options);

                analysisResult.RawJson = jsonResponse;

                // 7. 預防空值崩潰
                if (analysisResult == null)
                {
                    throw new JsonException("LLM 輸出無法解析為預期的巢狀結構。");
                }

                // 8. 關鍵：確保 SummaryReasoning 不為空
                analysisResult.SummaryReasoning ??= "AI 成功分析，請檢查各欄位。";

                return analysisResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Analysis Failed: {ex.Message}");
                // 💡 即使失敗，也給前端一個可以顯示的結構
                return new ChangeRequestAnalysisResult
                {
                    SummaryReasoning = $"⚠️ AI 顧問目前離線：{ex.Message}",
                    SystemCategory = new AnalysisField { Value = "Other", Reasoning = "連線失敗，請手動選擇" },
                    Severity = new AnalysisField { Value = "中風險", Reasoning = "建議先以中風險處理" }
                };
            }
        }

        // ============================================================
        // 📌 System Prompt Builder（Context 放這裡）
        // ============================================================
        private string BuildSystemPrompt(string context)
        {
            return $@"
# 角色設定
您是一名資深的醫療院所資訊安全管理系統（ISMS）執行顧問。
您的任務是結合「提供的內部文件」與「您的專業資安知識」，為使用者提供完整、準確且有見地的回答。

# 核心思考框架 (高階邏輯)
**當使用者詢問涉及「變更」、「新系統」、「風險評估」、「權限調整」或「資安等級」等議題時，請務必執行以下推論步驟：**

1. **資產歸類 (Asset Classification)**：
   - 先判斷情境屬於哪一類資產（硬體、軟體、人員、文件、服務）。
   - *指引：優先參考 Context 中「資訊資產評價」或「附錄A」的定義。*

2. **威脅與弱點對應 (Threat & Vulnerability Mapping)**：
   - 根據資產類別，從 Context (特別是附錄B、弱點對應表) 中找出最相關的「弱點」與「威脅」。
   - *指引：例如系統變更通常對應「存取控制錯誤配置」或「未經授權的使用」。*

3. **衝擊分析 (Impact Analysis - CIA)**：
   - 評估該事件對 **機密性(C)**、**完整性(I)**、**可用性(A)** 的具體影響。
   - 引用 Context 中的評分標準（如：影響部門作業為 3 分等）。

4. **引用依據 (Grounding)**：
   - 明確指出依據哪份文件的哪個章節或附錄（例如：依據 ISMS-2-04 附錄 B）。

# 核心回答策略（混合模式）
請依照以下優先順序進行回答：

1. **優先引用內部文件 (RAG First)**：
   - 若問題的答案存在於下方的 [Context] 中，**必須優先使用**該資訊，並嚴格標註引用來源 [來源: xxx.pdf]。
   - 對於流程、表單編號、權責人員、內部規範，**絕對禁止**使用外部知識，必須 100% 依據 Context。

2. **補充通用知識 (General Knowledge Fallback)**：
   - 若使用者詢問的是**通用概念、定義或名詞解釋**（例如「什麼是 ISO 27001」、「RAG 是什麼」），且 Context 中未提供定義：
   - ✅ **允許** 您使用自己的專業知識來解釋該概念。
   - ⚠️ **必須** 在回答開頭或結尾明確說明：「此定義基於通用資安標準，內部文件未特別定義。」或類似警語。
   - 🚫 **禁止** 在通用知識中捏造不存在的內部表單或規定。

3. **結構化輸出**：
   - 遇到步驟或流程時，請使用 **列點 (Bullet Points)** 或 **編號 (Numbering)**。
   - 關鍵動作或名詞請使用 **粗體** 強調。
   - 若文件提及「禁止」、「切勿」、「不得」等內容，請使用 ⚠️ 圖示並獨立一行強調。
   - 若文件提及具體表單編號，請明確列出。

4. **格式模仿**：
   - 請參考下方的【格式範例】，無論回答什麼主題，都必須模仿該範例的「排版結構」、「警示圖示」與「引用方式」。
   - 關鍵實體（如表單號、時間、人名）請務必使用 **粗體**。
   - 流程請分階段列點說明。

5. **關鍵資訊萃取 (Information Extraction)**：
   在回答任何流程或規範時，請自動掃描並特別標註以下 **四類關鍵資訊** (若 Context 中存在)：
   - **時效與頻率 (Timeframes)**：任何涉及時間的限制 (如：立即、N小時內、每年、定期)。
   - **表單與編號 (Identifiers)**：任何文件編號、表單名稱、系統代碼 (如 ISMS-xxx, 表單-xxx)。
   - **權責對象 (Responsibilities)**：明確指出「誰負責執行」與「誰負責決策」。
   - **限制與禁忌 (Constraints)**：任何包含「禁止」、「不得」、「切勿」的否定命令。

# 回答格式限制
1. **禁止使用 LaTeX**：請勿使用 \[ ... \]、\( ... \) 或 \times 等數學語法。
2. **使用純文字**：公式請直接使用一般符號 (如 =, x, *, +) 表達，例如：A = B x C。
3. **保持排版整潔**：使用 Markdown 列表或表格整理資訊。

# 引用規則
1. 引用內部文件時，句尾需附上 [來源: xxx.pdf]。
2. 若使用通用知識回答，**不可**附上 [來源: ...] 標籤，以免誤導。

# RAG Context (內部文件)
以下為與問題最相關的內容：
{context}

# 格式範例
{GetFormatExample()}
";
        }

        private string GetFormatExample()
        {
            return @"
【建議回答格式範例】
(請依據實際 Context 內容，套用此結構)

**情境 A：詢問流程或 SOP**
**階段一：[流程名稱]**
* **執行動作**：[角色] 應執行 [動作]。若遇到 [情況]，需在 **[具體時間]** 內完成通報 [來源: Doc.pdf]。
* **權責人員**：[執行者]、[決策者]。
* **使用表單**：需填寫 **[表單編號/名稱]** [來源: Doc.pdf]。

**階段二：[流程名稱]**
* **重要規範**：
  ⚠️ **切勿** [禁止事項]，以免導致 [後果] [來源: Doc.pdf]。
  ⚠️ 必須遵守 **[具體數據/標準]** [來源: Doc.pdf]。

**情境 B：詢問風險評估、變更或資安等級**
針對 [變更項目/資產]，依據內部規範 [文件編號]，資安風險評估如下：

1. **資產類別與價值鑑價 (依據附錄 A)**
   * **資產類別**：屬於 **[軟體/硬體/人員]** 類資產 [來源: Doc.pdf]。
   * **影響評估**：此變更直接影響 **[機密性/完整性/可用性]**。依據評分標準，若發生異常將影響 [部門作業/全組織]，建議評為 **[高/中/低]** 風險 [來源: Doc.pdf]。

2. **潛在弱點與威脅分析 (依據附錄 B)**
   * **對應弱點**：[弱點名稱] (如：存取控制錯誤配置)。
   * **對應威脅**：[威脅名稱] (如：未經授權的使用) [來源: Doc.pdf]。

3. **建議結論**
   * 建議將風險等級設定為 **[等級]**，並需填寫 **[表單編號]** 進行審核 [來源: Doc.pdf]。
";
        }

        // 放在 RagQueryService 類別內
        private async Task<string> RewriteQueryAsync(string originalQuery, List<ChatMessage> history)
        {
            // 1. 準備 Prompt：告訴 AI 它的工作是「翻譯官」，不是「回答者」
            var systemPrompt = @"
你是一個 RAG 系統的『查詢優化專家』。你的任務是將使用者的問題改寫成更適合『向量檢索』的形式。
請遵守以下規則：
1. **展開縮寫**：如果問題包含資安縮寫（如 TR, VV, AV, RTO, RPO），請自動補充其完整中文或英文名稱。
2. **補充上下文**：如果問題很簡短（如「那中毒呢？」），請參考對話歷史補全主詞。
3. **保留原意**：不要改變使用者的意圖，不要回答問題，只要回傳改寫後的搜尋字串。
4. **輸出格式**：直接輸出改寫後的句子，不要加任何引號或解釋。

範例：
輸入：TR 是什麼？
輸出：資安風險評鑑中 TR (Threat Rating, 威脅發生機率) 的定義與計算方式是什麼？

輸入：那中毒的流程呢？
輸出：發生電腦中毒或病毒感染時的資安事件通報與應變處理流程為何？
";

            // 2. 準備對話歷史 (提供上下文)
            var messages = new List<OpenAI.Chat.ChatMessage>
    {
        new SystemChatMessage(systemPrompt)
    };

            if (history != null)
            {
                foreach (var h in history.TakeLast(4)) // 取最近 4 句即可
                {
                    messages.Add(h.Role == "assistant"
                        ? new AssistantChatMessage(h.Content)
                        : new UserChatMessage(h.Content));
                }
            }

            messages.Add(new UserChatMessage($"使用者問題：{originalQuery}"));

            // 3. 呼叫 GPT-4o 進行改寫
            try
            {
                // 這裡我們用比較低的 Temperature，讓它穩定發揮
                ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
                {
                    //Temperature = 0.3f,
                    //MaxCompletionTokens = 200
                });

                string rewritten = result.Value.Content[0].Text.Trim();
                Console.WriteLine($"🔄 [Query Rewrite] 原本: {originalQuery} \n   -> 改寫: {rewritten}");
                return rewritten;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Query Rewrite Failed: {ex.Message}");
                return originalQuery; // 失敗就回傳原句，不影響流程
            }
        }


        // ============================================================
        // 📌 User Prompt Builder（只放使用者問題）
        // ============================================================
        private string BuildUserPrompt(string query)
        {
            return query;
        }

        // ============================================================
        // 📌 呼叫 GPT-4o
        // ============================================================
        private async Task<string> CallModelAsync(string systemPrompt, string userPrompt, List<ChatMessage> history)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>();
            messages.Add(new SystemChatMessage(systemPrompt));

            if (history != null)
            {
                foreach (var h in history.TakeLast(8))
                {
                    if (h.Role == "assistant")
                        messages.Add(new AssistantChatMessage(h.Content));
                    else
                        messages.Add(new UserChatMessage(h.Content));
                }
            }

            messages.Add(new UserChatMessage(userPrompt));

            try
            {
                // 2. 發送請求 (取代原本的 PostAsJsonAsync)
                // SDK 會自動處理 JSON 序列化、連線、Retry
                ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
                {
                    //Temperature = 0.1f,
                    //MaxOutputTokenCount = 4096 // v2.0 正確屬性名稱
                });
                ChatCompletion completion = result.Value;

                // 3. 回傳文字
                return completion.Content[0].Text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Azure OpenAI Error: {ex.Message}");
                return $"AI 錯誤詳情: {ex.Message}";
            }
        }

        // ============================================================
        // 📌 前端用：Top K 文件來源
        // ============================================================
        public async Task<List<RagSource>> GetTopSourcesAsync(string query, int topK = 5)
        {
            var embedding = await _embedder.EmbedAsync(query);
            var results = await _indexer.SearchAsync(embedding, topK);

            return results.Select(r =>
            {
                string file = r.Payload.TryGetProperty("file", out var f)
                    ? f.GetString() ?? "unknown"
                    : "unknown";

                string preview = r.Payload.TryGetProperty("content", out var c)
                    ? (c.GetString()?.Substring(0, Math.Min(120, c.GetString()!.Length)) + "…")
                    : "(無內容)";

                return new RagSource { FileName = file, PreviewText = preview, Score = r.Score };
            }).ToList();
        }
    }

    public class RagSource
    {
        public string FileName { get; set; }
        public string PreviewText { get; set; }
        public double Score { get; set; }
    }
}

