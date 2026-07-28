using System.IO;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.Utilities;

namespace MixCut.Services.Agent;

/// <summary>分镜修改工具（issue #23 第二阶段）：编号寻址解析与 9 个 handler。</summary>
public sealed partial class McpToolHandlers
{
    /// <summary>8 个分镜工具共用的寻址参数：segment_ids[] 或 project_id+video_no+segment_nos[] 二选一。</summary>
    private sealed record SelectorArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds);

    /// <summary>
    /// all-or-nothing 定位：全部找到才返回（tracked 实体，含 Video/SegmentDubs 导航），
    /// 任何一个找不到 → 整个调用不执行（§2.5 分镜寻址协议）。
    /// </summary>
    private static async Task<List<Segment>> ResolveSegmentsAsync(MixCutDbContext db, SelectorArgs sel)
    {
        if (sel.SegmentIds is { Count: > 0 } ids)
        {
            var result = new List<Segment>();
            var missing = new List<string>();
            foreach (var idString in ids)
            {
                if (!Guid.TryParse(idString, out var uuid))
                {
                    throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"segment_id 不是合法 UUID：{idString}");
                }
                var seg = await db.Segments
                    .Include(s => s.Video)
                    .Include(s => s.SegmentDubs)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(s => s.Id == uuid);
                if (seg is not null)
                {
                    result.Add(seg);
                }
                else
                {
                    missing.Add(idString);
                }
            }
            if (missing.Count > 0)
            {
                throw new ToolFailure(AgentToolErrorCode.SegmentNotFound, $"找不到分镜：{string.Join("、", missing)}");
            }
            return result;
        }

        if (sel.ProjectId is null || sel.VideoNo is null || sel.SegmentNos is not { Count: > 0 })
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "必须提供 segment_ids，或 project_id + video_no + segment_nos");
        }
        var project = await FetchProjectAsync(db, sel.ProjectId, includeDubs: true);
        var videos = OrderedVideos(project);
        var vno = sel.VideoNo.Value;
        if (vno < 1 || vno > videos.Count)
        {
            throw new ToolFailure(AgentToolErrorCode.VideoNotFound, $"video_no {vno} 超出范围（项目共 {videos.Count} 个视频）");
        }
        var ordered = OrderedSegments(videos[vno - 1]);
        var outOfRange = sel.SegmentNos.Where(n => n < 1 || n > ordered.Count).ToList();
        if (outOfRange.Count > 0)
        {
            throw new ToolFailure(
                AgentToolErrorCode.SegmentNotFound,
                $"视频 {vno} 共 {ordered.Count} 个分镜，找不到编号：{string.Join("、", outOfRange)}");
        }
        return sel.SegmentNos.Select(n => ordered[n - 1]).ToList();
    }

    // ==== 标签 ====

    private sealed record UpdateTagsArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("add_semantic_types")] List<string>? AddSemanticTypes,
        [property: JsonPropertyName("remove_semantic_types")] List<string>? RemoveSemanticTypes,
        [property: JsonPropertyName("set_semantic_types")] List<string>? SetSemanticTypes,
        [property: JsonPropertyName("position_type")] string? PositionType);

    private async Task<Outcome> UpdateSegmentTagsAsync(string argsJson)
    {
        var args = Parse<UpdateTagsArgs>(argsJson);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(
            db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));

        var opCount = new[] { args.AddSemanticTypes, args.RemoveSemanticTypes, args.SetSemanticTypes }
            .Count(x => x is not null);
        if (opCount > 1)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "add/remove/set_semantic_types 只能提供一个");
        }
        if (opCount == 0 && args.PositionType is null)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "至少提供一种修改：语义类型或 position_type");
        }

        List<SemanticType> ParseTypes(List<string> raw) => raw.Select(StrictSemanticType).ToList();

        PositionType? position = null;
        if (args.PositionType is { } p)
        {
            position = StrictPositionType(p);
        }

        var results = new List<Dictionary<string, object?>>();
        foreach (var seg in segments)
        {
            var note = "已修改";
            if (args.AddSemanticTypes is { } add)
            {
                var types = ParseTypes(add);
                var current = seg.SemanticTypes.ToList();
                foreach (var t in types.Where(t => !current.Contains(t)))
                {
                    current.Add(t);
                }
                seg.SemanticTypes = current;
            }
            else if (args.RemoveSemanticTypes is { } remove)
            {
                var types = ParseTypes(remove);
                var remaining = seg.SemanticTypes.Where(t => !types.Contains(t)).ToList();
                if (remaining.Count == 0)
                {
                    note = "跳过：至少要保留 1 个语义类型";
                }
                else
                {
                    seg.SemanticTypes = remaining;
                }
            }
            else if (args.SetSemanticTypes is { } set)
            {
                var types = ParseTypes(set);
                if (types.Count == 0)
                {
                    note = "跳过：至少要保留 1 个语义类型";
                }
                else
                {
                    seg.SemanticTypes = types;
                }
            }
            if (position is { } pos)
            {
                seg.PositionType = pos;
            }
            results.Add(new Dictionary<string, object?>
            {
                ["segment_index"] = seg.SegmentIndex,
                ["note"] = note,
                ["semantic_types"] = seg.SemanticTypes.Select(t => t.ToLabel()).ToList(),
                ["position_type"] = seg.PositionType.ToLabel(),
            });
        }
        await db.SaveChangesAsync();
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?> { ["results"] = results });
    }

    private static SemanticType StrictSemanticType(string raw)
    {
        foreach (var t in SemanticTypeExtensions.All)
        {
            if (t.ToLabel() == raw.Trim())
            {
                return t;
            }
        }
        var legal = string.Join("、", SemanticTypeExtensions.All.Select(t => t.ToLabel()));
        throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"非法语义类型「{raw}」，合法值：{legal}");
    }

    private static PositionType StrictPositionType(string raw)
    {
        foreach (var t in PositionTypeExtensions.All)
        {
            if (t.ToLabel() == raw.Trim())
            {
                return t;
            }
        }
        throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"非法位置「{raw}」，合法值：开头、中间、结尾");
    }

    // ==== 字幕模式 ====

    private sealed record SubtitleModeArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("font_ratio")] double? FontRatio,
        [property: JsonPropertyName("mask_y")] double? MaskY,
        [property: JsonPropertyName("mask_height")] double? MaskHeight);

    private async Task<Outcome> SetSubtitleModeAsync(string argsJson)
    {
        var args = Parse<SubtitleModeArgs>(argsJson);
        var mode = Require(args.Mode, "mode");
        if (mode is not ("直接烧录" or "模糊虚化" or "纯色遮挡"))
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "mode 只认三档：直接烧录、模糊虚化、纯色遮挡");
        }
        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(
            db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));

        var results = new List<Dictionary<string, object?>>();
        foreach (var seg in segments)
        {
            if (seg.IsVoiceLocked)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["segment_index"] = seg.SegmentIndex,
                    ["note"] = "已跳过（保留原声，不加字幕）",
                });
                continue;
            }
            seg.SubtitleTreatment = mode switch
            {
                "直接烧录" => SubtitleTreatment.Direct,
                "模糊虚化" => SubtitleTreatment.Blur,
                _ => SubtitleTreatment.Solid,
            };
            if (args.FontRatio is { } ratio)
            {
                var clamped = SubtitleFontSize.Clamp(ratio);
                seg.SubtitleFontRatio = clamped;
                // 记为「新分镜默认值」偏好（对齐 mac SubtitleFontSize.rememberPreferred）
                _settings.SubtitleFontRatio = clamped;
                ViewModels.SubtitleFontState.Shared.Ratio = clamped;
            }
            if (args.MaskY is not null || args.MaskHeight is not null)
            {
                var rect = seg.MaskRect;
                var y = args.MaskY ?? rect.Y;
                var h = args.MaskHeight ?? rect.Height;
                seg.MaskRect = new SubtitleMaskRect(rect.X, y, rect.Width, h).Clamped();
            }
            results.Add(new Dictionary<string, object?>
            {
                ["segment_index"] = seg.SegmentIndex,
                ["note"] = "已修改",
                ["subtitle_mode"] = SubtitleModeName(seg),
                ["font_ratio"] = EffectiveFontRatio(seg),
            });
        }
        await db.SaveChangesAsync();
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?> { ["results"] = results });
    }

    // ==== 保留原声 / 变体参与 ====

    private sealed record VoiceKeepArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("keep_original")] bool? KeepOriginal,
        [property: JsonPropertyName("original_in_combination")] bool? OriginalInCombination);

    private async Task<Outcome> SetVoiceKeepOriginalAsync(string argsJson)
    {
        var args = Parse<VoiceKeepArgs>(argsJson);
        if (args.KeepOriginal is null)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "缺少必填参数 keep_original");
        }
        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(
            db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));
        foreach (var seg in segments)
        {
            seg.IsVoiceLocked = args.KeepOriginal.Value;
            if (args.OriginalInCombination is { } flag)
            {
                seg.OriginalParticipatesInCombination = flag;
            }
        }
        await db.SaveChangesAsync();
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["updated"] = segments.Count,
            ["keep_original"] = args.KeepOriginal.Value,
        });
    }

    private sealed record DubParticipationArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("variant_indexes")] List<int>? VariantIndexes,
        [property: JsonPropertyName("participates")] bool? Participates);

    private async Task<Outcome> SetDubParticipationAsync(string argsJson)
    {
        var args = Parse<DubParticipationArgs>(argsJson);
        if (args.VariantIndexes is not { Count: > 0 })
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "缺少必填参数 variant_indexes");
        }
        if (args.Participates is null)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "缺少必填参数 participates");
        }
        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(
            db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));

        var results = new List<Dictionary<string, object?>>();
        foreach (var seg in segments)
        {
            var variants = seg.EffectiveDubVariants;
            var touched = new List<int>();
            var missing = new List<int>();
            foreach (var idx in args.VariantIndexes)
            {
                var matches = variants.Where(d => d.TextVariantIndex == idx).ToList();
                if (matches.Count == 0)
                {
                    missing.Add(idx);
                }
                else
                {
                    foreach (var d in matches)
                    {
                        d.ParticipatesInCombination = args.Participates.Value;
                    }
                    touched.Add(idx);
                }
            }
            results.Add(new Dictionary<string, object?>
            {
                ["segment_index"] = seg.SegmentIndex,
                ["updated_variants"] = touched,
                ["missing_variants"] = missing,
            });
        }
        await db.SaveChangesAsync();
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["results"] = results,
            ["participates"] = args.Participates.Value,
        });
    }

    // ==== 边界调整 ====

    private sealed record BoundaryArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("start_delta_frames")] int? StartDeltaFrames,
        [property: JsonPropertyName("end_delta_frames")] int? EndDeltaFrames,
        [property: JsonPropertyName("start_frame")] int? StartFrame,
        [property: JsonPropertyName("end_frame")] int? EndFrame);

    /// <summary>
    /// 按帧批量调整分镜边界（§2.7.1）。与 UI 手动微调同一套钳制（0 ≤ start，end ≤ 总帧，最短 2 帧），
    /// 但按规格<b>不做相邻分镜校验</b>——允许重叠/留空洞（混剪时每分镜独立取自己区间）。
    /// 副作用链：帧变更 → 同步秒级派生字段 → 清替换画面缓存 → 按新时间窗从 ASR 重配台词 →
    /// 保存 → 起点变化的分镜重抽首帧缩略图。已生成配音的变体命中 stale 判定时汇总提醒。
    /// </summary>
    private async Task<Outcome> AdjustSegmentBoundaryAsync(string argsJson)
    {
        var args = Parse<BoundaryArgs>(argsJson);
        var hasDelta = args.StartDeltaFrames is not null || args.EndDeltaFrames is not null;
        var hasAbsolute = args.StartFrame is not null || args.EndFrame is not null;
        if (!hasDelta && !hasAbsolute)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "必须提供 delta（start/end_delta_frames）或绝对值（start/end_frame）");
        }
        if (hasDelta && hasAbsolute)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "delta 与绝对值不能混用");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(
            db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));
        if (hasAbsolute && segments.Count > 1)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "绝对帧值只支持单个分镜，批量请用 delta");
        }

        var results = new List<Dictionary<string, object?>>();
        var staleDubs = new List<Dictionary<string, object?>>();
        var rethumbTargets = new List<(Guid SegmentId, string VideoPath, int StartFrame, double Fps)>();
        foreach (var seg in segments)
        {
            var fps = seg.EffectiveFps;
            var video = seg.Video;
            if (video is null || fps <= 0)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["segment_index"] = seg.SegmentIndex,
                    ["note"] = "跳过：视频帧率缺失",
                });
                continue;
            }
            var maxFrame = video.Duration > 0 ? FrameTime.SecondsToFrame(video.Duration, fps) : int.MaxValue;
            var targetStart = args.StartFrame ?? (seg.StartFrame + (args.StartDeltaFrames ?? 0));
            var targetEnd = args.EndFrame ?? (seg.EndFrame + (args.EndDeltaFrames ?? 0));
            // 与 UI 相同的钳制：0 ≤ start，end ≤ 视频总帧，最短 2 帧；钳制后静默采用实际值
            targetEnd = Math.Min(maxFrame, targetEnd);
            targetStart = Math.Max(0, targetStart);
            if (targetEnd < targetStart + 2)
            {
                targetEnd = Math.Min(maxFrame, targetStart + 2);
            }
            if (targetStart > targetEnd - 2)
            {
                targetStart = Math.Max(0, targetEnd - 2);
            }

            var oldStart = seg.StartFrame;
            var oldEnd = seg.EndFrame;
            seg.SetBoundsFrames(targetStart, targetEnd, fps);
            // 按新时间窗从 ASR 字级时间戳重配台词（非空才覆盖，与 UI ReExtractText 同规则）
            var matched = string.Concat(video.AsrWords
                .Where(w => (w.Start + w.End) / 2 >= seg.StartTime && (w.Start + w.End) / 2 < seg.EndTime)
                .Select(w => w.Word)).Trim();
            if (matched.Length > 0)
            {
                seg.Text = matched;
            }
            // 帧数已变 → 旧 AI 替换画面片与新边界不再匹配，作废之
            if (!string.IsNullOrEmpty(seg.ReplacedPictureVideoPath))
            {
                seg.InvalidateReplacedPicture();
            }
            if (seg.StartFrame != oldStart && !string.IsNullOrEmpty(video.LocalPath))
            {
                rethumbTargets.Add((seg.Id, video.LocalPath, seg.StartFrame, fps));
            }
            results.Add(new Dictionary<string, object?>
            {
                ["segment_index"] = seg.SegmentIndex,
                ["start_frame"] = new Dictionary<string, object?> { ["old"] = oldStart, ["new"] = seg.StartFrame },
                ["end_frame"] = new Dictionary<string, object?> { ["old"] = oldEnd, ["new"] = seg.EndFrame },
                ["duration_sec"] = seg.Duration,
                ["text"] = seg.Text,
            });
            // 配音过期提醒：只对已生成音频的变体；没有变体的分镜不出现在提醒里
            foreach (var dub in seg.EffectiveDubVariants)
            {
                if (!IsStaleDub(dub, seg))
                {
                    continue;
                }
                var oldDur = (dub.GeneratedForEndFrame - dub.GeneratedForStartFrame) / fps;
                staleDubs.Add(new Dictionary<string, object?>
                {
                    ["segment_index"] = seg.SegmentIndex,
                    ["variant_index"] = dub.TextVariantIndex,
                    ["dub_duration_sec"] = Math.Round(oldDur, 2),
                    ["segment_duration_sec"] = Math.Round(seg.Duration, 2),
                });
            }
        }
        await db.SaveChangesAsync();

        // issue #19 同款：起点变化的分镜按新首帧重抽缩略图（文件名带 startFrame 保证路径唯一）
        foreach (var target in rethumbTargets)
        {
            _ = RethumbFirstFrameAsync(target.SegmentId, target.VideoPath, target.StartFrame, target.Fps);
        }
        AgentGateway.NotifyUiReload();

        var payload = new Dictionary<string, object?> { ["results"] = results };
        if (staleDubs.Count > 0)
        {
            payload["stale_dubs"] = staleDubs;
            payload["stale_warning"] = "以上分镜已生成的配音是按旧时长合成的，与新时长不匹配，导出会静默使用旧音频。请把此情况告知用户，由用户决定是否用 generate_voice_variants 重新生成；不要自作主张重跑。";
        }
        return Success(payload);
    }

    private async Task RethumbFirstFrameAsync(Guid segId, string videoPath, int startFrame, double fps)
    {
        try
        {
            var timeSec = FrameTime.FrameToSeconds(startFrame, fps);
            var thumbDir = Path.Combine(AppPaths.Root, "Thumbnails");
            Directory.CreateDirectory(thumbDir);
            var newPath = Path.Combine(thumbDir, $"seg_{segId}_f{startFrame}.jpg");
            if (!File.Exists(newPath))
            {
                await _ffmpeg.GenerateThumbnailAsync(videoPath, newPath, timeSec);
            }
            if (!File.Exists(newPath))
            {
                return;
            }
            await using var db = await _dbFactory.CreateDbContextAsync();
            var seg = await db.Segments.FirstOrDefaultAsync(x => x.Id == segId);
            if (seg is not null)
            {
                seg.ThumbnailPath = newPath;
                await db.SaveChangesAsync();
            }
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(newPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP] 分镜 {Segment} 首帧缩略图重抽失败", segId);
        }
    }

    // ==== 删除分镜 ====

    private async Task<Outcome> DeleteSegmentsAsync(string argsJson)
    {
        var sel = Parse<SelectorArgs>(argsJson);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var segments = await ResolveSegmentsAsync(db, sel);
        var affectedSchemes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var seg in segments)
        {
            // 与 UI 删除同语义：先删方案槽位记录（收集受影响方案名），再删分镜（配音/物理镜头级联删除）
            var slots = await db.SchemeSegments
                .Include(ss => ss.Scheme)
                .Where(ss => ss.SegmentId == seg.Id)
                .ToListAsync();
            foreach (var slot in slots)
            {
                if (slot.Scheme?.Name is { } name)
                {
                    affectedSchemes.Add(name);
                }
                db.SchemeSegments.Remove(slot);
            }
            db.Segments.Remove(seg);
        }
        await db.SaveChangesAsync();
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["deleted"] = segments.Count,
            ["affected_schemes"] = affectedSchemes.ToList(),
            ["note"] = affectedSchemes.Count == 0
                ? "无方案受影响"
                : "以上方案因分镜被删而缺少槽位，导出前请检查",
        });
    }

    // ==== 自选组合 ====

    private sealed record CustomSchemeEntry(
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_no")] int? SegmentNo,
        [property: JsonPropertyName("segment_id")] string? SegmentId);

    private sealed record CustomSchemeArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("segments")] List<CustomSchemeEntry>? Segments);

    private async Task<Outcome> CreateCustomSchemeAsync(string argsJson)
    {
        var args = Parse<CustomSchemeArgs>(argsJson);
        if (args.Segments is not { Count: > 0 })
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "segments 不能为空");
        }
        Project project;
        var ordered = new List<Segment>();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            project = await FetchProjectAsync(db, Require(args.ProjectId, "project_id"));
            for (var i = 0; i < args.Segments.Count; i++)
            {
                var entry = args.Segments[i];
                if (entry.SegmentId is { } sid)
                {
                    ordered.AddRange(await ResolveSegmentsAsync(
                        db, new SelectorArgs(null, null, null, new List<string> { sid })));
                }
                else if (entry is { VideoNo: { } vno, SegmentNo: { } sno })
                {
                    ordered.AddRange(await ResolveSegmentsAsync(
                        db, new SelectorArgs(args.ProjectId, vno, new List<int> { sno }, null)));
                }
                else
                {
                    throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"第 {i + 1} 项必须提供 segment_id 或 video_no+segment_no");
                }
            }
        }
        if (ordered.Count < 2)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "自定义组合至少需要 2 个分镜");
        }

        // 走 UI 同一路径：占位方案落「自定义组合」策略组 → AI 反推元信息（失败不阻断、保默认名）
        _schemeVM.LoadSchemes(project);
        var scheme = await _schemeVM.CreateCustomSchemeAsync(ordered, project)
            ?? throw new ToolFailure(AgentToolErrorCode.InvalidArgument, _schemeVM.ErrorMessage ?? "自定义组合创建失败");

        if (!string.IsNullOrWhiteSpace(args.Name))
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tracked = await db.Schemes.FirstOrDefaultAsync(s => s.Id == scheme.Id);
            if (tracked is not null)
            {
                tracked.Name = args.Name!;
                await db.SaveChangesAsync();
                scheme.Name = args.Name!;
            }
        }
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["scheme_id"] = scheme.Id,
            ["name"] = scheme.Name,
            ["segment_count"] = ordered.Count,
            ["total_duration_sec"] = ordered.Sum(s => s.Duration),
        });
    }

    // ==== 分镜片段导出 ====

    private sealed record ExportSegmentsArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_no")] int? VideoNo,
        [property: JsonPropertyName("segment_nos")] List<int>? SegmentNos,
        [property: JsonPropertyName("segment_ids")] List<string>? SegmentIds,
        [property: JsonPropertyName("output_dir")] string? OutputDir);

    private async Task<Outcome> ExportSegmentsAsync(string argsJson)
    {
        var args = Parse<ExportSegmentsArgs>(argsJson);
        var outputDir = Require(args.OutputDir, "output_dir");
        ValidateOutputDirectory(outputDir);

        List<BatchExportItem> items;
        Guid projectId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var segments = await ResolveSegmentsAsync(
                db, new SelectorArgs(args.ProjectId, args.VideoNo, args.SegmentNos, args.SegmentIds));
            items = new List<BatchExportItem>();
            foreach (var seg in segments)
            {
                var video = seg.Video
                    ?? throw new ToolFailure(AgentToolErrorCode.VideoNotFound, $"分镜 {seg.SegmentIndex} 缺少所属视频");
                // 补齐视频的全部分镜以算稳定编号（Video 导航可能只加载了本分镜）
                var allSegs = await db.Segments.AsNoTracking()
                    .Where(s => s.VideoId == video.Id)
                    .OrderBy(s => s.StartFrame)
                    .Select(s => s.Id)
                    .ToListAsync();
                var sequence = allSegs.IndexOf(seg.Id) + 1;
                items.Add(new BatchExportItem(
                    seg.Id,
                    video.LocalPath,
                    Path.GetFileNameWithoutExtension(video.Name),
                    seg.StartFrame,
                    seg.EndFrame,
                    seg.EffectiveFps > 0 ? seg.EffectiveFps : video.Fps,
                    sequence));
            }
            projectId = segments
                .Select(s => s.Video)
                .Where(v => v is not null)
                .SelectMany(v => v!.ProjectVideos)
                .Select(pv => pv.ProjectId)
                .FirstOrDefault(id => id is not null) ?? Guid.Empty;
        }

        EnsureNoActiveJob();
        var job = _jobs.Begin("export_segments", projectId);
        var exportItems = items;
        _ = RunJobAsync(job.Id, async () =>
        {
            var (succeeded, failed) = await _batchExportService.ExportAllAsync(exportItems, outputDir);
            _jobs.FinishWithResult(job.Id, AgentJson.Encode(new Dictionary<string, object?>
            {
                ["succeeded"] = succeeded,
                ["output_dir"] = outputDir,
                ["failed"] = failed.Select(f => new Dictionary<string, object?>
                {
                    ["name"] = $"{f.Item1.SourceVideoName}_{f.Item1.SequenceNumber}",
                    ["reason"] = f.Item2,
                }).ToList(),
            }));
        });
        return Success(new Dictionary<string, object?> { ["job_id"] = job.Id });
    }

    // ==== 声音变体生成 ====

    private async Task<Outcome> GenerateVoiceVariantsAsync(string argsJson)
    {
        var sel = Parse<SelectorArgs>(argsJson);
        List<(Guid SegmentId, string SegmentIndex, Guid VideoId)> eligible;
        var skipped = new List<Dictionary<string, object?>>();
        Guid projectId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var segments = await ResolveSegmentsAsync(db, sel);
            eligible = new List<(Guid, string, Guid)>();
            foreach (var seg in segments)
            {
                if (seg.IsVoiceLocked || string.IsNullOrEmpty(seg.Text))
                {
                    skipped.Add(new Dictionary<string, object?>
                    {
                        ["segment_index"] = seg.SegmentIndex,
                        ["reason"] = seg.IsVoiceLocked ? "保留原声" : "台词为空",
                    });
                }
                else if (seg.VideoId is { } vid)
                {
                    eligible.Add((seg.Id, seg.SegmentIndex, vid));
                }
            }
            if (eligible.Count == 0)
            {
                throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "所选分镜均无法生成变体（保留原声或台词为空）");
            }
            projectId = segments
                .Select(s => s.Video)
                .Where(v => v is not null)
                .SelectMany(v => v!.ProjectVideos)
                .Select(pv => pv.ProjectId)
                .FirstOrDefault(id => id is not null) ?? Guid.Empty;
        }

        EnsureNoActiveJob();
        var job = _jobs.Begin("generate_voice_variants", projectId);
        _ = RunJobAsync(job.Id, async () =>
        {
            var perSegment = new List<Dictionary<string, object?>>();
            // 按视频分组：每视频先确保克隆音色（已有直接复用；否则分离人声→注册克隆），
            // 克隆失败则该视频全部分镜记失败（原因=克隆错误）
            foreach (var group in eligible.GroupBy(e => e.VideoId))
            {
                _dubbingVM.ErrorMessage = null;
                var cloneOk = await _dubbingVM.EnsureClonedVoiceAsync(group.Key);
                if (!cloneOk)
                {
                    var reason = _dubbingVM.ErrorMessage ?? "克隆原声失败";
                    foreach (var entry in group)
                    {
                        perSegment.Add(new Dictionary<string, object?>
                        {
                            ["segment_index"] = entry.SegmentIndex,
                            ["ok"] = false,
                            ["reason"] = reason,
                        });
                    }
                    continue;
                }
                foreach (var entry in group)
                {
                    _dubbingVM.ErrorMessage = null;
                    await _dubbingVM.RewriteSegmentAsync(entry.SegmentId);
                    if (_dubbingVM.ErrorMessage is { } err)
                    {
                        perSegment.Add(new Dictionary<string, object?>
                        {
                            ["segment_index"] = entry.SegmentIndex,
                            ["ok"] = false,
                            ["reason"] = err,
                        });
                    }
                    else
                    {
                        int variantCount;
                        await using (var db = await _dbFactory.CreateDbContextAsync())
                        {
                            var seg = await db.Segments
                                .Include(s => s.Video)
                                .Include(s => s.SegmentDubs)
                                .AsSplitQuery()
                                .AsNoTracking()
                                .FirstAsync(s => s.Id == entry.SegmentId);
                            variantCount = seg.EffectiveDubVariants.Count;
                        }
                        perSegment.Add(new Dictionary<string, object?>
                        {
                            ["segment_index"] = entry.SegmentIndex,
                            ["ok"] = true,
                            ["variant_count"] = variantCount,
                        });
                    }
                }
            }
            var summary = new Dictionary<string, object?> { ["results"] = perSegment };
            if (skipped.Count > 0)
            {
                summary["skipped"] = skipped;
            }
            _jobs.FinishWithResult(job.Id, AgentJson.Encode(summary));
            AgentGateway.NotifyUiReload();
        });
        return Success(new Dictionary<string, object?>
        {
            ["job_id"] = job.Id,
            ["note"] = "生成中：克隆原声→AI 改写→合成→字幕对齐，用 get_job 轮询",
        });
    }
}
