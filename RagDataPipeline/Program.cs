using RagPipeline.Services;
using RagPipeline.Embeddings;   // ✅ VoyageEmbedder
using RagPipeline.VectorDb;
using RagPipeline.Extractors;
using RagPipeline.Processing;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("使用方式:");
            Console.WriteLine("  index <folderPath>   建立索引");
            Console.WriteLine("  chat                 啟動對話模式");
            return;
        }

        // ------------------------------------------------------
        // ✅ 使用 Voyage Embedding（最推薦、支援中文）
        // ------------------------------------------------------
        var embedder = new VoyageEmbedder();

        // ✅ 建立 Qdrant Indexer
        var indexer = new QdrantIndexer();

        // ✅ File extractors
        var pdf = new PdfTextExtractor();
        var excel = new ExcelTextExtractor();
        var chunker = new TextChunker();

        // ✅ RAG Query（Voyage embedding + Groq LLM）
        var rag = new RagQueryService(embedder, indexer);

        // ------------------------------------------------------
        // ✅ index 模式
        // ------------------------------------------------------
        if (args[0] == "index")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("請指定資料夾路徑：");
                return;
            }

            var folder = args[1];
            if (!Directory.Exists(folder))
            {
                Console.WriteLine("資料夾不存在.");
                return;
            }

            Console.WriteLine("⚠ 是否要清空整個 Qdrant collection？ (y/N)");
            var input = Console.ReadLine()?.Trim().ToLower();

            if (input == "y")
            {
                Console.WriteLine("🗑 正在清空 collection...");
                await indexer.RecreateCollectionAsync();
            }
            else
            {
                Console.WriteLine("✅ 保留現有 collection，不清空");
                await indexer.EnsureCollectionAsync();
            }

            await RunIndexing(folder, pdf, excel, chunker, embedder, indexer);
            return;
        }

        // ------------------------------------------------------
        // ✅ chat 模式（RAG）
        // ------------------------------------------------------
        if (args[0] == "chat")
        {
            // 🆕 先執行嵌入測試
            Console.WriteLine("🔍 執行嵌入相似度測試...");
            await TestEmbeddingSimilarity(embedder, indexer);
            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("✅ RAG Chat 已啟動 (支援上下文記憶)");
            Console.WriteLine(new string('=', 50));
            Console.WriteLine("開始聊天模式...");
            await RunChat(rag);
            return;
        }
    }

    // =====================================================================
    // ✅ index：升級版索引邏輯 (支援頁碼、表格結構、類型標記)
    // =====================================================================
    static async Task RunIndexing(
        string folder,
        PdfTextExtractor pdf,
        ExcelTextExtractor excel,
        TextChunker chunker,
        VoyageEmbedder embedder,
        QdrantIndexer indexer)
    {
        Console.WriteLine($"🔍 開始建立索引：{folder}");

        var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"找到 {files.Count} 個文件.");

        foreach (var file in files)
        {
            string fileName = Path.GetFileName(file);
            Console.WriteLine($"📄 處理：{fileName}");

            // 1. 準備一個容器來裝「要存入的片段」
            // 結構：(內容, 類型, 頁碼)
            var segmentsToIndex = new List<(string Content, string Type, int Page)>();

            // ==================================================
            // 🟥 PDF 處理 (關鍵修改：使用 ExtractSegments)
            // ==================================================
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // 呼叫我們寫好的聰明萃取器
                // 它會回傳已經包含「頁碼」和「表格標記」的片段
                var pdfSegments = pdf.ExtractSegments(file);

                foreach (var seg in pdfSegments)
                {
                    // 直接使用萃取器回傳的區塊
                    // 不再進行二次 Chunk，以免把我們辛苦建立的表格結構或標題切斷
                    segmentsToIndex.Add((seg.Content, seg.Type, seg.PageNumber));
                }
            }
            // ==================================================
            // 🟩 Excel 處理 (維持原樣，視為表格)
            // ==================================================
            else
            {
                string text = excel.ExtractText(file);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Excel 通常內容較密集，還是切一下比較保險
                    var chunks = chunker.Chunk(text);
                    foreach (var c in chunks)
                    {
                        segmentsToIndex.Add((c, "table", 1)); // Excel 預設頁碼 1
                    }
                }
            }

            if (!segmentsToIndex.Any())
            {
                Console.WriteLine("⚠ 無內容可索引，跳過。");
                continue;
            }

            // ==================================================
            // 💾 存入 Qdrant (寫入正確的 Metadata)
            // ==================================================
            int order = 0;
            foreach (var item in segmentsToIndex)
            {
                // 生成向量
                var vec = await embedder.EmbedAsync(item.Content);
                var pointId = Guid.NewGuid().ToString();

                // ⚠️ 這裡最重要：把 Page 和 Type 存進去！
                var payload = new Dictionary<string, object>
                {
                    ["file"] = fileName,
                    ["type"] = item.Type,       // ✅ 存入類型 (pdf_table / pdf_text)
                    ["page"] = item.Page,       // ✅ 存入頁碼 (解決 Page ? 問題)
                    ["order"] = order,
                    ["content"] = item.Content  // ✅ 這是完整的結構化內容
                };

                await indexer.UpsertAsync(pointId, vec, payload);
                order++;

                // (選用) 顯示進度，讓你看到它有在抓表格
                if (item.Type == "pdf_table")
                {
                    // Console.WriteLine($"   🧩 偵測到表格 (Page {item.Page})");
                }
            }

            Console.WriteLine($"✅ 完成：{fileName} (共 {order} 個區塊)");
        }

        Console.WriteLine("✅ 全部索引完成！");
    }

    // =====================================================================
    // ✅ chat：使用者輸入 → RAG → Llama 70B（Groq）回答
    // =====================================================================
    static async Task RunChat(RagQueryService rag)
    {
        // 1. 在迴圈外宣告歷史紀錄，確保記憶延續
        var chatHistory = new List<ChatMessage>();

        Console.WriteLine("✅ RAG Chat 已啟動（輸入 exit 離開）");

        while (true)
        {
            Console.Write("\n你：");
            var q = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(q) || q.Trim().ToLower() == "exit")
                break;

            try
            {
                // 2. 呼叫 AskAsync
                // 注意：這裡回傳的是 RagResponse 物件，包含 Answer 和 Sources
                var response = await rag.AskAsync(q, chatHistory);

                // 3. 顯示主要回答
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n--- 回答 ---");
                Console.ResetColor();
                Console.WriteLine(response.Answer);
                Console.WriteLine();

                // 4. 顯示引用來源 (模擬前端的側邊欄功能)
                if (response.Sources != null && response.Sources.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("📚 參考文件來源 (Reference):");
                    Console.ResetColor();

                    foreach (var src in response.Sources)
                    {
                        Console.WriteLine($"📄 {src.FileName} (Page: {src.Page})");

                        // 製作內容預覽 (去除換行，只取前 60 字，避免洗版)
                        var preview = src.Content.Replace("\r", "").Replace("\n", " ").Trim();
                        if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"   📝 \"{preview}\"");
                        Console.ResetColor();
                        Console.WriteLine();
                    }
                }

                // 5. 更新歷史紀錄 (注意：只存文字 Answer，不需要存 Sources)
                chatHistory.Add(new ChatMessage { Role = "user", Content = q });
                chatHistory.Add(new ChatMessage { Role = "assistant", Content = response.Answer });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ 發生錯誤: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine("👋 已離開聊天模式。");
    }

    // 在 Program.cs 中添加
    public static async Task TestEmbeddingSimilarity(VoyageEmbedder embedder, QdrantIndexer indexer)
    {
        var testQueries = new[]
        {
        "如何界定ISMS的範圍",
        "資訊資產分類標準",
        "風險評鑑程序",
        "內部稽核查核"
    };

        foreach (var query in testQueries)
        {
            Console.WriteLine($"\n🧪 測試查詢: {query}");

            try
            {
                var vector = await embedder.EmbedAsync(query);
                var results = await indexer.SearchAsync(vector, 3);

                if (results.Any())
                {
                    Console.WriteLine($"✅ 檢索到 {results.Count} 個相關結果:");
                    foreach (var result in results)
                    {
                        Console.WriteLine($"   📊 相似度: {result.Score:F3}");
                        // 🆕 詳細檢查所有 Payload 字段
                        // 🆕 正確讀取 JsonElement Payload
                        Console.WriteLine($"   🔍 Payload 字段:");
                        if (result.Payload.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in result.Payload.EnumerateObject())
                            {
                                var value = property.Value.ValueKind == JsonValueKind.String
                                    ? property.Value.GetString()
                                    : property.Value.ToString();

                                var valuePreview = value?.Length > 30
                                    ? value.Substring(0, 30) + "..."
                                    : value ?? "null";
                                Console.WriteLine($"      {property.Name}: {valuePreview}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"      Payload 類型: {result.Payload.ValueKind}");
                        }

                        Console.WriteLine(); // 空行分隔
                    }
                }
                else
                {
                    Console.WriteLine("❌ 沒有檢索到相關結果");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 測試失敗: {ex.Message}");
            }
        }
    }
}
