using System.Globalization;
using Microsoft.Extensions.Logging;
using MixCut.Services.ASR;
using MixCut.Services.SceneDetection;

namespace MixCut.Services.AI;

/// <summary>
/// AI 语义分析服务。对应 macOS 版 AIAnalysisService。
/// 核心：本地数据驱动 + AI 语义决策，不向 AI 发送视频画面。注册为单例服务。
/// </summary>
public sealed class AIAnalysisService
{
    private readonly AIProviderManager _providerManager;
    private readonly PromptLoader _promptLoader;
    private readonly ILogger<AIAnalysisService> _logger;

    public AIAnalysisService(
        AIProviderManager providerManager, PromptLoader promptLoader, ILogger<AIAnalysisService> logger)
    {
        _providerManager = providerManager;
        _promptLoader = promptLoader;
        _logger = logger;
    }

    /// <summary>对视频进行语义分镜切分（基于本地分析数据）。</summary>
    public async Task<AISegmentationResult> AnalyzeVideoAsync(
        string videoId,
        TranscriptionResult transcript,
        IReadOnlyList<SceneBoundary> sceneBoundaries,
        VideoLocalAnalysis? localAnalysis = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        onProgress?.Invoke("正在构建分析 prompt...");
        var prompt = BuildSegmentationPrompt(videoId, transcript, sceneBoundaries, localAnalysis);

        onProgress?.Invoke("正在等待 AI 分析...");
        var provider = _providerManager.CurrentProvider();
        var result = await provider.GenerateJsonAsync<AISegmentationResult>(prompt, cancellationToken);

        onProgress?.Invoke($"分析完成，获得 {result.Segments.Count} 个分镜");
        _logger.LogInformation("AI 语义分析完成: {Count} 个分镜", result.Segments.Count);
        return result;
    }

    /// <summary>构建切分 prompt —— 核心：本地数据驱动，AI 做语义决策。</summary>
    private string BuildSegmentationPrompt(
        string videoId,
        TranscriptionResult transcript,
        IReadOnlyList<SceneBoundary> sceneBoundaries,
        VideoLocalAnalysis? localAnalysis)
    {
        var segmentTypesDefinition = _promptLoader.LoadPrompt("segment_types_definition") ?? string.Empty;

        // ASR 句子数据。
        var sentences = transcript.Sentences;
        var sentencesJson = string.Join(",\n", sentences.Select((s, i) =>
        {
            var escaped = s.Text
                .Replace("\\", "\\\\")
                .Replace("\"", "'")
                .Replace("\n", " ")
                .Replace("\r", string.Empty)
                .Replace("\t", " ");
            return $"  {{\"idx\": {i}, \"text\": \"{escaped}\", " +
                   $"\"start\": {F2(s.StartTime)}, \"end\": {F2(s.EndTime)}}}";
        }));

        // 画面切换数据。
        var scenesText = string.Join(", ", sceneBoundaries.Select(b =>
            $"{F3(b.Time)}s (置信度: {F2(b.Confidence)})"));

        // 静音段数据。
        var silenceText = localAnalysis is { SilencePeriods.Count: > 0 }
            ? string.Join(", ", localAnalysis.SilencePeriods.Select(p =>
                $"{F2(p.Start)}-{F2(p.End)}s ({p.Duration.ToString("F1", CultureInfo.InvariantCulture)}秒)"))
            : "（无静音数据）";

        // I-frame 概况。
        string iframeText;
        if (localAnalysis is { IframePositions.Count: > 0 } a)
        {
            var count = a.IframePositions.Count;
            var avgInterval = count > 1 ? a.VideoDuration / (count - 1) : a.VideoDuration;
            iframeText = $"共 {count} 个 I-frame，平均间隔 {F2(avgInterval)}s";
        }
        else
        {
            iframeText = "（无 I-frame 数据）";
        }

        var duration = localAnalysis?.VideoDuration ?? transcript.Duration;
        var fullText = transcript.Text;
        var scenesDisplay = scenesText.Length == 0 ? "（未检测到明显画面切换）" : scenesText;

        return $$"""
        # 任务：广告视频语义分镜切分

        你是一个专业的广告视频分析专家。请基于以下本地预处理数据，将视频切分为**语义独立的最小单元**。

        ⚠️ 你不会看到视频画面，所有视觉信息已由本地 FFmpeg 分析提取。

        ---

        ## 视频基础信息
        - 视频 ID: {{videoId}}
        - 总时长: {{F1(duration)}}s
        - I-frame 分布: {{iframeText}}

        ## 完整台词文本（先通读理解整体结构）
        {{fullText}}

        ## ASR 逐句转录（含精确时间戳）
        [
        {{sentencesJson}}
        ]

        ## 画面切换点（FFmpeg scene detection）
        检测到 {{sceneBoundaries.Count}} 个画面切换：
        {{scenesDisplay}}

        ## 音频静音段（FFmpeg silencedetect）
        {{silenceText}}

        ---

        ## 核心原则：语义独立性（最重要！）

        每个切出的片段必须是一个**语义上独立的最小单元**：
        - 能脱离上下文被独立理解
        - 表达一个完整的意思（一个痛点、一个卖点、一个号召等）
        - 不含两个不同主题（如"痛点描述"和"产品介绍"不能混在同一片段）
        - 排比/对比/因果等修辞结构保持完整，不在中间切断

        ## 切分规则（优先级从高到低）

        ### 规则 1：语义完整性 ⭐⭐⭐⭐⭐（最高优先级）
        - 每个片段必须只讲一件事，主题切换时必须切分
        - **绝对不能在一句话中间切断**
        - 片段的 text 必须是完整的句子，从 ASR 数据中精确提取
        - start_time 和 end_time 必须精确对应该片段台词在 ASR 中的时间范围

        ### 规则 2：台词与时间严格对应 ⭐⭐⭐⭐
        - text 字段必须是 start_time 到 end_time 之间 ASR 句子的原文拼接
        - 不要编造、改写或遗漏台词，必须从 ASR 数据中原样提取
        - start_time = 该片段第一个 ASR 句子的 start
        - end_time = 该片段最后一个 ASR 句子的 end

        ### 规则 3：画面切换对齐 ⭐⭐⭐
        - 切点应尽量对齐画面切换点（±0.5s 内）
        - 避免一个镜头的画面混入另一个片段

        ### 规则 4：静音段优先 ⭐⭐
        - 切点优先落在静音段内（说话间的停顿）

        ---

        ## 切分粒度

        - 目标片段时长：3-15 秒（语义完整性优先于时长）
        - **硬性上限：任何单个片段不得超过 18 秒**。超过 15 秒时必须寻找内部的语义切分点拆分
        - 短视频（< 40s）：平均 5-8s/片段
        - 中视频（40-80s）：平均 6-12s/片段
        - 长视频（> 80s）：平均 8-15s/片段
        - 宁可多切几个短片段，也不要把不同主题混在一起

        ---

        ## 片段类型定义

        {{segmentTypesDefinition}}

        ⚠️ types 是一个数组，每个元素必须是以下 11 个字符串之一（精确匹配）：
        "噱头引入"、"痛点"、"产品方案"、"效果展示"、"信任背书"、"价格对比"、"活动福利"、"行动号召"、"产品定位"、"产品使用教育"、"过渡"

        一个片段可以同时具有多个语义类型。例如一段既在展示效果又在做信任背书，则 types: ["效果展示", "信任背书"]。
        至少标注 1 个类型，通常 1-2 个，最多 3 个。第一个是主类型。

        ## 位置类型

        position 字段必须是以下 3 个字符串之一，表示该片段在**原视频**中的位置：
        - "开头"：位于视频前 20%，承担吸引注意力功能
        - "中间"：核心内容区域
        - "结尾"：位于视频后 20%，承担驱动转化功能

        ---

        ## 输出格式（严格 JSON）

        ```json
        {
          "video_id": "{{videoId}}",
          "total_duration": {{F1(duration)}},
          "total_segments": <切分数量>,
          "segments": [
            {
              "id": "seg_001",
              "start_time": <从ASR句子的start精确取值>,
              "end_time": <从ASR句子的end精确取值>,
              "duration": <end_time - start_time>,
              "text": "<从ASR原文精确提取的该时间段完整台词>",
              "types": ["<主类型>", "<可选次类型>"],
              "position": "<开头|中间|结尾>",
              "data_quality": {
                "score": <0-10>,
                "reasoning": "<评分理由>"
              },
              "keywords": ["关键词1", "关键词2", "关键词3"]
            }
          ]
        }
        ```

        ## 切分步骤

        1. 通读完整台词，识别整体内容结构和主题切换点
        2. 将 ASR 句子按语义主题分组，每组成为一个片段
        3. 每个片段的 start_time/end_time 直接取自 ASR 句子的时间戳
        4. 每个片段的 text 是该组 ASR 句子原文的拼接
        5. 标注语义类型（从 11 种中精确选择）和位置类型
        6. 自检：确认每个片段语义独立、台词完整、时间正确

        请直接输出 JSON，不要添加其他说明文字。
        """;
    }

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
}
