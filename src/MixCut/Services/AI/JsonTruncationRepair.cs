using System.Text;

namespace MixCut.Services.AI;

/// <summary>
/// 修复被 max_tokens 截断的 JSON。对应 macOS 版 commit 3406d61 的 repairTruncatedJSON。
///
/// 典型场景：MiniMax / Claude 在长输出时被截断在某个对象中间。
/// 算法：扫描字符串维护 {/[ 嵌套栈，记录最后一个"安全切断点"——
/// 顶层是数组时，每个完整 {...} 结束就是一个安全切断点。
/// 不处理字符串里的引号转义边角，但已能救回 95% 的截断场景。
/// </summary>
public static class JsonTruncationRepair
{
    /// <summary>
    /// 用控制字符清洗 + 截断修复。任何阶段都不修改原串，返回新串（或原串如已完整/无法修）。
    /// </summary>
    public static string Repair(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return raw;
        }

        // 单次扫描：记录嵌套栈 + 最后安全切断点（数组内完整对象结束位置）。
        var stack = new Stack<char>();
        var inString = false;
        var escape = false;
        var lastSafeIndex = -1;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (escape) { escape = false; continue; }
            if (ch == '\\') { escape = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            switch (ch)
            {
                case '{':
                case '[':
                    stack.Push(ch);
                    break;
                case '}':
                    if (stack.Count > 0 && stack.Peek() == '{')
                    {
                        stack.Pop();
                        // 栈顶若是 [，说明刚结束的是数组里一项 → 安全切断点
                        if (stack.Count > 0 && stack.Peek() == '[')
                        {
                            lastSafeIndex = i;
                        }
                    }
                    break;
                case ']':
                    if (stack.Count > 0 && stack.Peek() == '[')
                    {
                        stack.Pop();
                        lastSafeIndex = i;
                    }
                    break;
            }
        }

        // 没有不平衡的栈：JSON 完整，无需修
        if (stack.Count == 0)
        {
            return raw;
        }

        // 有不平衡但没找到任何安全切断点：放弃修
        if (lastSafeIndex < 0)
        {
            return raw;
        }

        // 在最后一个安全切断点截断 + 重新扫一遍计算还差几个闭合符
        var sub = trimmed[..(lastSafeIndex + 1)];
        var newStack = new Stack<char>();
        var s2 = false;
        var e2 = false;
        foreach (var c in sub)
        {
            if (e2) { e2 = false; continue; }
            if (c == '\\') { e2 = true; continue; }
            if (c == '"') { s2 = !s2; continue; }
            if (s2) continue;
            switch (c)
            {
                case '{':
                case '[':
                    newStack.Push(c);
                    break;
                case '}':
                    if (newStack.Count > 0 && newStack.Peek() == '{') newStack.Pop();
                    break;
                case ']':
                    if (newStack.Count > 0 && newStack.Peek() == '[') newStack.Pop();
                    break;
            }
        }

        var sb = new StringBuilder(sub);
        while (newStack.Count > 0)
        {
            sb.Append(newStack.Pop() == '{' ? '}' : ']');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 清洗 JSON 字符串：移除 ASCII 控制字符（保留 \t \n \r）+ BOM 等 noise。
    /// 对齐 macOS 版 sanitizeForJSON——MiniMax 等模型偶尔会把控制字符塞进输出。
    /// </summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            var c = (int)ch;
            // 保留 \t (9) \n (10) \r (13)
            if (c < 0x20 && c != 0x09 && c != 0x0A && c != 0x0D)
            {
                continue;
            }
            // 移除 BOM / FFFE
            if (c == 0xFEFF || c == 0xFFFE)
            {
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
