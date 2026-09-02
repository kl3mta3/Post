namespace Post.Core;

public readonly record struct SegmentPosition(int SegmentIndex, TimeSpan SourceTime);
public readonly record struct ActiveTimelinePlacement(TimelineLayer Layer, TimelinePlacement Placement, TimeSpan ClipSequenceTime, SegmentPosition SourcePosition);
public enum PlacementDropAction { Moved, SnappedBefore, SnappedAfter, Reverted, Swapped }

public static class TimelineOperations
{
    public static TimelinePlacement AddPlacement(TimelineLayer layer, ClipItem clip, TimeSpan start)
    {
        var placement = new TimelinePlacement { Clip = clip, Start = start < TimeSpan.Zero ? TimeSpan.Zero : start };
        layer.Placements.Add(placement);
        SortPlacements(layer);
        return placement;
    }

    public static bool MovePlacement(TimelineComposition composition, Guid placementId, Guid targetLayerId, TimeSpan start)
    {
        var sourceLayer = composition.Layers.FirstOrDefault(layer => layer.Placements.Any(item => item.Id == placementId));
        var targetLayer = composition.Layers.FirstOrDefault(layer => layer.Id == targetLayerId);
        var placement = sourceLayer?.Placements.FirstOrDefault(item => item.Id == placementId);
        if (sourceLayer is null || targetLayer is null || placement is null) return false;
        sourceLayer.Placements.Remove(placement);
        placement.Start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        targetLayer.Placements.Add(placement);
        SortPlacements(sourceLayer); SortPlacements(targetLayer);
        return true;
    }

    public static bool RemovePlacement(TimelineComposition composition, Guid placementId)
    {
        foreach (var layer in composition.Layers)
        {
            var placement = layer.Placements.FirstOrDefault(item => item.Id == placementId);
            if (placement is not null) return layer.Placements.Remove(placement);
        }
        return false;
    }

    public static TimelinePlacement? SplitPlacement(TimelineComposition composition, Guid placementId, TimeSpan projectTime)
    {
        var layer = composition.Layers.FirstOrDefault(candidate => candidate.Placements.Any(item => item.Id == placementId));
        var placement = layer?.Placements.FirstOrDefault(item => item.Id == placementId);
        if (layer is null || placement is null) return null;
        var offset = projectTime - placement.Start;
        if (offset <= TimeSpan.Zero || offset >= placement.Duration) return null;
        var oldDuration = placement.Duration;
        placement.Length = offset;
        var right = new TimelinePlacement { Clip = placement.Clip, Start = projectTime, InPoint = placement.InPoint + offset, Length = oldDuration - offset };
        foreach (var property in Enum.GetValues<KeyframeProperty>())
        {
            var fallback = property == KeyframeProperty.Scale || property == KeyframeProperty.Opacity || property == KeyframeProperty.Volume ? 1 : 0;
            var boundary = KeyframeEvaluator.Evaluate(placement.Keyframes, property, offset, fallback);
            var active = placement.Keyframes.Where(item => item.Property == property).OrderBy(item => item.Offset).LastOrDefault(item => item.Offset <= offset);
            if (placement.Keyframes.Any(item => item.Property == property))
            {
                KeyframeEvaluator.Upsert(placement.Keyframes, property, offset, boundary, active?.Interpolation ?? KeyframeInterpolation.Linear, oldDuration);
                KeyframeEvaluator.Upsert(right.Keyframes, property, TimeSpan.Zero, boundary, active?.Interpolation ?? KeyframeInterpolation.Linear, right.Duration);
            }
        }
        foreach (var keyframe in placement.Keyframes.Where(item => item.Offset > offset).ToArray())
        {
            right.Keyframes.Add(new AnimationKeyframe { Property = keyframe.Property, Offset = keyframe.Offset - offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation });
            placement.Keyframes.Remove(keyframe);
        }
        layer.Placements.Add(right); SortPlacements(layer); return right;
    }

    public static bool MoveLayer(TimelineComposition composition, Guid layerId, int targetIndex)
    {
        var oldIndex = composition.Layers.ToList().FindIndex(layer => layer.Id == layerId);
        if (oldIndex < 0) return false;
        targetIndex = Math.Clamp(targetIndex, 0, composition.Layers.Count - 1);
        if (oldIndex == targetIndex) return false;
        composition.Layers.Move(oldIndex, targetIndex); return true;
    }

    public static bool RemoveLayer(TimelineComposition composition, Guid layerId)
    {
        var layer = composition.Layers.FirstOrDefault(item => item.Id == layerId);
        return layer is not null && composition.Layers.Remove(layer);
    }

    public static TimelineLayer? DuplicateLayer(TimelineComposition composition, Guid layerId)
    {
        var sourceIndex = composition.Layers.ToList().FindIndex(layer => layer.Id == layerId);
        if (sourceIndex < 0) return null;
        var source = composition.Layers[sourceIndex];
        var copy = CreateLayerCopy(composition, source);
        foreach (var placement in source.Placements) copy.Placements.Add(ClonePlacement(placement));
        foreach (var graphic in source.Graphics) copy.Graphics.Add(CloneGraphic(graphic));
        composition.Layers.Insert(sourceIndex + 1, copy);
        return copy;
    }

    public static TimelineLayer? DuplicatePlacementToNewLayer(TimelineComposition composition, Guid placementId)
    {
        var sourceIndex = composition.Layers.ToList().FindIndex(layer => layer.Placements.Any(item => item.Id == placementId));
        if (sourceIndex < 0) return null;
        var source = composition.Layers[sourceIndex];
        var placement = source.Placements.First(item => item.Id == placementId);
        var copy = CreateLayerCopy(composition, source);
        copy.Placements.Add(ClonePlacement(placement));
        composition.Layers.Insert(sourceIndex + 1, copy);
        return copy;
    }

    private static TimelineLayer CreateLayerCopy(TimelineComposition composition, TimelineLayer source) => new()
    {
        Name = CopyLayerName(composition, source.Name), Kind = source.Kind, IsVisible = source.IsVisible, IsMuted = source.IsMuted,
        MuteLeftChannel = source.MuteLeftChannel, MuteRightChannel = source.MuteRightChannel
    };

    private static string CopyLayerName(TimelineComposition composition, string sourceName)
    {
        var root = $"{sourceName} Copy"; var name = root; var suffix = 2;
        while (composition.Layers.Any(layer => layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{root} {suffix++}";
        return name;
    }

    private static TimelinePlacement ClonePlacement(TimelinePlacement source)
    {
        var copy = new TimelinePlacement { Clip = source.Clip, Start = source.Start, InPoint = source.InPoint, Length = source.Length };
        foreach (var keyframe in source.Keyframes) copy.Keyframes.Add(CloneKeyframe(keyframe));
        return copy;
    }

    private static GraphicsOverlay CloneGraphic(GraphicsOverlay source)
    {
        var copy = new GraphicsOverlay
        {
            Kind = source.Kind, Text = source.Text, ImagePath = source.ImagePath, RenderedImagePath = source.RenderedImagePath,
            FontFamily = source.FontFamily, FontSize = source.FontSize, Foreground = source.Foreground, Background = source.Background,
            FillColor1 = source.FillColor1, FillColor2 = source.FillColor2, UseSecondFillColor = source.UseSecondFillColor,
            GradientKind = source.GradientKind, GradientAngle = source.GradientAngle, Opacity = source.Opacity,
            PreserveAspectRatio = source.PreserveAspectRatio, X = source.X, Y = source.Y, Width = source.Width, Height = source.Height,
            Start = source.Start, Duration = source.Duration
        };
        foreach (var keyframe in source.Keyframes) copy.Keyframes.Add(CloneKeyframe(keyframe));
        return copy;
    }

    private static AnimationKeyframe CloneKeyframe(AnimationKeyframe source) => new()
    {
        Property = source.Property, Offset = source.Offset, Value = source.Value, Interpolation = source.Interpolation
    };

    public static (TimeSpan Start, TimeSpan End) PlaybackRange(TimelineComposition composition, Guid? layerId = null, Guid? placementId = null)
    {
        if (placementId is { } requestedPlacement)
        {
            var placement = composition.Layers.Where(layer => layerId is null || layer.Id == layerId).SelectMany(layer => layer.Placements).FirstOrDefault(item => item.Id == requestedPlacement);
            if (placement is not null) return (placement.Start, placement.End);
        }
        if (layerId is { } requestedLayer && composition.Layers.FirstOrDefault(layer => layer.Id == requestedLayer) is { Placements.Count: > 0 } layer)
            return (layer.Placements.Min(item => item.Start), layer.Placements.Max(item => item.End));
        return (TimeSpan.Zero, composition.OutputDuration);
    }

    public static TimeSpan SnapPlacementStart(TimelineComposition composition, Guid? movingPlacementId, TimeSpan proposedStart, TimeSpan duration, TimeSpan threshold)
    {
        proposedStart = proposedStart < TimeSpan.Zero ? TimeSpan.Zero : proposedStart;
        var candidates = new List<TimeSpan> { TimeSpan.Zero };
        foreach (var placement in composition.Layers.SelectMany(layer => layer.Placements))
        {
            if (placement.Id == movingPlacementId) continue;
            candidates.Add(placement.Start); candidates.Add(placement.End);
        }

        var best = proposedStart;
        var bestDistance = threshold + TimeSpan.FromTicks(1);
        foreach (var candidate in candidates)
        {
            foreach (var snappedStart in new[] { candidate, candidate - duration })
            {
                if (snappedStart < TimeSpan.Zero) continue;
                var distance = (snappedStart - proposedStart).Duration();
                if (distance <= threshold && distance < bestDistance) { best = snappedStart; bestDistance = distance; }
            }
        }
        return best;
    }

    public static PlacementDropAction PlaceWithinLayer(TimelineLayer layer, TimelinePlacement moving, TimeSpan proposedStart, TimeSpan originalStart)
    {
        proposedStart = proposedStart < TimeSpan.Zero ? TimeSpan.Zero : proposedStart;
        var proposedEnd = proposedStart + moving.Duration;
        var overlap = layer.Placements
            .Where(item => item.Id != moving.Id)
            .Select(item => new { Placement = item, Ticks = Math.Max(0, Math.Min(proposedEnd.Ticks, item.End.Ticks) - Math.Max(proposedStart.Ticks, item.Start.Ticks)) })
            .OrderByDescending(item => item.Ticks)
            .FirstOrDefault();
        if (overlap is null || overlap.Ticks <= 0)
        {
            moving.Start = proposedStart; SortPlacements(layer); return PlacementDropAction.Moved;
        }

        var fraction = overlap.Ticks / (double)Math.Max(1, moving.Duration.Ticks);
        var other = overlap.Placement;
        if (fraction <= .4)
        {
            var movingCenter = proposedStart + TimeSpan.FromTicks(moving.Duration.Ticks / 2);
            var otherCenter = other.Start + TimeSpan.FromTicks(other.Duration.Ticks / 2);
            if (movingCenter <= otherCenter)
            {
                moving.Start = other.Start > moving.Duration ? other.Start - moving.Duration : TimeSpan.Zero; SortPlacements(layer); return PlacementDropAction.SnappedBefore;
            }
            moving.Start = other.End; SortPlacements(layer); return PlacementDropAction.SnappedAfter;
        }
        if (fraction <= .6)
        {
            moving.Start = originalStart; SortPlacements(layer); return PlacementDropAction.Reverted;
        }

        var anchor = originalStart <= other.Start ? originalStart : other.Start;
        if (originalStart <= other.Start)
        {
            other.Start = anchor; moving.Start = anchor + other.Duration;
        }
        else
        {
            moving.Start = anchor; other.Start = anchor + moving.Duration;
        }
        SortPlacements(layer); return PlacementDropAction.Swapped;
    }

    public static IReadOnlyList<ActiveTimelinePlacement> ActivePlacementsAt(TimelineComposition composition, TimeSpan projectTime)
    {
        var active = new List<ActiveTimelinePlacement>();
        foreach (var layer in composition.Layers.Where(layer => layer.IsVisible))
        {
            foreach (var placement in layer.Placements.Where(item => projectTime >= item.Start && projectTime < item.End))
            {
                var clipTime = placement.InPoint + (projectTime - placement.Start);
                var source = SequenceToSource(placement.Clip.Segments, clipTime);
                if (source.SegmentIndex >= 0) active.Add(new(layer, placement, clipTime, source));
            }
        }
        return active;
    }

    private static void SortPlacements(TimelineLayer layer)
    {
        var sorted = layer.Placements.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray();
        layer.Placements.Clear(); foreach (var placement in sorted) layer.Placements.Add(placement);
    }

    public static SegmentPosition SequenceToSource(IReadOnlyList<MediaSegment> segments, TimeSpan sequenceTime)
    {
        if (segments.Count == 0) return new(-1, TimeSpan.Zero);
        var target = Clamp(sequenceTime, TimeSpan.Zero, TotalDuration(segments));
        var cursor = TimeSpan.Zero;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var end = cursor + segment.Duration;
            if (target < end || i == segments.Count - 1)
                return new(i, segment.SourceStart + Clamp(target - cursor, TimeSpan.Zero, segment.Duration));
            cursor = end;
        }
        return new(segments.Count - 1, segments[^1].SourceEnd);
    }

    public static TimeSpan SourceToSequence(IReadOnlyList<MediaSegment> segments, int segmentIndex, TimeSpan sourceTime)
    {
        if (segments.Count == 0) return TimeSpan.Zero;
        segmentIndex = Math.Clamp(segmentIndex, 0, segments.Count - 1);
        var before = TimeSpan.FromTicks(segments.Take(segmentIndex).Sum(s => s.Duration.Ticks));
        var segment = segments[segmentIndex];
        return before + Clamp(sourceTime - segment.SourceStart, TimeSpan.Zero, segment.Duration);
    }

    public static TimeSpan TotalDuration(IEnumerable<MediaSegment> segments) => TimeSpan.FromTicks(segments.Sum(s => Math.Max(0, s.Duration.Ticks)));

    public static bool SplitSelection(ClipItem clip, TimeSpan sequenceStart, TimeSpan sequenceEnd)
    {
        var total = clip.SelectedDuration;
        var start = Clamp(sequenceStart, TimeSpan.Zero, total);
        var end = Clamp(sequenceEnd, TimeSpan.Zero, total);
        if (end <= start) return false;

        var boundaries = new[] { start, end };
        var result = new List<MediaSegment>();
        var cursor = TimeSpan.Zero;
        foreach (var segment in clip.Segments)
        {
            var offsets = boundaries
                .Where(b => b > cursor && b < cursor + segment.Duration)
                .Select(b => b - cursor)
                .Distinct()
                .OrderBy(b => b)
                .ToList();
            offsets.Insert(0, TimeSpan.Zero);
            offsets.Add(segment.Duration);
            if (offsets.Count == 2) { result.Add(segment); cursor += segment.Duration; continue; }
            for (var i = 0; i < offsets.Count - 1; i++)
            {
                var sourceStart = segment.SourceStart + offsets[i];
                var sourceEnd = segment.SourceStart + offsets[i + 1];
                if (sourceEnd > sourceStart)
                    result.Add(new MediaSegment { Id = i == 0 ? segment.Id : Guid.NewGuid(), SourceStart = sourceStart, SourceEnd = sourceEnd });
            }
            cursor += segment.Duration;
        }
        if (result.Count == clip.Segments.Count) return false;
        clip.Segments.Clear();
        foreach (var segment in result) clip.Segments.Add(segment);
        return true;
    }

    public static bool Remove(ClipItem clip, Guid segmentId)
    {
        if (clip.Segments.Count <= 1) return false;
        var segment = clip.Segments.FirstOrDefault(s => s.Id == segmentId);
        return segment is not null && clip.Segments.Remove(segment);
    }

    public static bool Move(ClipItem clip, Guid segmentId, int targetIndex)
    {
        var oldIndex = clip.Segments.ToList().FindIndex(s => s.Id == segmentId);
        if (oldIndex < 0) return false;
        targetIndex = Math.Clamp(targetIndex, 0, clip.Segments.Count);
        if (targetIndex > oldIndex) targetIndex--;
        if (targetIndex == oldIndex) return false;
        var segment = clip.Segments[oldIndex];
        clip.Segments.RemoveAt(oldIndex);
        clip.Segments.Insert(Math.Clamp(targetIndex, 0, clip.Segments.Count), segment);
        return true;
    }

    public static bool Duplicate(ClipItem clip, Guid segmentId)
    {
        var index = clip.Segments.ToList().FindIndex(s => s.Id == segmentId);
        if (index < 0) return false;
        var source = clip.Segments[index];
        clip.Segments.Insert(index + 1, new MediaSegment { SourceStart = source.SourceStart, SourceEnd = source.SourceEnd });
        return true;
    }

    public static bool TrimSequenceStart(ClipItem clip, TimeSpan sequenceTime)
    {
        if (clip.Segments.Count == 0) return false;
        var position = SequenceToSource(clip.Segments, sequenceTime);
        if (position.SegmentIndex < 0) return false;
        for (var i = position.SegmentIndex - 1; i >= 0; i--) clip.Segments.RemoveAt(i);
        clip.Segments[0].SourceStart = position.SourceTime;
        RemoveEmpty(clip);
        return clip.Segments.Count > 0;
    }

    public static bool TrimSequenceEnd(ClipItem clip, TimeSpan sequenceTime)
    {
        if (clip.Segments.Count == 0) return false;
        var position = SequenceToSource(clip.Segments, sequenceTime);
        if (position.SegmentIndex < 0) return false;
        while (clip.Segments.Count > position.SegmentIndex + 1) clip.Segments.RemoveAt(clip.Segments.Count - 1);
        clip.Segments[^1].SourceEnd = position.SourceTime;
        RemoveEmpty(clip);
        return clip.Segments.Count > 0;
    }

    private static void RemoveEmpty(ClipItem clip)
    {
        for (var i = clip.Segments.Count - 1; i >= 0; i--)
            if (clip.Segments[i].Duration <= TimeSpan.Zero) clip.Segments.RemoveAt(i);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
