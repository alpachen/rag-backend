using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RagDataPipeline.Services.Extractors
{
    /// <summary>
    /// 更智能的 PDF 段落偵測與清理：
    /// - 合併同段落
    /// - 移除 header/footer
    /// - 用空行 / 標題 / 大寫行當做分段點
    /// </summary>
    public static class PdfParagraphExtractor
    {
        public static List<string> ExtractParagraphs(string raw)
        {
            var lines = raw.Split('\n');
            var paragraphs = new List<string>();
            var buffer = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // 過濾 header/footer/page number
                if (IsJunk(line))
                    continue;

                if (string.IsNullOrWhiteSpace(line))
                {
                    // 遇到空行 → 段落結束
                    Flush();
                    continue;
                }

                // 大標題（全大寫 + 短句）
                if (IsTitle(line))
                {
                    Flush();
                    buffer.Add(line);
                    Flush();
                    continue;
                }

                buffer.Add(line);
            }

            Flush();
            return paragraphs;

            void Flush()
            {
                if (buffer.Count == 0) return;
                paragraphs.Add(string.Join(" ", buffer));
                buffer.Clear();
            }
        }

        private static bool IsJunk(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return true;

            // Page numbers
            if (Regex.IsMatch(line, @"^Page\s*\d+|^\d+$|^\d+/\d+$"))
                return true;

            // Header/footer 常見形式
            if (Regex.IsMatch(line, @"(公司|資訊安全|機密|Confidential)", RegexOptions.IgnoreCase)
                && line.Length <= 30)
                return true;

            return false;
        }

        private static bool IsTitle(string line)
        {
            if (line.Length <= 50 && Regex.IsMatch(line, @"^[A-Z0-9\s\-]+$"))
                return true;

            return false;
        }
    }
}

