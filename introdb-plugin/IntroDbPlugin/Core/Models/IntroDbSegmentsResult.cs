// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Core.Models;

public sealed record IntroDbSegmentsResult(
    string ImdbId,
    int Season,
    int Episode,
    SegmentEntry? Intro,
    SegmentEntry? Recap,
    SegmentEntry? Outro);
