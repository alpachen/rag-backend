using RagPipeline.Embeddings;
using RagPipeline.VectorDb;
using RagPipeline.Extractors;
using RagPipeline.Processing;

namespace RagDataPipeline.Services
{
    public class DocumentIndexService
    {
        private readonly PdfTextExtractor _pdf;
        private readonly ExcelTextExtractor _excel;
        private readonly TextChunker _chunker;
        private readonly VoyageEmbedder _embedder;
        private readonly QdrantIndexer _qdrant;

        private readonly string _docFolder =
            Path.Combine("Data", "Docs");  // 所有上傳文件都放這裡

        public DocumentIndexService(
            PdfTextExtractor pdf,
            ExcelTextExtractor excel,
            TextChunker chunker,
            VoyageEmbedder embedder,
            QdrantIndexer qdrant)
        {
            _pdf = pdf;
            _excel = excel;
            _chunker = chunker;
            _embedder = embedder;
            _qdrant = qdrant;

            if (!Directory.Exists(_docFolder))
                Directory.CreateDirectory(_docFolder);
        }

        // ======================================================================
        // 🚀 1. 上傳單一文件 → 切片 → 嵌入 → Qdrant upsert
        // ======================================================================
        public async Task IndexSingleFileAsync(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            Console.WriteLine($"📄 [IndexSingle] 處理：{fileName}");

            // 1. 準備一個容器，用來統一存放處理後的資料片段
            // 結構：(內容, 類型標籤, 頁碼)
            var segmentsToIndex = new List<(string Content, string Type, int Page)>();

            // ============================================================
            // 🟥 處理 PDF (需要區分：是表格還是文字？)
            // ============================================================
            if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // 使用新的 ExtractSegments 方法 (來自 PdfPig 的邏輯)
                // 這會回傳已經標記好是 "pdf_table" 還是 "pdf_text" 的片段
                var pdfSegments = _pdf.ExtractSegments(filePath);

                foreach (var seg in pdfSegments)
                {
                    if (seg.Type == "pdf_table")
                    {
                        // 🔥 關鍵策略：如果是 PDF 表格，整塊保留，不要切斷！
                        // 這樣 LLM 才能看到完整的欄位對應
                        segmentsToIndex.Add((seg.Content, "pdf_table", seg.PageNumber));
                    }
                    else
                    {
                        // 策略：普通文字，照常切塊 (Chunking)
                        var chunks = _chunker.Chunk(seg.Content);
                        foreach (var c in chunks)
                        {
                            segmentsToIndex.Add((c, "pdf_text", seg.PageNumber));
                        }
                    }
                }
            }
            // ============================================================
            // 🟩 處理 Excel (全部視為表格)
            // ============================================================
            else if (filePath.EndsWith(".xlsx") || filePath.EndsWith(".xls"))
            {
                // Excel 的文字提取 (使用你原本的邏輯)
                // 建議：確保 _excel.ExtractText 輸出的格式是 Markdown Table 或 CSV 格式
                string text = _excel.ExtractText(filePath);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // 策略：Excel 內容標記為 "table"
                    var chunks = _chunker.Chunk(text);
                    foreach (var c in chunks)
                    {
                        // Excel 暫時沒有頁碼概念，設為 1
                        segmentsToIndex.Add((c, "table", 1));
                    }
                }
            }
            else
            {
                Console.WriteLine($"⚠ 跳過不支援的檔案類型：{fileName}");
                return;
            }

            // 2. 檢查是否有內容需存入
            if (!segmentsToIndex.Any())
            {
                Console.WriteLine("⚠ 無內容可索引，跳過。");
                return;
            }

            // 3. 統一寫入 Qdrant (包含正確的 Metadata)
            int order = 0;
            foreach (var item in segmentsToIndex)
            {
                var vec = await _embedder.EmbedAsync(item.Content);
                var id = Guid.NewGuid().ToString();

                var payload = new Dictionary<string, object>
                {
                    ["file"] = fileName,
                    ["type"] = item.Type,       // ✅ 這裡會存入 "pdf_table", "pdf_text" 或 "table"
                    ["page"] = item.Page,       // ✅ 頁碼 (讓回答更專業)
                    ["order"] = order,
                    ["content"] = item.Content
                };

                await _qdrant.UpsertAsync(id, vec, payload);
                order++;

                // (選用 debug) 顯示有無抓到 PDF 表格
                if (item.Type == "pdf_table")
                {
                    Console.WriteLine($"   🧩 [PDF表格] Page {item.Page} 已保留完整結構");
                }
            }

            Console.WriteLine($"✅ [IndexSingle] 完成：{fileName} (共 {order} 個區塊)");
        }

        // ======================================================================
        // 🚀 2. 重建單一文件（刪除舊向量 → 重新建立）
        // ======================================================================
        public async Task ReindexSingleFileAsync(string fileName)
        {
            Console.WriteLine($"♻ [ReindexOne] 重新索引：{fileName}");

            string path = Path.Combine(_docFolder, fileName);

            if (!File.Exists(path))
                throw new FileNotFoundException($"找不到文件：{path}");

            // 1) 刪除舊向量
            await _qdrant.DeleteByFileNameAsync(fileName);

            // 2) 重建新向量
            await IndexSingleFileAsync(path);
        }

        // ======================================================================
        // 🚀 3. 重建全部文件
        // ======================================================================
        public async Task RebuildAllAsync()
        {
            Console.WriteLine("♻ [RebuildAll] 清空 Qdrant collection...");
            await _qdrant.RecreateCollectionAsync();

            var files = Directory.GetFiles(_docFolder)
                                 .Where(f => f.EndsWith(".pdf") ||
                                             f.EndsWith(".xlsx") ||
                                             f.EndsWith(".xls"))
                                 .ToList();

            Console.WriteLine($"📌 找到 {files.Count} 個文件");

            foreach (var file in files)
                await IndexSingleFileAsync(file);

            Console.WriteLine("🎉 [RebuildAll] 全部重新索引完成！");
        }

        // ======================================================================
        // 🚀 4. 刪除文件 + 刪除對應向量
        // ======================================================================
        public async Task DeleteFileAsync(string fileName)
        {
            string path = Path.Combine(_docFolder, fileName);

            Console.WriteLine($"🗑 [DeleteFile] 刪除：{fileName}");

            if (File.Exists(path))
                File.Delete(path);

            await _qdrant.DeleteByFileNameAsync(fileName);

            Console.WriteLine("🧹 [DeleteFile] 文件與向量已清理。");
        }

        // ======================================================================
        // 工具函式：依副檔名抽取文字
        // ======================================================================
        private string ExtractFileText(string filePath)
        {
            if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return _pdf.ExtractText(filePath);

            if (filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
                return _excel.ExtractText(filePath);

            throw new InvalidOperationException("Unsupported file type: " + filePath);
        }
    }
}

