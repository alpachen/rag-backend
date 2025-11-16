using ClosedXML.Excel;
using System.Text;
using System.Text.RegularExpressions;

namespace RagPipeline.Extractors
{
    public class ExcelTextExtractor
    {
        /// <summary>
        /// 將 Excel (.xlsx) 內容轉成可供 RAG 使用的純文字。
        /// - 保留欄位名稱
        /// - 每列轉成「欄位：值」格式
        /// - 自動跳過空白列
        /// - 多工作表逐一輸出
        /// </summary>
        public string ExtractText(string excelPath)
        {
            if (!File.Exists(excelPath))
                throw new FileNotFoundException($"Excel 檔案不存在：{excelPath}");

            var sb = new StringBuilder();

            using (var workbook = new XLWorkbook(excelPath))
            {
                foreach (var sheet in workbook.Worksheets)
                {
                    sb.AppendLine($"【工作表：{sheet.Name}】");

                    var rows = sheet.RangeUsed()?.RowsUsed();
                    if (rows == null)
                        continue;

                    // 第一列為表頭
                    var headerRow = rows.First();
                    var headers = headerRow.Cells().Select(c => c.GetValue<string>().Trim()).ToList();

                    foreach (var row in rows.Skip(1)) // 從資料列開始
                    {
                        var cells = row.Cells().Select(c => c.GetValue<string>().Trim()).ToList();

                        // 判斷是否整列為空，若空則跳過
                        if (cells.All(string.IsNullOrWhiteSpace))
                            continue;

                        // 每列轉成「欄位名：值」格式
                        for (int i = 0; i < headers.Count && i < cells.Count; i++)
                        {
                            if (!string.IsNullOrWhiteSpace(cells[i]))
                            {
                                sb.AppendLine($"{headers[i]}：{cells[i]}");
                            }
                        }

                        sb.AppendLine(); // 分隔列
                    }

                    sb.AppendLine(); // 分隔工作表
                }
            }

            return Clean(sb.ToString());
        }

        /// <summary>
        /// 中度清洗：
        /// - 壓縮多餘空白
        /// - 合併換行
        /// - 去除重複空行
        /// </summary>
        private string Clean(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string text = input;

            // 換行 → 保留換行但去掉多餘空行
            text = Regex.Replace(text, @"\n{2,}", "\n\n");

            // 壓縮多餘空白
            text = Regex.Replace(text, @"[ \t]{2,}", " ");

            return text.Trim();
        }
    }
}
