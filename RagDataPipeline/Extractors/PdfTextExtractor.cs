using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RagPipeline.Extractors
{
    public class DocumentSegment
    {
        public string Content { get; set; } = "";
        public string Type { get; set; } = "pdf_text";
        public int PageNumber { get; set; }
    }

    public class PdfTextExtractor
    {
        // 設定：欄位間隔閾值 (單位：PDF點數)
        // 如果兩個字之間的 X 軸空白大於這個寬度，就視為換欄
        private const double ColumnGapThreshold = 15.0;

        public List<DocumentSegment> ExtractSegments(string filePath)
        {
            var segments = new List<DocumentSegment>();
            string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);

            // 輔助判斷：檔名是否暗示這是表格文件
            bool isTableFile = fileName.Contains("表") || fileName.Contains("清單") || fileName.Contains("名冊");

            try
            {
                using (var pdf = PdfDocument.Open(filePath))
                {
                    foreach (var page in pdf.GetPages())
                    {
                        // 1. 取得所有單字及其座標 (Word list with coordinates)
                        var words = page.GetWords().ToList();

                        // 如果頁面完全沒字 (可能是圖片掃描件)，跳過
                        if (!words.Any()) continue;

                        // 2. 進行結構化重組 (把座標轉成 Markdown 表格格式)
                        // 這是「治本」的關鍵：強制插入 '|' 分隔線
                        var structuredText = ConvertToStructuredMarkdown(words);

                        if (string.IsNullOrWhiteSpace(structuredText)) continue;

                        // 3. 判斷是否為表格頁
                        // 結合「檔名暗示」與「內容結構 (是否有 | 分隔線)」來判定
                        bool isTable = IsTableStructure(structuredText) || isTableFile;

                        segments.Add(new DocumentSegment
                        {
                            Content = structuredText,
                            // 如果被判定為表格，或是檔名說它是表格，就標記為 pdf_table
                            Type = isTable ? "pdf_table" : "pdf_text",
                            PageNumber = page.Number
                        });

                        if (isTable)
                        {
                            Console.WriteLine($"   🧩 [結構化解析] Page {page.Number} 偵測到表格結構 (Markdown化)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ PDF 解析失敗: {filePath} - {ex.Message}");
            }

            return segments;
        }

        // 🔥 核心演算法：座標轉 Markdown 表格
        private string ConvertToStructuredMarkdown(List<Word> words)
        {
            // 步驟 A: 依 Y 軸分組 (處理同一行的字)
            // PDF 的 Y 座標通常是從下往上算，且有些微浮點數誤差，所以要用 groupBy 區間
            // 這裡用 5.0 作為行高容許誤差 (Tolerance)
            var rows = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 5.0) * 5.0)
                .OrderByDescending(g => g.Key) // 從頁面上方往下讀 (Y軸大到小)
                .ToList();

            var sb = new StringBuilder();

            foreach (var row in rows)
            {
                // 步驟 B: 在同一行內，依 X 軸排序 (從左往右讀)
                var sortedWords = row.OrderBy(w => w.BoundingBox.Left).ToList();

                var lineBuilder = new StringBuilder();
                double lastRight = 0;

                for (int i = 0; i < sortedWords.Count; i++)
                {
                    var word = sortedWords[i];

                    // 步驟 C: 偵測欄位間隔
                    if (i > 0)
                    {
                        // 計算「目前字左邊」與「上個字右邊」的距離
                        double gap = word.BoundingBox.Left - lastRight;

                        // 如果距離夠大，插入 Markdown 分隔線
                        if (gap > ColumnGapThreshold)
                        {
                            lineBuilder.Append(" | "); // 👈 關鍵！強制物理隔離
                        }
                        else
                        {
                            lineBuilder.Append(" "); // 一般單字間的空白
                        }
                    }

                    lineBuilder.Append(word.Text);
                    lastRight = word.BoundingBox.Right;
                }

                sb.AppendLine(lineBuilder.ToString());
            }

            return sb.ToString();
        }

        // 判斷是否為表格 (依賴 '|' 的出現頻率)
        private bool IsTableStructure(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var lines = text.Split('\n');
            if (lines.Length < 3) return false;

            // 計算含有 "|" 的行數
            int pipeLines = lines.Count(l => l.Contains("|"));

            // 如果超過 20% 的行都有分隔線，就當作是表格
            return (double)pipeLines / lines.Length > 0.2;
        }

        public string ExtractText(string filePath)
        {
            var segs = ExtractSegments(filePath);
            var sb = new StringBuilder();
            foreach (var s in segs) sb.AppendLine(s.Content);
            return sb.ToString();
        }
    }
}